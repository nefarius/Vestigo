using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;

using CliWrap;
using CliWrap.Buffered;

using Vestigo.Configuration;
using Vestigo.Diagnostics;
using Vestigo.Security;

namespace Vestigo.Performance;

/// <summary>Runs allow-listed <c>wpr.exe</c> recordings with orphan cancel on startup.</summary>
public sealed class WindowsWprRecorder : IWprRecorder, IHostedService
{
    private readonly VestigoOptions _options;
    private readonly IVolumeSpace _volumes;
    private readonly ILogger<WindowsWprRecorder> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _commandLock = new();
    private TaskCompletionSource<bool>? _finish;
    private WprStatus _status = new("idle", null, null, null);

    public WindowsWprRecorder(VestigoOptions options, IVolumeSpace volumes, ILogger<WindowsWprRecorder> logger)
    {
        _options = options;
        _volumes = volumes;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Load() is { } recorded)
        {
            _ = Run(WprPlanner.CancelArguments(), CancellationToken.None);
            _logger.LogInformation("Cancelled orphan WPR recording {Profile}", recorded);
        }

        Save(null);
        return Task.CompletedTask;
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public OperationResult<string> PlanStart(string profile, string outputPath, TimeSpan duration)
    {
        if (!_options.Performance.AllowRawEtlCapture)
        {
            return OperationResult<string>.Fail("raw_etl_disabled", "WPR recording is disabled. Set Vestigo:Performance:AllowRawEtlCapture to true.");
        }

        if (WprPlanner.ValidateProfile(profile) is { } profileError)
        {
            return OperationResult<string>.Fail("invalid_request", profileError);
        }

        if (duration <= TimeSpan.Zero || duration > _options.Performance.MaxRawEtlDuration)
        {
            return OperationResult<string>.Fail(
                "invalid_request",
                $"Duration must be greater than zero and no more than {_options.Performance.MaxRawEtlDuration:c}.");
        }

        if (string.IsNullOrWhiteSpace(outputPath) || !outputPath.EndsWith(".etl", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<string>.Fail("invalid_request", "output must be a new .etl file in a root with Write access.");
        }

        return OperationResult<string>.Ok(WprPlanner.Canonical(profile));
    }

    public async Task<OperationResult<WprCopyOutcome>> RecordAsync(
        string profile,
        string outputPath,
        TimeSpan duration,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        OperationResult<string> planned = PlanStart(profile, outputPath, duration);
        if (!planned.Success)
        {
            return planned.FailAs<WprCopyOutcome>();
        }

        string directory = Path.GetDirectoryName(outputPath)!;
        long? free = _volumes.FreeBytes(directory);
        if (free is null)
        {
            return OperationResult<WprCopyOutcome>.Fail("disk_space_unknown", "Free space on the output volume could not be read.");
        }

        if (free.Value < maxBytes || free.Value - maxBytes < _options.Performance.MinFreeDiskBytes)
        {
            return OperationResult<WprCopyOutcome>.Fail("insufficient_disk_space", "The output volume does not have enough free space for the ETL file and the configured reserve.");
        }

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return OperationResult<WprCopyOutcome>.Fail("wpr_busy", "A WPR recording is already running.");
        }

        TaskCompletionSource<bool> finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            lock (_commandLock)
            {
                _finish = finish;
            }

            Save(planned.Value);
            _status = new WprStatus("running", planned.Value, outputPath, null);
            OperationResult<int> started = await Run(WprPlanner.StartArguments(planned.Value!), cancellationToken).ConfigureAwait(false);
            if (!started.Success)
            {
                _status = new WprStatus("failed", planned.Value, outputPath, started.Error);
                Save(null);
                return started.FailAs<WprCopyOutcome>();
            }

            bool stop = true;
            try
            {
                Task delay = Task.Delay(duration, cancellationToken);
                Task completed = await Task.WhenAny(delay, finish.Task).ConfigureAwait(false);
                if (completed == finish.Task)
                {
                    stop = finish.Task.Result;
                    if (stop)
                    {
                        _ = delay;
                    }
                }
                else
                {
                    await delay.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                stop = false;
                if (cancellationToken.IsCancellationRequested)
                {
                    _ = await Run(WprPlanner.CancelArguments(), CancellationToken.None).ConfigureAwait(false);
                    _status = new WprStatus("cancelled", planned.Value, outputPath, null);
                    Save(null);
                    throw;
                }
            }

            if (!stop)
            {
                _ = await Run(WprPlanner.CancelArguments(), CancellationToken.None).ConfigureAwait(false);
                _status = new WprStatus("cancelled", planned.Value, outputPath, null);
                Save(null);
                return OperationResult<WprCopyOutcome>.Fail("wpr_cancelled", "The WPR recording was cancelled.");
            }

            OperationResult<int> stopped = await Run(WprPlanner.StopArguments(outputPath), cancellationToken).ConfigureAwait(false);
            Save(null);
            if (!stopped.Success)
            {
                _status = new WprStatus("failed", planned.Value, outputPath, stopped.Error);
                return stopped.FailAs<WprCopyOutcome>();
            }

            if (!File.Exists(outputPath))
            {
                _status = new WprStatus("failed", planned.Value, outputPath, "WPR did not write an ETL file.");
                return OperationResult<WprCopyOutcome>.Fail("wpr_failed", "WPR did not write an ETL file.");
            }

            FileInfo info = new(outputPath);
            if (info.Length > maxBytes)
            {
                try
                {
                    File.Delete(outputPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return OperationResult<WprCopyOutcome>.Fail("etl_too_large", "The ETL file exceeded MaxRawEtlBytes and cleanup_failed.");
                }

                return OperationResult<WprCopyOutcome>.Fail("etl_too_large", "The ETL file is larger than Vestigo:Performance:MaxRawEtlBytes.");
            }

            await using FileStream stream = File.OpenRead(outputPath);
            string sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            _status = new WprStatus("idle", planned.Value, outputPath, null);
            return OperationResult<WprCopyOutcome>.Ok(new WprCopyOutcome(info.Length, sha));
        }
        finally
        {
            lock (_commandLock)
            {
                if (ReferenceEquals(_finish, finish))
                {
                    _finish = null;
                }
            }

            _gate.Release();
        }
    }

    public OperationResult<WprStatus> Status() => OperationResult<WprStatus>.Ok(_status);

    public Task<OperationResult<WprStatus>> StopRecordingAsync(CancellationToken cancellationToken)
    {
        lock (_commandLock)
        {
            if (_status.State != "running" || _finish is null)
            {
                return Task.FromResult(OperationResult<WprStatus>.Fail("wpr_not_running", "No WPR recording is running."));
            }

            _finish.TrySetResult(true);
        }

        return Task.FromResult(OperationResult<WprStatus>.Ok(_status));
    }

    public Task<OperationResult<WprStatus>> CancelRecordingAsync(CancellationToken cancellationToken)
    {
        lock (_commandLock)
        {
            if (_finish is null)
            {
                _ = Run(WprPlanner.CancelArguments(), CancellationToken.None);
                Save(null);
                _status = new WprStatus("cancelled", _status.Profile, _status.Output, null);
                return Task.FromResult(OperationResult<WprStatus>.Ok(_status));
            }

            _finish.TrySetResult(false);
        }

        return Task.FromResult(OperationResult<WprStatus>.Ok(_status));
    }

    private static async Task<OperationResult<int>> Run(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        string path = Path.Combine(Environment.SystemDirectory, "wpr.exe");
        if (!File.Exists(path))
        {
            return OperationResult<int>.Fail("wpr_not_found", "wpr.exe was not found in the Windows system directory.");
        }

        try
        {
            BufferedCommandResult result = await Cli.Wrap(path)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken)
                .ConfigureAwait(false);
            return result.ExitCode == 0
                ? OperationResult<int>.Ok(0)
                : OperationResult<int>.Fail("wpr_failed", $"wpr.exe exited {result.ExitCode}.");
        }
        catch (Win32Exception)
        {
            return OperationResult<int>.Fail("wpr_failed", "wpr.exe did not start.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private static string CheckpointPath => VestigoPaths.OwnedWprPath;

    private static string? Load()
    {
        try
        {
            return File.Exists(CheckpointPath) ? JsonSerializer.Deserialize<string>(File.ReadAllText(CheckpointPath)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Save(string? profile)
    {
        Directory.CreateDirectory(VestigoPaths.SettingsDirectory);
        if (profile is null)
        {
            if (File.Exists(CheckpointPath))
            {
                File.Delete(CheckpointPath);
            }

            return;
        }

        File.WriteAllText(CheckpointPath, JsonSerializer.Serialize(profile));
    }
}
