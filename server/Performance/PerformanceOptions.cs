namespace Vestigo.Performance;

/// <summary>Settings for the Performance module, bound from <c>Vestigo:Performance</c>.</summary>
public sealed class PerformanceOptions
{
    public const string SectionName = "Performance";

    /// <summary>Allows <c>capture_kernel_latency_summary</c>. The tool stays disabled until this is true.</summary>
    public bool AllowKernelTrace { get; set; }

    /// <summary>Allows <c>record_wpr_trace</c> and related WPR tools. They stay disabled until this is true.</summary>
    public bool AllowRawEtlCapture { get; set; }

    /// <summary>Allows <c>probe_storage_latency</c>. The tool stays disabled until this is true.</summary>
    public bool AllowStorageProbes { get; set; }

    /// <summary>Upper bound for one <c>sample_performance_counters</c> or <c>sample_process_metrics</c> window.</summary>
    public TimeSpan MaxSampleDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Smallest sampling interval accepted by counter and process-metric tools.</summary>
    public int MinSampleIntervalMilliseconds { get; set; } = 100;

    /// <summary>Upper bound for PDH counter paths expanded from a preset or explicit list.</summary>
    public int MaxCounterPaths { get; set; } = 64;

    /// <summary>Upper bound for one kernel ETW latency capture.</summary>
    public TimeSpan MaxKernelTraceDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound for kernel events processed in one capture or offline analysis.</summary>
    public int MaxKernelTraceEvents { get; set; } = 500_000;

    /// <summary>Upper bound for one WPR recording.</summary>
    public TimeSpan MaxRawEtlDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Maximum size of a recorded or analyzed <c>.etl</c> file.</summary>
    public long MaxRawEtlBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Free bytes that must remain on the output volume after reserving <see cref="MaxRawEtlBytes"/>.</summary>
    public long MinFreeDiskBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Maximum temporary file size for <c>probe_storage_latency</c>.</summary>
    public long MaxStorageProbeBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Upper bound for one storage latency probe.</summary>
    public TimeSpan MaxStorageProbeDuration { get; set; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (MaxSampleDuration < TimeSpan.FromMilliseconds(100) || MaxSampleDuration > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException("Vestigo:Performance:MaxSampleDuration must be between 00:00:00.100 and 00:05:00.");
        }

        if (MinSampleIntervalMilliseconds is < 50 or > 5000)
        {
            throw new InvalidOperationException("Vestigo:Performance:MinSampleIntervalMilliseconds must be between 50 and 5000.");
        }

        if (MaxCounterPaths is < 1 or > 256)
        {
            throw new InvalidOperationException("Vestigo:Performance:MaxCounterPaths must be between 1 and 256.");
        }

        if (MaxKernelTraceDuration < TimeSpan.FromSeconds(1) || MaxKernelTraceDuration > TimeSpan.FromMinutes(2))
        {
            throw new InvalidOperationException("Vestigo:Performance:MaxKernelTraceDuration must be between 00:00:01 and 00:02:00.");
        }

        if (MaxKernelTraceEvents is < 1000 or > 5_000_000)
        {
            throw new InvalidOperationException("Vestigo:Performance:MaxKernelTraceEvents must be between 1000 and 5000000.");
        }

        if (MaxRawEtlDuration < TimeSpan.FromSeconds(1) || MaxRawEtlDuration > TimeSpan.FromMinutes(10))
        {
            throw new InvalidOperationException("Vestigo:Performance:MaxRawEtlDuration must be between 00:00:01 and 00:10:00.");
        }

        if (MaxRawEtlBytes is < 16L * 1024 * 1024 or > 4L * 1024 * 1024 * 1024)
        {
            throw new InvalidOperationException("Vestigo:Performance:MaxRawEtlBytes must be between 16777216 and 4294967296.");
        }

        if (MinFreeDiskBytes is < 64L * 1024 * 1024 or > 16L * 1024 * 1024 * 1024)
        {
            throw new InvalidOperationException("Vestigo:Performance:MinFreeDiskBytes must be between 67108864 and 17179869184.");
        }

        if (MaxStorageProbeBytes is < 4096 or > 512L * 1024 * 1024)
        {
            throw new InvalidOperationException("Vestigo:Performance:MaxStorageProbeBytes must be between 4096 and 536870912.");
        }

        if (MaxStorageProbeDuration < TimeSpan.FromSeconds(1) || MaxStorageProbeDuration > TimeSpan.FromMinutes(2))
        {
            throw new InvalidOperationException("Vestigo:Performance:MaxStorageProbeDuration must be between 00:00:01 and 00:02:00.");
        }
    }
}
