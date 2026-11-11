using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Etws;

/// <summary>
///     Bounded read-only presentation trace. The session name starts with <c>Vestigo-</c> and is
///     stopped on completion, cancellation, failure, and the next service start.
/// </summary>
public sealed class PresentationObserver : IHostedService
{
    public const int MaxEvents = 2000;

    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(30);

    private readonly IRealtimeTraceSessionFactory _sessions;
    private readonly VestigoOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PresentationObserver> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PresentationObserver(
        IRealtimeTraceSessionFactory sessions,
        VestigoOptions options,
        TimeProvider time,
        ILogger<PresentationObserver> logger)
    {
        _sessions = sessions;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        List<string> retained = [];
        foreach (string name in Load())
        {
            if (!name.StartsWith(CaptureManager.SessionPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                _sessions.StopOrphan(name);
                _logger.LogInformation("Stopped orphan presentation session {SessionName}", name);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Could not stop orphan presentation session {SessionName}", name);
                retained.Add(name);
            }
        }

        Save(retained);
        return Task.CompletedTask;
    }

    private void Remember(string sessionName)
    {
        List<string> names = [.. Load()];
        if (!names.Contains(sessionName, StringComparer.Ordinal))
        {
            names.Add(sessionName);
        }

        Save(names);
    }

    private void Forget(string sessionName) =>
        Save(Load().Where(name => !string.Equals(name, sessionName, StringComparison.Ordinal)).ToList());

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<OperationResult<PresentationObservation>> ObserveAsync(
        int processId,
        long? windowHandle,
        TimeSpan duration,
        CancellationToken cancellationToken) =>
        ObserveAsync(processId, windowHandle, duration, cancellationToken, null, true);

    /// <param name="sessionName">Session selected by the parent. The worker must not invent another one.</param>
    /// <param name="manageCheckpoint">False when the parent process owns the checkpoint and orphan stop.</param>
    /// <param name="release">Set when the caller needs to know whether this process disposed the ETW session.</param>
    public async Task<OperationResult<PresentationObservation>> ObserveAsync(
        int processId,
        long? windowHandle,
        TimeSpan duration,
        CancellationToken cancellationToken,
        string? sessionName,
        bool manageCheckpoint,
        PresentationSessionRelease? release = null)
    {
        if (processId <= 0)
        {
            return OperationResult<PresentationObservation>.Fail("invalid_request", "Process id must be positive.");
        }

        if (windowHandle is <= 0)
        {
            return OperationResult<PresentationObservation>.Fail("invalid_request", "Window handle must be positive when supplied.");
        }

        TimeSpan cap = _options.MaxCaptureDuration < MaxDuration ? _options.MaxCaptureDuration : MaxDuration;
        if (duration <= TimeSpan.Zero || duration > cap)
        {
            return OperationResult<PresentationObservation>.Fail(
                "invalid_request",
                $"Duration must be greater than zero and no more than {cap:c}.");
        }

        if (!string.IsNullOrWhiteSpace(sessionName) && !PresentationWorker.IsOwnedSession(sessionName))
        {
            return OperationResult<PresentationObservation>.Fail("invalid_request", "Presentation session name is not owned by Vestigo.");
        }

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return OperationResult<PresentationObservation>.Fail(
                "presentation_busy",
                "A presentation observation is already running.");
        }

        IRealtimeTraceSession? session = null;
        bool sessionReleased = false;
        List<string> enabled = [];
        List<string> failed = [];
        List<PresentationSample> samples = [];
        int undecoded = 0;
        bool truncated = false;
        bool cancelled = false;
        bool eventsLost = false;
        string? readerFailure = null;
        DateTimeOffset started = _time.GetUtcNow();
        sessionName ??= CaptureManager.SessionPrefix + "P" + Guid.NewGuid().ToString("N")[..11];
        try
        {
            if (manageCheckpoint)
            {
                Remember(sessionName);
            }
            try
            {
                session = _sessions.Create(sessionName, _options);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Unknown(processId, windowHandle, sessionName, started, "session_unavailable", ex.Message);
            }

            foreach (Guid provider in PresentationProviders.All)
            {
                try
                {
                    session.EnableProvider(provider, TraceLevelSelection.Information, ulong.MaxValue, 0);
                    enabled.Add(provider.ToString("D"));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failed.Add(provider.ToString("D"));
                    _logger.LogWarning(ex, "Presentation provider {Provider} could not be enabled", provider);
                }
            }

            using CancellationTokenSource durationSource = new(duration);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, durationSource.Token);
            try
            {
                await foreach (ReadOnlyMemory<byte> item in session.ReadEventsAsync(null, false, static () => { }, linked.Token))
                {
                    if (PresentationEventParser.TryParse(item.Span, out PresentationSample sample))
                    {
                        samples.Add(sample);
                    }
                    else
                    {
                        undecoded++;
                    }

                    if (samples.Count + undecoded >= MaxEvents)
                    {
                        truncated = true;
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = cancellationToken.IsCancellationRequested;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                readerFailure = ex.GetType().Name + ": " + ex.Message;
                _logger.LogWarning(ex, "Presentation decoding failed in session {SessionName}", sessionName);
            }
        }
        finally
        {
            try
            {
                bool releaseCheckpoint = session is null;
                if (session is not null)
                {
                    try
                    {
                        eventsLost = session.EventsWereLost;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        _logger.LogWarning(ex, "Could not read loss counters for presentation session {SessionName}", sessionName);
                        eventsLost = true;
                    }

                    await session.DisposeAsync().ConfigureAwait(false);
                    releaseCheckpoint = true;
                }

                sessionReleased = releaseCheckpoint;
                if (manageCheckpoint && releaseCheckpoint)
                {
                    Forget(sessionName);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                sessionReleased = false;
                _logger.LogWarning(ex, "Presentation session {SessionName} cleanup failed", sessionName);
            }
            finally
            {
                if (release is not null)
                {
                    release.Released = sessionReleased;
                }

                _gate.Release();
            }
        }

        if (readerFailure is not null)
        {
            return Unknown(processId, windowHandle, sessionName, started, "decode_failed", readerFailure);
        }

        return OperationResult<PresentationObservation>.Ok(PresentationClassifier.Classify(
            samples,
            processId,
            windowHandle,
            enabled,
            failed,
            started,
            _time.GetUtcNow(),
            sessionName,
            cancelled,
            truncated,
            undecoded,
            eventsLost));
    }

    private OperationResult<PresentationObservation> Unknown(
        int processId,
        long? windowHandle,
        string sessionName,
        DateTimeOffset started,
        string reason,
        string detail)
    {
        return OperationResult<PresentationObservation>.Ok(new PresentationObservation(
            PresentationProviders.Unknown,
            reason,
            "etw",
            started,
            _time.GetUtcNow(),
            processId,
            windowHandle,
            0,
            0,
            [],
            [],
            sessionName,
            detail));
    }

    private static IReadOnlyList<string> Load()
    {
        if (!File.Exists(VestigoPaths.OwnedPresentationPath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(VestigoPaths.OwnedPresentationPath)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void Save(IReadOnlyList<string> names)
    {
        StateDirectories.EnsureSubdirectory(VestigoPaths.SettingsDirectory);
        string tempPath = Path.Combine(VestigoPaths.SettingsDirectory, $".owned-presentation.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, JsonSerializer.Serialize(names));
        if (File.Exists(VestigoPaths.OwnedPresentationPath))
        {
            File.Replace(tempPath, VestigoPaths.OwnedPresentationPath, null);
        }
        else
        {
            File.Move(tempPath, VestigoPaths.OwnedPresentationPath);
        }
    }
}

/// <summary>Set by <see cref="PresentationObserver"/> after it attempts to dispose the ETW session.</summary>
public sealed class PresentationSessionRelease
{
    public bool Released { get; set; }
}
