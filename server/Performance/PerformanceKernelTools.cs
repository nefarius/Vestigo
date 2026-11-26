using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Performance;

/// <summary>Bounded kernel ETW summaries and offline ETL analysis.</summary>
[McpServerToolType]
public sealed class PerformanceKernelTools(
    IKernelLatencyCapture capture,
    IEtlPerformanceAnalyzer analyzer,
    RootPolicy roots,
    VestigoOptions options)
{
    [McpServerTool(Name = "capture_kernel_latency_summary", ReadOnly = true)]
    [Description(
        "Runs a bounded kernel ETW capture in an isolated Vestigo-Perf-* worker and returns structured DiskIO, FileIO, "
        + "DPC/ISR, scheduler, and hard-fault summaries. Requires Vestigo:Performance:AllowKernelTrace. "
        + "Does not return raw events.")]
    public Task<OperationResult<KernelLatencySummary>> CaptureKernelLatencySummary(
        [Description("Focus groups: disk_io, file_io, dpc_isr, scheduler, hard_faults. Empty selects all.")] string[]? groups = null,
        [Description("Capture window in seconds.")] int durationSeconds = 10,
        CancellationToken cancellationToken = default)
    {
        if (!options.Performance.AllowKernelTrace)
        {
            return Task.FromResult(OperationResult<KernelLatencySummary>.Fail(
                "kernel_trace_disabled",
                "Kernel ETW capture is disabled. Set Vestigo:Performance:AllowKernelTrace to true."));
        }

        int maxSeconds = (int)options.Performance.MaxKernelTraceDuration.TotalSeconds;
        if (durationSeconds < 1 || durationSeconds > maxSeconds)
        {
            return Task.FromResult(OperationResult<KernelLatencySummary>.Fail(
                "invalid_request",
                $"durationSeconds must be between 1 and {maxSeconds}."));
        }

        return capture.CaptureAsync(
            groups ?? [],
            TimeSpan.FromSeconds(durationSeconds),
            options.Performance.MaxKernelTraceEvents,
            cancellationToken);
    }

    [McpServerTool(Name = "analyze_etl_performance", ReadOnly = true)]
    [Description(
        "Analyzes an existing .etl file inside a root with Read access using TraceEvent and returns the same structured "
        + "kernel latency summary as capture_kernel_latency_summary. The file must be no larger than MaxRawEtlBytes.")]
    public OperationResult<KernelLatencySummary> AnalyzeEtlPerformance(
        [Description("Path of a .etl file in a root with Read access.")] string path,
        [Description("Focus groups: disk_io, file_io, dpc_isr, scheduler, hard_faults. Empty selects all.")] string[]? groups = null)
    {
        PathCheck located = roots.ResolveExisting(path, RootAccess.Read);
        if (!located.Success)
        {
            return OperationResult<KernelLatencySummary>.Fail(located.ErrorCode!, located.Error!);
        }

        return analyzer.Analyze(located.FullPath!, groups ?? [], options.Performance.MaxKernelTraceEvents);
    }
}
