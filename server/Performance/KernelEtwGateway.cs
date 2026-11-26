using System.Diagnostics;
using System.Text.Json;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Performance;

/// <summary>Launches <see cref="KernelEtwWorker"/>, tracks Vestigo-Perf-* sessions, and restores orphans.</summary>
public sealed class KernelEtwGateway : IKernelLatencyCapture, IHostedService
{
    private readonly VestigoOptions _options;
    private readonly ILogger<KernelEtwGateway> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public KernelEtwGateway(VestigoOptions options, ILogger<KernelEtwGateway> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (string name in Load())
        {
            StopSession(name);
        }

        Save([]);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<OperationResult<KernelLatencySummary>> CaptureAsync(
        IReadOnlyList<string> groups,
        TimeSpan duration,
        int maxEvents,
        CancellationToken cancellationToken)
    {
        if (!_options.Performance.AllowKernelTrace)
        {
            return OperationResult<KernelLatencySummary>.Fail(
                "kernel_trace_disabled",
                "Kernel ETW capture is disabled. Set Vestigo:Performance:AllowKernelTrace to true.");
        }

        OperationResult<IReadOnlyList<string>> parsed = KernelTraceGroups.Parse(groups);
        if (!parsed.Success)
        {
            return parsed.FailAs<KernelLatencySummary>();
        }

        string sessionName = KernelEtwWorker.SessionPrefix + Guid.NewGuid().ToString("N")[..12];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Remember(sessionName);
            string self = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Vestigo.exe");
            ProcessStartInfo start = new()
            {
                FileName = self,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(KernelEtwWorker.Flag);
            start.ArgumentList.Add("capture");
            start.ArgumentList.Add(sessionName);
            start.ArgumentList.Add(((int)duration.TotalMilliseconds).ToString());
            start.ArgumentList.Add(maxEvents.ToString());
            start.ArgumentList.Add(string.Join(',', parsed.Value!));
            using Process process = new() { StartInfo = start };
            if (!process.Start())
            {
                return OperationResult<KernelLatencySummary>.Fail("kernel_trace_failed", "The kernel ETW worker did not start.");
            }

            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            Forget(sessionName);
            if (string.IsNullOrWhiteSpace(output))
            {
                return OperationResult<KernelLatencySummary>.Fail("kernel_trace_failed", "The kernel ETW worker returned no result.");
            }

            string line = output.Trim().Split('\n').Last().Trim();
            OperationResult<KernelLatencySummary>? parsedResult = JsonSerializer.Deserialize<OperationResult<KernelLatencySummary>>(
                line,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsedResult ?? OperationResult<KernelLatencySummary>.Fail("kernel_trace_failed", "The kernel ETW worker result could not be parsed.");
        }
        catch (OperationCanceledException)
        {
            StopSession(sessionName);
            Forget(sessionName);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StopSession(sessionName);
            Forget(sessionName);
            return OperationResult<KernelLatencySummary>.Fail("kernel_trace_failed", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _gate.Release();
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

    private void Forget(string sessionName) =>
        Save(Load().Where(name => !string.Equals(name, sessionName, StringComparison.Ordinal)).ToList());

    private void StopSession(string sessionName)
    {
        try
        {
            string self = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Vestigo.exe");
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = self,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    ArgumentList = { KernelEtwWorker.Flag, "stop", sessionName }
                }
            };
            _ = process.Start();
            process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Could not stop orphan kernel performance session {SessionName}", sessionName);
        }
    }

    private static string CheckpointPath => VestigoPaths.OwnedPerformanceEtwPath;

    private static IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(CheckpointPath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(CheckpointPath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Save(IReadOnlyList<string> names)
    {
        Directory.CreateDirectory(VestigoPaths.SettingsDirectory);
        File.WriteAllText(CheckpointPath, JsonSerializer.Serialize(names));
    }
}
