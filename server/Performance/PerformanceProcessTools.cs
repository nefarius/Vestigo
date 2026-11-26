using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;

namespace Vestigo.Performance;

/// <summary>Process resource deltas and Wait Chain Traversal.</summary>
[McpServerToolType]
public sealed class PerformanceProcessTools(
    IProcessMetricSource processes,
    IThreadInspector threads,
    VestigoOptions options)
{
    [McpServerTool(Name = "sample_process_metrics", ReadOnly = true)]
    [Description(
        "Takes a bounded delta snapshot of process CPU, disk/I/O bytes and ops, page faults, working set, private bytes, "
        + "handle/thread/GDI/USER counts, priority class, and EcoQoS/Efficiency Mode. PID 0 and 4 are omitted.")]
    public Task<OperationResult<ProcessMetricResult>> SampleProcessMetrics(
        [Description("Sampling window in seconds.")] int durationSeconds = 2,
        [Description("Optional process id. Omit to rank the busiest processes.")] int? pid = null,
        [Description("Optional case-insensitive process-name substring.")] string? name = null,
        [Description("Maximum processes to return.")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (PerformanceBounds.SampleWindow(durationSeconds, options.Performance.MinSampleIntervalMilliseconds, options.Performance) is not { } window)
        {
            return Task.FromResult(OperationResult<ProcessMetricResult>.Fail(
                "invalid_request",
                $"durationSeconds must be at least 1 and no more than {(int)options.Performance.MaxSampleDuration.TotalSeconds}."));
        }

        if (pid is < 0)
        {
            return Task.FromResult(OperationResult<ProcessMetricResult>.Fail("invalid_request", "pid cannot be negative."));
        }

        return processes.SampleAsync(window.Duration, pid, name, Math.Clamp(limit, 1, 200), cancellationToken);
    }

    [McpServerTool(Name = "inspect_process_threads", ReadOnly = true)]
    [Description(
        "Returns per-thread CPU, priority, state, kernel wait reason, and Wait Chain Traversal nodes for one pid. "
        + "PID 0, PID 4, the Vestigo service, and other protected processes are refused.")]
    public OperationResult<ThreadInspection> InspectProcessThreads(
        [Description("Target process id.")] int pid,
        [Description("Optional process start time used to reject pid reuse.")] DateTimeOffset? expectedStartTime = null) =>
        threads.Inspect(pid, expectedStartTime);
}
