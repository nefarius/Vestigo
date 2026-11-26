using System.ComponentModel;
using System.Text.Json;

using CliWrap;
using CliWrap.Buffered;

using Vestigo.Configuration;

namespace Vestigo.Performance;

/// <summary>Runs allow-listed drop-only <c>pktmon.exe</c> captures with orphan stop on startup.</summary>
public sealed class WindowsPktmonDropRecorder : IPacketDropCapture, IHostedService
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);

    private readonly VestigoOptions _options;
    private readonly ILogger<WindowsPktmonDropRecorder> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsPktmonDropRecorder(VestigoOptions options, ILogger<WindowsPktmonDropRecorder> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        PktmonCheckpoint? recorded = Load();
        if (recorded is { NativeStarted: true })
        {
            if (!await StopOwnedAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("Could not stop orphan pktmon drop capture");
                return;
            }

            _logger.LogInformation("Stopped orphan pktmon drop capture");
        }

        _ = TrySave(null);
        TryDeleteDirectory();
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public OperationResult<string> PlanStart(TimeSpan duration)
    {
        if (!_options.Performance.AllowPacketDropCapture)
        {
            return OperationResult<string>.Fail(
                "packet_drop_disabled",
                "Packet-drop capture is disabled. Set Vestigo:Performance:AllowPacketDropCapture to true.");
        }

        if (duration < TimeSpan.FromSeconds(1) || duration > _options.Performance.MaxPacketDropDuration)
        {
            return OperationResult<string>.Fail(
                "invalid_request",
                $"Duration must be at least 00:00:01 and no more than {_options.Performance.MaxPacketDropDuration:c}.");
        }

        return OperationResult<string>.Ok("drop-only");
    }

    public async Task<OperationResult<PacketDropResult>> CaptureAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        OperationResult<string> planned = PlanStart(duration);
        if (!planned.Success)
        {
            return planned.FailAs<PacketDropResult>();
        }

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return OperationResult<PacketDropResult>.Fail("pktmon_busy", "A packet-drop capture is already running.");
        }

        string directory = VestigoPaths.PacketDropDirectory;
        string etl = Path.Combine(directory, $"vestigo-drop-{Guid.NewGuid():N}.etl");
        string text = Path.ChangeExtension(etl, ".txt");
        IReadOnlyList<string> start = PktmonPlanner.StartArguments(etl);
        if (!PktmonPlanner.IsDropOnlyStart(start))
        {
            _gate.Release();
            return OperationResult<PacketDropResult>.Fail("pktmon_failed", "The pktmon start argument list was not drop-only metadata.");
        }

        bool nativeStarted = false;
        bool stopped = false;
        try
        {
            PktmonCheckpoint? previous = Load();
            if (previous is { NativeStarted: true })
            {
                if (!await StopOwnedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return OperationResult<PacketDropResult>.Fail(
                        "pktmon_busy",
                        "A previous Vestigo-owned pktmon capture could not be stopped.");
                }

                TryDeleteCapture(previous.EtlPath);
                if (!TrySave(null))
                {
                    return OperationResult<PacketDropResult>.Fail(
                        "pktmon_checkpoint_failed",
                        "The previous pktmon ownership checkpoint could not be cleared.");
                }
            }

            Directory.CreateDirectory(directory);
            if (!TrySave(new PktmonCheckpoint(true, etl)))
            {
                return OperationResult<PacketDropResult>.Fail("pktmon_checkpoint_failed", "The pktmon ownership checkpoint could not be saved.");
            }

            nativeStarted = true;
            NativeResult started = await Run(start, cancellationToken).ConfigureAwait(false);
            if (!started.Succeeded)
            {
                if (PktmonPlanner.IndicatesSessionInUse(started.Output))
                {
                    nativeStarted = false;
                    if (!TrySave(null) && !TrySave(new PktmonCheckpoint(false, null)))
                    {
                        return OperationResult<PacketDropResult>.Fail(
                            "pktmon_checkpoint_failed",
                            "The pktmon ownership checkpoint could not be cleared after a rejected start.");
                    }

                    return FailNative<PacketDropResult>(started);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                stopped = await StopOwnedAsync().ConfigureAwait(false);
                if (stopped)
                {
                    _ = TrySave(null);
                    nativeStarted = false;
                }

                return FailNative<PacketDropResult>(started);
            }

            NativeResult counters = NativeResult.Missing();
            IReadOnlyList<PacketDropCounter> parsed = [];
            try
            {
                await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
                counters = await Run(PktmonPlanner.CountersArguments(), cancellationToken).ConfigureAwait(false);
                parsed = PktmonPlanner.ParseCounters(counters.Output);
            }
            finally
            {
                stopped = await StopOwnedAsync().ConfigureAwait(false);
            }
            IReadOnlyList<PacketDropCounter> reasons = [];
            bool partial = !counters.Succeeded || !stopped;
            if (File.Exists(etl))
            {
                NativeResult formatted = await Run(PktmonPlanner.FormatArguments(etl, text), cancellationToken).ConfigureAwait(false);
                if (formatted.Succeeded && File.Exists(text))
                {
                    reasons = PktmonPlanner.ParseDropReasons(await File.ReadAllTextAsync(text, cancellationToken).ConfigureAwait(false));
                }
                else
                {
                    partial = true;
                }
            }

            if (stopped)
            {
                _ = TrySave(null);
            }

            return OperationResult<PacketDropResult>.Ok(PktmonPlanner.Combine(
                (int)duration.TotalMilliseconds,
                parsed,
                reasons,
                truncated: false,
                partial,
                partial ? "pktmon counters, format, or stop returned a non-zero exit." : null));
        }
        catch (OperationCanceledException)
        {
            if (nativeStarted && !stopped)
            {
                stopped = await StopOwnedAsync().ConfigureAwait(false);
            }

            if (stopped)
            {
                _ = TrySave(null);
            }

            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (nativeStarted && !stopped)
            {
                stopped = await StopOwnedAsync().ConfigureAwait(false);
            }

            if (stopped)
            {
                _ = TrySave(null);
            }

            return OperationResult<PacketDropResult>.Fail("pktmon_failed", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (stopped || !nativeStarted)
            {
                TryDelete(etl);
                TryDelete(text);
            }

            _gate.Release();
        }
    }

    private async Task<bool> StopOwnedAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource cleanup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cleanup.CancelAfter(CleanupTimeout);
        NativeResult result;
        try
        {
            result = await Run(PktmonPlanner.StopArguments(), cleanup.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("pktmon stop timed out after {Timeout}", CleanupTimeout);
            return false;
        }

        if (!Stopped(result))
        {
            _logger.LogWarning("pktmon stop returned {Exit}: {Output}", result.ExitCode, result.Output);
            return false;
        }

        NativeResult status;
        try
        {
            status = await Run(PktmonPlanner.StatusArguments(), cleanup.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("pktmon status timed out after stop");
            return false;
        }

        if (PktmonPlanner.IndicatesActive(status.Output))
        {
            _logger.LogWarning("pktmon is still active after stop: {Output}", status.Output);
            return false;
        }

        if (!PktmonPlanner.ConfirmsIdle(status.Succeeded, status.Output))
        {
            _logger.LogWarning("pktmon status after stop was unconfirmed: {Exit} {Output}", status.ExitCode, status.Output);
            return false;
        }

        return true;
    }

    private static async Task<NativeResult> Run(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        string path = Path.Combine(Environment.SystemDirectory, "pktmon.exe");
        if (!File.Exists(path))
        {
            return NativeResult.Missing();
        }

        try
        {
            BufferedCommandResult result = await Cli.Wrap(path)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken)
                .ConfigureAwait(false);
            string output = string.Join('\n', new[] { result.StandardOutput, result.StandardError }
                .Where(item => !string.IsNullOrWhiteSpace(item)));
            return new NativeResult(result.ExitCode, output, "pktmon_failed");
        }
        catch (Win32Exception)
        {
            return NativeResult.Missing();
        }
    }

    private static bool Stopped(NativeResult result) =>
        result.Succeeded || PktmonPlanner.IndicatesIdle(result.Output);

    private static OperationResult<T> FailNative<T>(NativeResult result) =>
        OperationResult<T>.Fail(result.ErrorCode, string.IsNullOrWhiteSpace(result.Output) ? $"pktmon.exe exited {result.ExitCode}." : result.Output.Trim());

    private static PktmonCheckpoint? Load()
    {
        try
        {
            if (!File.Exists(VestigoPaths.OwnedPktmonPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<PktmonCheckpoint>(File.ReadAllText(VestigoPaths.OwnedPktmonPath));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool TrySave(PktmonCheckpoint? checkpoint)
    {
        try
        {
            Directory.CreateDirectory(VestigoPaths.SettingsDirectory);
            if (checkpoint is null)
            {
                if (File.Exists(VestigoPaths.OwnedPktmonPath))
                {
                    File.Delete(VestigoPaths.OwnedPktmonPath);
                }

                return true;
            }

            File.WriteAllText(VestigoPaths.OwnedPktmonPath, JsonSerializer.Serialize(checkpoint));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not update the pktmon ownership checkpoint");
            return false;
        }
    }

    private static void TryDeleteCapture(string? etl)
    {
        if (string.IsNullOrWhiteSpace(etl))
        {
            return;
        }

        TryDelete(etl);
        TryDelete(Path.ChangeExtension(etl, ".txt"));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory()
    {
        try
        {
            if (Directory.Exists(VestigoPaths.PacketDropDirectory))
            {
                foreach (string file in Directory.GetFiles(VestigoPaths.PacketDropDirectory, "vestigo-drop-*"))
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct NativeResult(int ExitCode, string Output, string ErrorCode)
    {
        public bool Succeeded => ExitCode == 0;

        public static NativeResult Missing() =>
            new(-1, "pktmon.exe was not found in the Windows system directory.", "pktmon_not_found");
    }

    private sealed record PktmonCheckpoint(bool NativeStarted, string? EtlPath);
}
