using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Etws;

/// <summary>
///     Owns presentation checkpoints and runs decoding in a child process. The child is the only process
///     that calls the ETW deserializer for this tool.
/// </summary>
public sealed class PresentationGateway : IHostedService
{
    private readonly IPresentationProcess _process;
    private readonly VestigoOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PresentationGateway> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _shutdown;

    public PresentationGateway(
        IPresentationProcess process,
        VestigoOptions options,
        TimeProvider time,
        ILogger<PresentationGateway> logger)
    {
        _process = process;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _shutdown = new CancellationTokenSource();
        List<string> retained = [];
        foreach (string name in Load())
        {
            if (!PresentationWorker.IsOwnedSession(name))
            {
                continue;
            }

            if (!await StopOwnedAsync(name, CancellationToken.None).ConfigureAwait(false))
            {
                retained.Add(name);
            }
        }

        Save(retained);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_shutdown is not null)
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _gate.Release();
    }

    public async Task<OperationResult<PresentationObservation>> ObserveAsync(
        int processId,
        long? windowHandle,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        TimeSpan cap = _options.MaxCaptureDuration < PresentationObserver.MaxDuration
            ? _options.MaxCaptureDuration
            : PresentationObserver.MaxDuration;
        if (processId <= 0 || windowHandle is <= 0 || duration <= TimeSpan.Zero || duration > cap)
        {
            return OperationResult<PresentationObservation>.Fail(
                "invalid_request",
                "Process id, window handle, and duration must describe one bounded observation.");
        }

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return OperationResult<PresentationObservation>.Fail("presentation_busy", "A presentation observation is already running.");
        }

        string sessionName = CaptureManager.SessionPrefix + "P" + Guid.NewGuid().ToString("N")[..11];
        DateTimeOffset started = _time.GetUtcNow();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown?.Token ?? CancellationToken.None);
        try
        {
            Remember(sessionName);
            PresentationProcessResult run = await _process.RunAsync(
                [PresentationWorker.Flag, "observe", sessionName, processId.ToString(), (windowHandle ?? 0).ToString(), ((int)duration.TotalMilliseconds).ToString()],
                duration + TimeSpan.FromSeconds(15),
                linked.Token).ConfigureAwait(false);
            if (run.Cancelled || run.TimedOut)
            {
                bool stopped = await StopOwnedAsync(sessionName, CancellationToken.None).ConfigureAwait(false);
                if (stopped)
                {
                    Forget(sessionName);
                }

                return Unknown(
                    processId,
                    windowHandle,
                    sessionName,
                    started,
                    run.Cancelled ? "cancelled" : "worker_timeout",
                    run.Cancelled
                        ? "The observation was cancelled and the decoder process was stopped."
                        : "The decoder process exceeded the observation deadline and was stopped.");
            }

            if (!TryParse(run.Output, out OperationResult<PresentationObservation>? parsed) || parsed is null)
            {
                bool stopped = await StopOwnedAsync(sessionName, CancellationToken.None).ConfigureAwait(false);
                if (stopped)
                {
                    Forget(sessionName);
                }

                return Unknown(
                    processId,
                    windowHandle,
                    sessionName,
                    started,
                    "decoder_terminated",
                    "The decoder process ended without a result. ExitCode=" + (run.ExitCode is int exit ? "0x" + exit.ToString("X8") : "none") + ".");
            }

            if (parsed.Success)
            {
                Forget(sessionName);
                return parsed;
            }

            bool cleaned = await StopOwnedAsync(sessionName, CancellationToken.None).ConfigureAwait(false);
            if (cleaned)
            {
                Forget(sessionName);
            }

            return parsed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> StopOwnedAsync(string sessionName, CancellationToken cancellationToken)
    {
        try
        {
            PresentationProcessResult run = await _process.RunAsync(
                [PresentationWorker.Flag, "stop", sessionName],
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            if (run.ExitCode == 0)
            {
                return true;
            }

            _logger.LogWarning("Presentation session {SessionName} could not be stopped. ExitCode={ExitCode}", sessionName, run.ExitCode);
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Presentation session {SessionName} could not be stopped", sessionName);
            return false;
        }
    }

    private OperationResult<PresentationObservation> Unknown(
        int processId,
        long? windowHandle,
        string sessionName,
        DateTimeOffset started,
        string reason,
        string detail) =>
        OperationResult<PresentationObservation>.Ok(new PresentationObservation(
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

    private static bool TryParse(string output, out OperationResult<PresentationObservation>? result)
    {
        result = null;
        string? line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(candidate => candidate.StartsWith('{'));
        if (line is null)
        {
            return false;
        }

        try
        {
            result = JsonSerializer.Deserialize<OperationResult<PresentationObservation>>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return result is not null;
        }
        catch (JsonException)
        {
            return false;
        }
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

    private void Remember(string sessionName)
    {
        List<string> names = [.. Load()];
        if (!names.Contains(sessionName, StringComparer.Ordinal))
        {
            names.Add(sessionName);
        }

        Save(names);
    }

    private static void Forget(string sessionName) =>
        Save(Load().Where(name => !string.Equals(name, sessionName, StringComparison.Ordinal)).ToList());

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
