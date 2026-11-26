namespace Vestigo.Performance;

public interface ICounterSampler
{
    Task<OperationResult<CounterSampleResult>> SampleAsync(
        IReadOnlyList<string> paths,
        TimeSpan duration,
        TimeSpan interval,
        int maxPaths,
        string preset,
        CancellationToken cancellationToken);
}

public interface IStorageTopologySource
{
    OperationResult<StorageTopology> Read();
}

public interface IMinifilterSource
{
    OperationResult<MinifilterList> List();
}

public interface IVmEnvironmentSource
{
    OperationResult<VmEnvironment> Read();
}

public interface ISchedulerJitterProbe
{
    Task<OperationResult<JitterProbeResult>> ProbeAsync(TimeSpan duration, CancellationToken cancellationToken);
}

public interface IProcessMetricSource
{
    Task<OperationResult<ProcessMetricResult>> SampleAsync(
        TimeSpan duration,
        int? pid,
        string? nameFilter,
        int limit,
        CancellationToken cancellationToken);
}

public interface IThreadInspector
{
    OperationResult<ThreadInspection> Inspect(int pid, DateTimeOffset? expectedStartTime);
}

public interface IMemoryPressureSource
{
    OperationResult<MemoryPressure> Read(bool includePoolTags, int poolTagLimit);
}

public interface IKernelLatencyCapture
{
    Task<OperationResult<KernelLatencySummary>> CaptureAsync(
        IReadOnlyList<string> groups,
        TimeSpan duration,
        int maxEvents,
        CancellationToken cancellationToken);
}

public interface IEtlPerformanceAnalyzer
{
    OperationResult<KernelLatencySummary> Analyze(string fullPath, IReadOnlyList<string> groups, int maxEvents);
}

public interface IWprRecorder
{
    OperationResult<string> PlanStart(string profile, string outputPath, TimeSpan duration);
    Task<OperationResult<WprCopyOutcome>> RecordAsync(string profile, string outputPath, TimeSpan duration, long maxBytes, CancellationToken cancellationToken);
    OperationResult<WprStatus> Status();
    Task<OperationResult<WprStatus>> StopRecordingAsync(CancellationToken cancellationToken);
    Task<OperationResult<WprStatus>> CancelRecordingAsync(CancellationToken cancellationToken);
}

public sealed record WprCopyOutcome(long Bytes, string Sha256);

public sealed record WprStatus(string State, string? Profile, string? Output, string? Error);

public interface IStorageLatencyProbe
{
    Task<OperationResult<StorageProbeResult>> ProbeAsync(
        string directory,
        long fileBytes,
        TimeSpan duration,
        CancellationToken cancellationToken);
}
