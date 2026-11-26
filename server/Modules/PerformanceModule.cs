using Microsoft.Extensions.DependencyInjection.Extensions;

using Vestigo.Configuration;
using Vestigo.Diagnostics;
using Vestigo.Performance;

namespace Vestigo.Modules;

/// <summary>Opt-in machine and VM performance analysis. Kernel ETW, WPR, and storage probes are separate gates.</summary>
public sealed class PerformanceModule : VestigoModule
{
    public override string Name => "Performance";

    public override string Description =>
        "Sample performance counters, storage topology, filesystem minifilters, VM environment, scheduler jitter, process metrics, wait chains, and memory pressure. Opt-in. Needs Jobs. Kernel ETW summaries, raw WPR ETL recording, and storage latency probes are separate gates.";

    public override bool EnabledByDefault => false;

    public override IReadOnlyList<string> Dependencies { get; } = ["Jobs"];

    public override IReadOnlyList<Type> ToolTypes { get; } =
    [
        typeof(PerformanceCounterTools),
        typeof(PerformanceInventoryTools),
        typeof(PerformanceProcessTools),
        typeof(PerformanceKernelTools),
        typeof(PerformanceWprTools),
        typeof(PerformanceStorageProbeTools)
    ];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.TryAddSingleton<IVolumeSpace, DriveVolumeSpace>();
        services.AddSingleton<ICounterSampler, WindowsPdhSampler>();
        services.AddSingleton<IStorageTopologySource, WindowsStorageTopology>();
        services.AddSingleton<IMinifilterSource, WindowsMinifilterSource>();
        services.AddSingleton<IVmEnvironmentSource, WindowsVmEnvironment>();
        services.AddSingleton<ISchedulerJitterProbe, WindowsSchedulerJitterProbe>();
        services.AddSingleton<IProcessMetricSource, WindowsProcessMetrics>();
        services.AddSingleton<IThreadInspector, WindowsThreadInspector>();
        services.AddSingleton<IMemoryPressureSource, WindowsMemoryPressure>();
        services.AddSingleton<KernelEtwGateway>();
        services.AddSingleton<IKernelLatencyCapture>(provider => provider.GetRequiredService<KernelEtwGateway>());
        services.AddHostedService(provider => provider.GetRequiredService<KernelEtwGateway>());
        services.AddSingleton<IEtlPerformanceAnalyzer, WindowsEtlPerformanceAnalyzer>();
        services.AddSingleton<WindowsWprRecorder>();
        services.AddSingleton<IWprRecorder>(provider => provider.GetRequiredService<WindowsWprRecorder>());
        services.AddHostedService(provider => provider.GetRequiredService<WindowsWprRecorder>());
        services.AddSingleton<IStorageLatencyProbe, WindowsStorageLatencyProbe>();
    }
}
