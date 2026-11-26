using Vestigo.Files;
using Vestigo.Jobs;

namespace Vestigo.Performance;

public sealed record CounterSample(
    string Path,
    PercentileStats Values,
    string? Status,
    string? Error);

public sealed record CounterSampleResult(
    string Preset,
    int DurationMilliseconds,
    int IntervalMilliseconds,
    int CounterCount,
    bool Truncated,
    IReadOnlyList<CounterSample> Counters);

public sealed record StorageReliability(
    long? ReadErrors,
    long? WriteErrors,
    long? FlushLatencyMaxMs,
    long? TemperatureCelsius);

public sealed record PhysicalDiskInfo(
    string? ObjectId,
    string? FriendlyName,
    string? SerialNumber,
    string? MediaType,
    string? BusType,
    string? HealthStatus,
    string? OperationalStatus,
    long? SizeBytes,
    int? LogicalSectorBytes,
    int? PhysicalSectorBytes,
    bool? FourKNative,
    bool? FiveTwelveE,
    StorageReliability? Reliability);

public sealed record DiskVolumeInfo(
    string? ObjectId,
    string? Path,
    string? FileSystem,
    long? SizeBytes,
    long? SizeRemainingBytes,
    string? HealthStatus);

public sealed record DiskInfo(
    string? ObjectId,
    int? Number,
    string? PartitionStyle,
    bool? IsBoot,
    bool? IsSystem,
    string? FriendlyName,
    string? AdapterDriver,
    IReadOnlyList<DiskVolumeInfo> Volumes);

public sealed record StorageTopology(
    IReadOnlyList<PhysicalDiskInfo> PhysicalDisks,
    IReadOnlyList<DiskInfo> Disks,
    string? Error);

public sealed record MinifilterInstance(string Name, string VolumeName, string Altitude);

public sealed record MinifilterInfo(
    string Name,
    string? Altitude,
    uint FrameId,
    IReadOnlyList<MinifilterInstance> Instances);

public sealed record MinifilterList(IReadOnlyList<MinifilterInfo> Filters);

public sealed record ProcessorTopology(int LogicalProcessors, int Cores, int Packages, int NumaNodes);

public sealed record ParavirtDevice(string Kind, string? Name, string? Service, bool Present);

public sealed record TimerResolution(uint CurrentHundredNs, uint MinimumHundredNs, uint MaximumHundredNs);

public sealed record VmEnvironment(
    bool HypervisorPresent,
    string? Vendor,
    string? Manufacturer,
    string? Model,
    string? BiosManufacturer,
    ProcessorTopology Topology,
    IReadOnlyList<ParavirtDevice> Devices,
    TimerResolution? Timer,
    bool? HvciEnabled,
    bool? VbsEnabled,
    IReadOnlyList<string> Notes);

public sealed record JitterProbeResult(
    int DurationMilliseconds,
    int SampleCount,
    PercentileStats WakeDriftMs,
    PercentileStats SpinGapMs,
    int SpikesOver2Ms,
    int SpikesOver10Ms,
    int SpikesOver50Ms);

public sealed record ProcessMetricSample(
    int Pid,
    string Name,
    DateTimeOffset? StartTime,
    double CpuPercent,
    double IoReadBytesPerSec,
    double IoWriteBytesPerSec,
    double IoOtherBytesPerSec,
    long IoReadOps,
    long IoWriteOps,
    long IoOtherOps,
    long WorkingSetBytes,
    long PrivateBytes,
    long PageFaultDelta,
    int HandleCount,
    int ThreadCount,
    int? GdiCount,
    int? UserCount,
    string? PriorityClass,
    bool? EcoQos,
    string? Error);

public sealed record ProcessMetricResult(
    int DurationMilliseconds,
    IReadOnlyList<ProcessMetricSample> Processes,
    bool Truncated);

public sealed record WaitChainNode(string ObjectType, string? Name, int? ProcessId, int? ThreadId, string? Status);

public sealed record ThreadSample(
    int ThreadId,
    double CpuMilliseconds,
    int Priority,
    string? WaitReason,
    string? State,
    bool Deadlock,
    IReadOnlyList<WaitChainNode> WaitChain,
    string? Error);

public sealed record ThreadInspection(int Pid, string Name, IReadOnlyList<ThreadSample> Threads);

public sealed record PagefileInfo(string? Name, long AllocatedBytes, long CurrentUsageBytes, long PeakUsageBytes);

public sealed record PoolTagInfo(string Tag, long PagedBytes, long NonPagedBytes, long Allocations);

public sealed record MemoryPressure(
    long TotalPhysicalBytes,
    long AvailablePhysicalBytes,
    long CommitTotalBytes,
    long CommitLimitBytes,
    long KernelPagedBytes,
    long KernelNonPagedBytes,
    long? CompressionWorkingSetBytes,
    long? BalloonBytes,
    IReadOnlyList<PagefileInfo> Pagefiles,
    IReadOnlyList<PoolTagInfo>? PoolTags);

public sealed record DiskIoSummary(
    string Disk,
    int Count,
    PercentileStats ServiceMs,
    IReadOnlyList<SlowIo> Slowest);

public sealed record ProcessIoSummary(int Pid, string? Name, int Count, PercentileStats ServiceMs);

public sealed record SlowIo(
    string Kind,
    int Pid,
    string? ProcessName,
    string? Path,
    string? Disk,
    long Bytes,
    long Offset,
    double ElapsedMs);

public sealed record FileIoSummary(string Path, string Kind, int Count, PercentileStats ElapsedMs);

public sealed record DriverDpcSummary(string Image, int Count, double TotalMs, double MaxMs, PercentileStats DurationMs);

public sealed record SchedulerProcessSummary(
    int Pid,
    string? Name,
    double CpuMs,
    double BlockedMs,
    double WaitRatio,
    IReadOnlyList<WaitReasonCount> WaitReasons);

public sealed record WaitReasonCount(string Reason, int Count, double BlockedMs);

public sealed record ReadyDelaySummary(PercentileStats DelayMs);

public sealed record HardFaultSummary(int Pid, string? Name, string? File, int Count);

public sealed record KernelLatencySummary(
    string SessionName,
    int DurationMilliseconds,
    IReadOnlyList<string> Groups,
    int EventCount,
    bool Truncated,
    bool EventsLost,
    bool BuffersLost,
    IReadOnlyList<DiskIoSummary> DiskIo,
    IReadOnlyList<ProcessIoSummary> DiskIoByProcess,
    IReadOnlyList<SlowIo> SlowestDiskIo,
    IReadOnlyList<FileIoSummary> FileIo,
    IReadOnlyList<SlowIo> SlowestFileIo,
    IReadOnlyList<DriverDpcSummary> Dpc,
    IReadOnlyList<DriverDpcSummary> Isr,
    IReadOnlyList<SchedulerProcessSummary> Scheduler,
    ReadyDelaySummary? ReadyDelay,
    IReadOnlyList<HardFaultSummary> HardFaults,
    string? Error);

public sealed record WprTraceResult(
    JobInfo Job,
    string? Output,
    long? Bytes,
    string? Sha256,
    FileLink? Download,
    string? OutputTail,
    bool Running);

public sealed record StorageProbeModeResult(
    string Mode,
    int Operations,
    double Iops,
    double MegabytesPerSecond,
    PercentileStats LatencyMs);

public sealed record StorageProbeResult(
    string Directory,
    bool CleanedUp,
    IReadOnlyList<StorageProbeModeResult> Modes);
