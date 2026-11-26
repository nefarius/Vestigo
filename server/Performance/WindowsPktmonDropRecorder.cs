using System.ComponentModel;
using System.Text.Json;

using CliWrap;
using CliWrap.Buffered;

using Vestigo.Configuration;

namespace Vestigo.Performance;

/// <summary>Runs allow-listed drop-only <c>pktmon.exe</c> captures with orphan stop on startup.</summary>
public sealed class WindowsPktmonDropRecorder : IPacketDropCapture, IHostedService
{
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
            NativeResult stopped = await Run(PktmonPlanner.StopArguments(), cancellationToken).ConfigureAwait(false);
            if (stopped.Succeeded || IndicatesIdle(stopped))
            {
                _logger.LogInformation("Stopped orphan pktmon drop capture");
            }
            else
            {
                _logger.LogWarning("Could not stop orphan pktmon drop capture: {Output}", stopped.Output);
            }
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
        try
        {
            Directory.CreateDirectory(directory);
            if (!TrySave(new PktmonCheckpoint(true, etl)))
            {
                return OperationResult<PacketDropResult>.Fail("pktmon_checkpoint_failed", "The pktmon ownership checkpoint could not be saved.");
            }

            NativeResult started = await Run(PktmonPlanner.StartArguments(etl), cancellationToken).ConfigureAwait(false);
            if (!started.Succeeded)
            {
                _ = TrySave(null);
                return FailNative<PacketDropResult>(started);
            }

            try
            {
                await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                NativeResult stopped = await Run(PktmonPlanner.StopArguments(), CancellationToken.None).ConfigureAwait(false);
                if (!stopped.Succeeded && !IndicatesIdle(stopped))
                {
                    _logger.LogWarning("pktmon stop returned {Exit}: {Output}", stopped.ExitCode, stopped.Output);
                }
            }

            NativeResult counters = await Run(PktmonPlanner.CountersArguments(), cancellationToken).ConfigureAwait(false);
            IReadOnlyList<PacketDropCounter> parsed = PktmonPlanner.ParseCounters(counters.Output);
            IReadOnlyList<PacketDropCounter> reasons = [];
            bool partial = !counters.Succeeded;
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

            _ = TrySave(null);
            return OperationResult<PacketDropResult>.Ok(PktmonPlanner.Combine(
                (int)duration.TotalMilliseconds,
                parsed,
                reasons,
                truncated: false,
                partial,
                partial ? "pktmon counters or format returned a non-zero exit." : null));
        }
        catch (OperationCanceledException)
        {
            _ = await Run(PktmonPlanner.StopArguments(), CancellationToken.None).ConfigureAwait(false);
            _ = TrySave(null);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ = await Run(PktmonPlanner.StopArguments(), CancellationToken.None).ConfigureAwait(false);
            _ = TrySave(null);
            return OperationResult<PacketDropResult>.Fail("pktmon_failed", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TryDelete(etl);
            TryDelete(text);
            _gate.Release();
        }
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

    private static bool IndicatesIdle(NativeResult result) =>
        result.Output.Contains("not running", StringComparison.OrdinalIgnoreCase)
        || result.Output.Contains("is not started", StringComparison.OrdinalIgnoreCase)
        || result.Output.Contains("No packet monitor", StringComparison.OrdinalIgnoreCase);

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
