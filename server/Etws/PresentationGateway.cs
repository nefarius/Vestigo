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
            PresentationProcessResult run;
            try
            {
                run = await _process.RunAsync(
                    [
                        PresentationWorker.Flag,
                        "observe",
                        sessionName,
                        processId.ToString(),
                        (windowHandle ?? 0).ToString(),
                        ((int)duration.TotalMilliseconds).ToString(),
                        _options.BufferSizeKb.ToString(),
                        _options.FlushTimerSeconds.ToString(),
                        _options.ClockResolution
                    ],
                    duration + TimeSpan.FromSeconds(15),
                    linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Presentation worker {SessionName} could not be run", sessionName);
                await StopIfCreatedAsync(sessionName, started: true, terminationUncertain: true).ConfigureAwait(false);
                return Unknown(
                    processId,
                    windowHandle,
                    sessionName,
                    started,
                    "worker_host_failed",
                    "The service could not start or read the decoder process. " + ex.GetType().Name);
            }

            if (run.HostFailed && !run.Started)
            {
                Forget(sessionName);
                return Unknown(
                    processId,
                    windowHandle,
                    sessionName,
                    started,
                    "worker_host_failed",
                    "The decoder process could not be started.");
            }

            if (run.HostFailed || run.TerminationUncertain || run.Cancelled || run.TimedOut)
            {
                bool sessionStopped = await StopIfCreatedAsync(sessionName, run.Started, run.TerminationUncertain).ConfigureAwait(false);
                string reason = run.Cancelled ? "cancelled" : run.TimedOut ? "worker_timeout" : "worker_host_failed";
                return Unknown(
                    processId,
                    windowHandle,
                    sessionName,
                    started,
                    reason,
                    TerminationDetail(run, sessionStopped));
            }

            if (!TryParse(run.Output, out OperationResult<PresentationObservation>? parsed) || parsed is null)
            {
                await StopIfCreatedAsync(sessionName, started: true, terminationUncertain: false).ConfigureAwait(false);
                WorkerProgress? progress = ReadProgress(run.Output);
                return Unknown(
                    processId,
                    windowHandle,
                    sessionName,
                    started,
                    "decoder_terminated",
                    DescribeTermination(run, progress),
                    progress?.ProvidersEnabled,
                    progress?.ProvidersFailed);
            }

            if (parsed.Success && run.ExitCode == 0)
            {
                Forget(sessionName);
                return parsed;
            }

            await StopIfCreatedAsync(sessionName, started: true, terminationUncertain: false).ConfigureAwait(false);
            if (parsed.Success)
            {
                return Unknown(
                    processId,
                    windowHandle,
                    sessionName,
                    started,
                    "decoder_terminated",
                    "The decoder process exited before the session release was confirmed. ExitCode=" + FormatExit(run.ExitCode) + ".");
            }

            return parsed;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal sealed record WorkerProgress(string Stage, IReadOnlyList<string> ProvidersEnabled, IReadOnlyList<string> ProvidersFailed);

    /// <summary>
    ///     Facts only: the exit code, the last stage the worker reported, and a bounded, control-character-free tail of its stderr.
    ///     Provider outcomes come from the worker's own progress line; none are inferred.
    /// </summary>
    internal static string DescribeTermination(PresentationProcessResult run, WorkerProgress? progress)
    {
        System.Text.StringBuilder detail = new("The decoder process ended without a result. ExitCode=" + FormatExit(run.ExitCode) + ".");
        detail.Append(progress is null
            ? " The worker did not report a stage; the fault happened before providers were enabled or the session could not be created."
            : $" Last reported stage: {progress.Stage}. Providers enabled={progress.ProvidersEnabled.Count}, failed={progress.ProvidersFailed.Count}.");
        if (!string.IsNullOrWhiteSpace(run.ErrorTail))
        {
            string tail = new(run.ErrorTail.Where(character => !char.IsControl(character) || character is '\n' or '\t').ToArray());
            tail = string.Join(" | ", tail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (tail.Length > 600)
            {
                tail = tail[..600] + "...";
            }

            detail.Append(" Worker stderr (untrusted text): ").Append(tail);
        }

        return detail.ToString();
    }

    private static string FormatExit(int? exitCode) => exitCode switch
    {
        null => "none",
        unchecked((int)0xC0000005) => "0xC0000005 (access violation)",
        { } code => "0x" + code.ToString("X8")
    };

    internal static WorkerProgress? ReadProgress(string output)
    {
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith('{') || !line.Contains("\"progress\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("progress", out JsonElement progress)
                    && progress.TryGetProperty("providersEnabled", out JsonElement enabled)
                    && progress.TryGetProperty("providersFailed", out JsonElement failed))
                {
                    string stage = progress.TryGetProperty("stage", out JsonElement stageElement) && stageElement.ValueKind == JsonValueKind.String
                        ? stageElement.GetString()!
                        : "decoding";
                    return new WorkerProgress(stage, Guids(enabled), Guids(failed));
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static List<string> Guids(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(item => item.GetString()).Where(text => Guid.TryParse(text, out _)).Select(text => text!).Take(16).ToList()
            : [];

    private static string TerminationDetail(PresentationProcessResult run, bool sessionStopped)
    {
        if (run.TerminationUncertain && run.Cancelled)
        {
            return "The observation was cancelled. Termination was attempted, but cleanup is unconfirmed.";
        }

        if (run.TerminationUncertain && run.TimedOut)
        {
            return "The decoder process exceeded the observation deadline. Termination was attempted, but cleanup is unconfirmed.";
        }

        if (!sessionStopped && (run.Cancelled || run.TimedOut))
        {
            string lead = run.Cancelled
                ? "The observation was cancelled and the decoder process was stopped."
                : "The decoder process exceeded the observation deadline and was stopped.";
            return lead + " Cleanup is unconfirmed and the ETW session may remain active.";
        }

        if (run.Cancelled)
        {
            return "The observation was cancelled and the decoder process was stopped.";
        }

        if (run.TimedOut)
        {
            return "The decoder process exceeded the observation deadline and was stopped.";
        }

        return sessionStopped
            ? "The service could not read the decoder process."
            : "The service could not read the decoder process. Cleanup is unconfirmed and the ETW session may remain active.";
    }

    private async Task<bool> StopIfCreatedAsync(string sessionName, bool started, bool terminationUncertain)
    {
        if (!started)
        {
            Forget(sessionName);
            return true;
        }

        bool stopped = await StopOwnedAsync(sessionName, CancellationToken.None).ConfigureAwait(false);
        if (stopped && !terminationUncertain)
        {
            Forget(sessionName);
            return true;
        }

        return false;
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
        string detail,
        IReadOnlyList<string>? enabled = null,
        IReadOnlyList<string>? failed = null) =>
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
            enabled ?? [],
            failed ?? [],
            sessionName,
            detail));

    private static bool TryParse(string output, out OperationResult<PresentationObservation>? result)
    {
        result = null;
        string? line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(candidate => candidate.StartsWith('{') && !candidate.StartsWith("{\"progress\"", StringComparison.Ordinal));
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
