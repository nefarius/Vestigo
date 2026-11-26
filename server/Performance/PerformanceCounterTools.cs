using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;

namespace Vestigo.Performance;

/// <summary>English PDH counter sampling and scheduler jitter.</summary>
[McpServerToolType]
public sealed class PerformanceCounterTools(
    ICounterSampler counters,
    ISchedulerJitterProbe jitter,
    VestigoOptions options)
{
    [McpServerTool(Name = "sample_performance_counters", ReadOnly = true)]
    [Description(
        "Samples English PDH counters over a bounded window and returns min/avg/p50/p95/p99/max per counter. "
        + "preset is overview, storage, cpu, memory, network, network_rss, or hypervisor. paths may add extra English PDH paths. "
        + "Duration is clamped by Vestigo:Performance:MaxSampleDuration. Interval is at least MinSampleIntervalMilliseconds.")]
    public Task<OperationResult<CounterSampleResult>> SamplePerformanceCounters(
        [Description("Curated preset: overview, storage, cpu, memory, network, network_rss, or hypervisor.")] string preset = "overview",
        [Description("Optional extra English PDH paths, for example \\\\Processor(_Total)\\\\% DPC Time.")] string[]? paths = null,
        [Description("Sampling window in seconds.")] int durationSeconds = 5,
        [Description("Sampling interval in milliseconds.")] int intervalMs = 1000,
        CancellationToken cancellationToken = default)
    {
        if (!CounterPresets.IsKnown(preset))
        {
            return Task.FromResult(OperationResult<CounterSampleResult>.Fail(
                "invalid_request",
                $"Unknown counter preset '{preset}'. Use {string.Join(", ", CounterPresets.Names)}."));
        }

        if (PerformanceBounds.SampleWindow(durationSeconds, intervalMs, options.Performance) is not { } window)
        {
            return Task.FromResult(OperationResult<CounterSampleResult>.Fail(
                "invalid_request",
                $"durationSeconds must be at least 1 and no more than {(int)options.Performance.MaxSampleDuration.TotalSeconds}, and intervalMs must be at least {options.Performance.MinSampleIntervalMilliseconds}."));
        }

        List<string> requested = [.. CounterPresets.Paths[preset]];
        if (paths is not null)
        {
            requested.AddRange(paths);
        }

        return counters.SampleAsync(requested, window.Duration, window.Interval, options.Performance.MaxCounterPaths, preset, cancellationToken);
    }

    [McpServerTool(Name = "probe_scheduler_jitter", ReadOnly = true)]
    [Description(
        "Measures timer wake-drift and tight-spin preemption gaps with QueryPerformanceCounter. "
        + "Returns latency percentiles and spike counts over 2 ms, 10 ms, and 50 ms. Duration is clamped by MaxSampleDuration.")]
    public Task<OperationResult<JitterProbeResult>> ProbeSchedulerJitter(
        [Description("Probe window in seconds.")] int durationSeconds = 5,
        CancellationToken cancellationToken = default)
    {
        if (PerformanceBounds.SampleWindow(durationSeconds, options.Performance.MinSampleIntervalMilliseconds, options.Performance) is not { } window)
        {
            return Task.FromResult(OperationResult<JitterProbeResult>.Fail(
                "invalid_request",
                $"durationSeconds must be at least 1 and no more than {(int)options.Performance.MaxSampleDuration.TotalSeconds}."));
        }

        return jitter.ProbeAsync(window.Duration, cancellationToken);
    }
}

internal static class PerformanceBounds
{
    public static (TimeSpan Duration, TimeSpan Interval)? SampleWindow(int durationSeconds, int intervalMs, PerformanceOptions options)
    {
        if (durationSeconds < 1 || durationSeconds > (int)options.MaxSampleDuration.TotalSeconds)
        {
            return null;
        }

        if (intervalMs < options.MinSampleIntervalMilliseconds || intervalMs > 5000)
        {
            return null;
        }

        return (TimeSpan.FromSeconds(durationSeconds), TimeSpan.FromMilliseconds(intervalMs));
    }
}
