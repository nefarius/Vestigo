using System.Text.Json.Nodes;

using Bunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using MudBlazor.Services;

using Vestigo.Configuration;
using Vestigo.Diagnostics;
using Vestigo.Files;
using Vestigo.Hosting;
using Vestigo.Hosting.Components;
using Vestigo.Installer.Actions.Logic;
using Vestigo.Jobs;
using Vestigo.Modules;
using Vestigo.Performance;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

[Collection("VestigoHost")]
public sealed class PerformanceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vestigo-perf-").FullName;
    private readonly string _probeRoot = Directory.CreateTempSubdirectory("vestigo-perf-out-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");

    public PerformanceTests() => Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _previous);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        try
        {
            Directory.Delete(_probeRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Percentiles_UseNearestRankInterpolation()
    {
        PercentileStats empty = PercentileStats.From([]);
        Assert.Equal(0, empty.Count);
        PercentileStats one = PercentileStats.From([10]);
        Assert.Equal(10, one.P50);
        Assert.Equal(10, one.P99);
        PercentileStats stats = PercentileStats.From([1, 2, 3, 4, 5]);
        Assert.Equal(1, stats.Min);
        Assert.Equal(3, stats.P50);
        Assert.Equal(5, stats.Max);
        Assert.Equal(3, stats.Average);
    }

    [Fact]
    public void CounterPath_RejectsBlankControlCharactersAndMissingSlash()
    {
        Assert.NotNull(PdhCounterPath.Validate(null));
        Assert.NotNull(PdhCounterPath.Validate("Processor Time"));
        Assert.NotNull(PdhCounterPath.Validate("\\Processor\nTime"));
        Assert.Null(PdhCounterPath.Validate(@"\Processor(_Total)\% Processor Time"));
    }

    [Fact]
    public void Options_RejectOutOfRangeBounds()
    {
        PerformanceOptions options = new();
        options.Validate();
        options.MaxSampleDuration = TimeSpan.FromMilliseconds(1);
        Assert.Throws<InvalidOperationException>(options.Validate);
        options = new() { MaxStorageProbeDuration = TimeSpan.FromMilliseconds(500) };
        Assert.Throws<InvalidOperationException>(options.Validate);
        options = new() { MaxCounterPaths = 0 };
        Assert.Throws<InvalidOperationException>(options.Validate);
        options = new() { MaxRawEtlBytes = 1 };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void PresetsAndKernelGroups_AreAllowListed()
    {
        Assert.True(CounterPresets.IsKnown("overview"));
        Assert.Contains(@"\PhysicalDisk(_Total)\Avg. Disk sec/Read", CounterPresets.Paths["overview"]);
        Assert.True(CounterPresets.IsKnown("network_rss"));
        Assert.False(CounterPresets.IsKnown("custom"));
        Assert.Equal("invalid_request", KernelTraceGroups.Parse(["nope"]).ErrorCode);
        Assert.Equal(KernelTraceGroups.All, KernelTraceGroups.Parse([]).Value);
        Assert.Equal(["disk_io", "dpc_isr"], KernelTraceGroups.Parse(["DISK_IO", "dpc_isr"]).Value);
    }

    [Fact]
    public void Aggregator_SummarizesSyntheticKernelEvents()
    {
        KernelLatencyAggregator aggregator = new();
        aggregator.AddDiskIo("0", 100, "app", "read", @"C:\data.bin", 4096, 0, 12.5);
        aggregator.AddDiskIo("0", 100, "app", "write", @"C:\data.bin", 4096, 4096, 40);
        aggregator.AddFileIo(@"C:\data.bin", "create", 100, "app", 0, 0, 8);
        aggregator.AddDpc(0x1000, 1.5);
        aggregator.NoteImage(0x1000, 0x1000, @"C:\Windows\System32\drivers\disk.sys");
        aggregator.AddDpc(0x1000, 3);
        aggregator.AddCSwitch(100, 4, "app", "System", "WrFilterContext", 20, 5);
        aggregator.AddReadyDelay(7);
        aggregator.AddHardFault(100, "app", @"C:\page.sys");
        KernelLatencySummary summary = aggregator.Build("Vestigo-Perf-abc", 1000, KernelTraceGroups.All, null);
        Assert.False(summary.Truncated);
        Assert.Equal(12.5, summary.DiskIo[0].ServiceMs.Min);
        Assert.Equal(40, summary.SlowestDiskIo[0].ElapsedMs);
        Assert.Equal("0", summary.SlowestDiskIo[0].Disk);
        Assert.Contains(summary.FileIo, item => item.Kind == "create");
        Assert.Equal("disk.sys", summary.Dpc[0].Image);
        SchedulerProcessSummary incoming = Assert.Single(summary.Scheduler, item => item.Pid == 4);
        Assert.Contains(incoming.WaitReasons, item => item.Reason == "WrFilterContext");
        Assert.Equal(20, incoming.BlockedMs);
        Assert.Equal(5, Assert.Single(summary.Scheduler, item => item.Pid == 100).CpuMs);
        Assert.Equal(7, summary.ReadyDelay!.DelayMs.Max);
        Assert.Equal(1, summary.HardFaults[0].Count);
    }

    [Fact]
    public void Aggregator_StopsAtTheEventLimitAndKeepsImageBounds()
    {
        KernelLatencyAggregator limited = new(1);
        limited.AddDiskIo("0", 1, "a", "read", @"C:\a", 1, 0, 1);
        limited.AddDiskIo("0", 1, "a", "read", @"C:\a", 1, 0, 9);
        KernelLatencySummary truncated = limited.Build("s", 1, KernelTraceGroups.All, null);
        Assert.True(truncated.Truncated);
        Assert.Equal(1, truncated.EventCount);
        Assert.Equal(1, truncated.DiskIo[0].ServiceMs.Max);

        KernelLatencyAggregator sourceBudget = new(1);
        Assert.True(sourceBudget.Observe());
        Assert.True(sourceBudget.Truncated);
        Assert.False(sourceBudget.Observe());
        sourceBudget.NoteProcess(2, "dropped");
        sourceBudget.AddDiskIo("0", 2, "dropped", "read", @"C:\a", 1, 0, 9);
        KernelLatencySummary observed = sourceBudget.Build("s", 1, KernelTraceGroups.All, null);
        Assert.True(observed.Truncated);
        Assert.Equal(1, observed.EventCount);
        Assert.Empty(observed.DiskIo);

        KernelLatencyAggregator ready = new(1);
        ready.AddCSwitch(100, 4, "app", "System", "WrFilterContext", 20, 5);
        ready.AddReadyDelay(7);
        KernelLatencySummary oneSwitch = ready.Build("s", 1, KernelTraceGroups.All, null);
        Assert.True(oneSwitch.Truncated);
        Assert.Equal(1, oneSwitch.EventCount);
        Assert.Equal(7, oneSwitch.ReadyDelay!.DelayMs.Max);

        KernelLatencyAggregator images = new();
        images.NoteImage(0x1000, 0x100, @"C:\Windows\System32\drivers\disk.sys");
        images.AddDpc(0x2000, 5);
        images.AddDpc(0x1050, 2);
        KernelLatencySummary named = images.Build("s", 1, KernelTraceGroups.All, null);
        Assert.Contains(named.Dpc, item => item.Image == "disk.sys" && item.MaxMs == 2);
        Assert.Contains(named.Dpc, item => item.Image == "unknown" && item.MaxMs == 5);
    }

    [Fact]
    public void Aggregator_ExcludesIdleRanksSchedulerByCpuAndTracksPerProcessReadyDelay()
    {
        KernelLatencyAggregator aggregator = new();
        aggregator.AddCSwitch(0, 10, "Idle", "busy", "Executive", 5, 9000);
        aggregator.AddCSwitch(10, 0, "busy", "Idle", "Executive", 9000, 500);
        aggregator.AddReadyDelay(0, "Idle", 500);
        aggregator.AddReadyDelay(10, "busy", 1);
        aggregator.AddReadyDelay(10, "busy", 3);

        aggregator.AddCSwitch(20, 30, "tied-high-ready", "tied-low-ready", "UserRequest", 5000, 100);
        aggregator.AddCSwitch(30, 20, "tied-low-ready", "tied-high-ready", "UserRequest", 10, 100);
        aggregator.AddReadyDelay(20, "tied-high-ready", 2);
        aggregator.AddReadyDelay(20, "tied-high-ready", 25);
        aggregator.AddReadyDelay(30, "tied-low-ready", 2);
        aggregator.AddReadyDelay(30, "tied-low-ready", 4);

        aggregator.AddCSwitch(40, 50, "tied-no-ready-high-blocked", "tied-no-ready-low-blocked", "DelayExecution", 50, 100);
        aggregator.AddCSwitch(50, 40, "tied-no-ready-low-blocked", "tied-no-ready-high-blocked", "DelayExecution", 9000, 100);

        aggregator.AddCSwitch(60, 10, "starved", "busy", "Executive", 0, 1);
        aggregator.AddCSwitch(10, 60, "busy", "starved", "WrQueue", 2000, 0);
        aggregator.AddReadyDelay(60, "starved", 10);
        aggregator.AddReadyDelay(60, "starved", 120);

        KernelLatencySummary summary = aggregator.Build("Vestigo-Perf-rank", 1000, KernelTraceGroups.All, null);

        Assert.DoesNotContain(summary.Scheduler, item => item.Pid == 0);
        Assert.Equal([10, 20, 30, 40, 50, 60], summary.Scheduler.Select(item => item.Pid).ToArray());

        SchedulerProcessSummary busy = summary.Scheduler[0];
        Assert.Equal(500, busy.CpuMs);
        Assert.NotNull(busy.ReadyDelayMs);
        Assert.Equal(2, busy.ReadyDelayMs!.Count);
        Assert.Equal(3, busy.ReadyDelayMs.Max);

        SchedulerProcessSummary highReady = summary.Scheduler[1];
        Assert.Equal(25, highReady.ReadyDelayMs!.Max);

        SchedulerProcessSummary noReady = summary.Scheduler[3];
        Assert.Null(noReady.ReadyDelayMs);

        Assert.NotNull(summary.ReadyDelay);
        Assert.Equal(120, summary.ReadyDelay!.DelayMs.Max);
        Assert.DoesNotContain(summary.ReadyDelay.ByProcess, item => item.Pid == 0);
        Assert.Equal([60, 20, 30, 10], summary.ReadyDelay.ByProcess.Select(item => item.Pid).ToArray());
        Assert.Equal("starved", summary.ReadyDelay.ByProcess[0].Name);
        Assert.Equal(120, summary.ReadyDelay.ByProcess[0].DelayMs.Max);
        Assert.Equal(25, summary.ReadyDelay.ByProcess[1].DelayMs.Max);
    }

    [Fact]
    public void PdhSampler_ClassifyStatus_MapsUnsupportedNoDataAndError()
    {
        Assert.Equal("unsupported", WindowsPdhSampler.ClassifyStatus(WindowsPdhSampler.PdhCstatusNoObject, "add").Status);
        Assert.Equal("unsupported", WindowsPdhSampler.ClassifyStatus(WindowsPdhSampler.PdhCstatusNoCounter, "add").Status);
        Assert.Equal("no_data", WindowsPdhSampler.ClassifyStatus(WindowsPdhSampler.PdhNoData, "collect").Status);
        Assert.Equal("no_data", WindowsPdhSampler.ClassifyStatus(WindowsPdhSampler.PdhCstatusNoInstance, "read").Status);
        Assert.Equal("error", WindowsPdhSampler.ClassifyStatus(0xC0000BBC, "collect").Status);
    }

    [Fact]
    public async Task PdhSampler_MissingHypervisorCounters_ReturnsOkWithUnsupportedStatuses()
    {
        IReadOnlyList<string> paths = CounterPresets.Paths["hypervisor"];
        FakePdhQueryFactory factory = new(path =>
            path.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                ? (null, WindowsPdhSampler.PdhCstatusNoObject)
                : (null, WindowsPdhSampler.PdhCstatusNoCounter));
        WindowsPdhSampler sampler = new(factory, new InstantTimeProvider());

        OperationResult<CounterSampleResult> result = await sampler.SampleAsync(
            paths,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(1),
            64,
            "hypervisor",
            Xunit.TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal("hypervisor", result.Value!.Preset);
        Assert.Equal(0, result.Value.DurationMilliseconds);
        Assert.Equal(paths.Count, result.Value.CounterCount);
        Assert.All(result.Value.Counters, counter =>
        {
            Assert.Equal("unsupported", counter.Status);
            Assert.Equal(0, counter.Values.Count);
            Assert.False(string.IsNullOrWhiteSpace(counter.Error));
        });
    }

    [Fact]
    public async Task PdhSampler_MixedUnsupportedNoDataAndValidCounters_ReturnsPerCounterStatuses()
    {
        const string validPath = @"\Processor(_Total)\% Processor Time";
        const string noDataPath = @"\Hyper-V Dynamic Memory VM(*)\Current Pressure";
        const string noInstancePath = @"\VM Processor(*)\% Processor Time";
        const string noObjectPath = @"\Hyper-V Hypervisor Logical Processor(_Total)\% Total Run Time";

        FakePdhQueryFactory factory = new(path => path switch
        {
            validPath => (new FakePdhCounter(path, [new WindowsPdhSampler.PdhSampleReading(path, 42.5, null)]), 0),
            noDataPath => (new FakePdhCounter(path, [new WindowsPdhSampler.PdhSampleReading(path, 0, WindowsPdhSampler.ClassifyStatus(WindowsPdhSampler.PdhNoData, "read"))]), 0),
            noInstancePath => (null, WindowsPdhSampler.PdhCstatusNoInstance),
            _ => (null, WindowsPdhSampler.PdhCstatusNoObject)
        });
        WindowsPdhSampler sampler = new(factory, new InstantTimeProvider());

        OperationResult<CounterSampleResult> result = await sampler.SampleAsync(
            [validPath, noDataPath, noInstancePath, noObjectPath],
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1),
            64,
            "hypervisor",
            Xunit.TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        CounterSample ok = Assert.Single(result.Value!.Counters, item => item.Path == validPath);
        Assert.Equal("ok", ok.Status);
        Assert.Null(ok.Error);
        Assert.Equal(42.5, ok.Values.Max);

        CounterSample noData = Assert.Single(result.Value.Counters, item => item.Path == noDataPath);
        Assert.Equal("no_data", noData.Status);
        Assert.Equal(0, noData.Values.Count);

        CounterSample noInstance = Assert.Single(result.Value.Counters, item => item.Path == noInstancePath);
        Assert.Equal("no_data", noInstance.Status);
        Assert.Equal(0, noInstance.Values.Count);

        CounterSample unsupported = Assert.Single(result.Value.Counters, item => item.Path == noObjectPath);
        Assert.Equal("unsupported", unsupported.Status);
        Assert.Equal(0, unsupported.Values.Count);
    }

    [Fact]
    public async Task PdhSampler_InitialCollectNoData_ReturnsOkWithNoDataStatuses()
    {
        const string path = @"\Hyper-V Hypervisor Virtual Processor(*)\% Guest Run Time";
        FakePdhQueryFactory factory = new(
            p => (new FakePdhCounter(p, []), 0),
            collectStatuses: [WindowsPdhSampler.PdhNoData]);
        WindowsPdhSampler sampler = new(factory, new InstantTimeProvider());

        OperationResult<CounterSampleResult> result = await sampler.SampleAsync(
            [path],
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(1),
            64,
            "hypervisor",
            Xunit.TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, result.Value!.DurationMilliseconds);
        CounterSample sample = Assert.Single(result.Value.Counters);
        Assert.Equal("no_data", sample.Status);
        Assert.Contains("800007D5", sample.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PdhSampler_UnexpectedErrorsStillFail()
    {
        const string path = @"\Processor(_Total)\% Processor Time";
        WindowsPdhSampler badAdd = new(
            new FakePdhQueryFactory(_ => (null, 0xC0000BC0)),
            new InstantTimeProvider());
        OperationResult<CounterSampleResult> addFailure = await badAdd.SampleAsync(
            [path],
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            64,
            "overview",
            Xunit.TestContext.Current.CancellationToken);
        Assert.False(addFailure.Success);
        Assert.Equal("counter_not_found", addFailure.ErrorCode);

        WindowsPdhSampler badCollect = new(
            new FakePdhQueryFactory(p => (new FakePdhCounter(p, []), 0), collectStatuses: [0xC0000BBC]),
            new InstantTimeProvider());
        OperationResult<CounterSampleResult> collectFailure = await badCollect.SampleAsync(
            [path],
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            64,
            "overview",
            Xunit.TestContext.Current.CancellationToken);
        Assert.False(collectFailure.Success);
        Assert.Equal("pdh_failed", collectFailure.ErrorCode);
    }

    [Fact]
    public async Task PdhSampler_PreservesUnexpectedReadErrorsWhenValuesOrNoDataAlsoOccur()
    {
        const string flakyErrorPath = @"\Processor(_Total)\% Processor Time";
        const string transientNoDataPath = @"\PhysicalDisk(_Total)\Current Disk Queue Length";

        FakePdhQueryFactory factory = new(path => path switch
        {
            flakyErrorPath => (new SequencePdhCounter(path, [
                [new WindowsPdhSampler.PdhSampleReading(path, 15.0, null)],
                [new WindowsPdhSampler.PdhSampleReading(path, 0, WindowsPdhSampler.ClassifyStatus(0xC0000BBC, "PdhGetFormattedCounterValue failed"))],
                [new WindowsPdhSampler.PdhSampleReading(path, 0, WindowsPdhSampler.ClassifyStatus(WindowsPdhSampler.PdhNoData, "PdhGetFormattedCounterValue failed"))]
            ]), 0),
            _ => (new SequencePdhCounter(path, [
                [new WindowsPdhSampler.PdhSampleReading(path, 0, WindowsPdhSampler.ClassifyStatus(WindowsPdhSampler.PdhNoData, "PdhGetFormattedCounterValue failed"))],
                [new WindowsPdhSampler.PdhSampleReading(path, 3.0, null)],
                [new WindowsPdhSampler.PdhSampleReading(path, 0, WindowsPdhSampler.ClassifyStatus(WindowsPdhSampler.PdhCstatusNoInstance, "PdhGetFormattedCounterValue failed"))]
            ]), 0)
        });
        WindowsPdhSampler sampler = new(factory, new InstantTimeProvider());

        OperationResult<CounterSampleResult> result = await sampler.SampleAsync(
            [flakyErrorPath, transientNoDataPath],
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(1),
            64,
            "overview",
            Xunit.TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);

        CounterSample errored = Assert.Single(result.Value!.Counters, item => item.Path == flakyErrorPath);
        Assert.Equal("error", errored.Status);
        Assert.Contains("C0000BBC", errored.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, errored.Values.Count);
        Assert.Equal(15.0, errored.Values.Max);

        CounterSample recovered = Assert.Single(result.Value.Counters, item => item.Path == transientNoDataPath);
        Assert.Equal("ok", recovered.Status);
        Assert.Null(recovered.Error);
        Assert.Equal(1, recovered.Values.Count);
        Assert.Equal(3.0, recovered.Values.Max);
    }

    [Fact]
    public void WprPlanner_AllowsOnlyBuiltInProfilesAndFixedArguments()
    {
        const string instance = "Vestigo-Wpr-abc123def456";
        Assert.Null(WprPlanner.ValidateProfile("DiskIO"));
        Assert.NotNull(WprPlanner.ValidateProfile("Custom"));
        Assert.StartsWith(WprPlanner.InstancePrefix, WprPlanner.NewInstanceName());
        Assert.True(WprPlanner.IsOwnedInstance(instance));
        Assert.False(WprPlanner.IsOwnedInstance(null));
        Assert.False(WprPlanner.IsOwnedInstance("GeneralProfile"));
        Assert.Equal(["-start", "GeneralProfile", "-instancename", instance], WprPlanner.StartArguments("generalprofile", instance));
        Assert.Equal(["-stop", @"C:\out.etl", "-instancename", instance], WprPlanner.StopArguments(@"C:\out.etl", instance));
        Assert.Equal(["-cancel", "-instancename", instance], WprPlanner.CancelArguments(instance));
        Assert.Equal(["-status", "-instancename", instance], WprPlanner.StatusArguments(instance));
        Assert.True(WprPlanner.IndicatesNoRecording(unchecked((int)WprPlanner.NoRecordingHresult), ""));
        Assert.True(WprPlanner.IndicatesNoRecording(
            1,
            "        There are no trace profiles running.\n        Error code: 0xc5583000"));
        Assert.True(WprPlanner.IndicatesNoRecording(1, "WPR is not recording"));
        Assert.True(WprPlanner.IndicatesActiveRecording("WPR recording is in progress..."));
        Assert.False(WprPlanner.IndicatesNoRecording(1, "WPR recording is in progress..."));
    }

    [Fact]
    public void Module_IsOptInNeedsJobsAndWarnsWithoutTokens()
    {
        ModuleSettings on = new() { Enabled = true };
        VestigoOptions defaults = new();
        VestigoOptions enabled = new() { Modules = { ["Performance"] = on } };
        VestigoOptions noJobs = new()
        {
            Modules = { ["Performance"] = on, ["Jobs"] = new ModuleSettings { Enabled = false } }
        };
        VestigoOptions gated = new()
        {
            Modules = { ["Performance"] = on },
            Performance = { AllowKernelTrace = true, AllowRawEtlCapture = true, AllowStorageProbes = true, AllowPacketDropCapture = true }
        };

        Assert.DoesNotContain(ModuleRegistry.Enabled(defaults), module => module.Name == "Performance");
        ModuleRegistry.ValidateConfiguration(defaults);
        ModuleRegistry.ValidateConfiguration(enabled);
        Assert.Throws<InvalidOperationException>(() => ModuleRegistry.ValidateConfiguration(noJobs));
        Assert.Contains("Performance", ModuleRegistry.GetPrivilegedModuleWarning(enabled));
        Assert.DoesNotContain("Kernel ETW", ModuleRegistry.GetPrivilegedModuleWarning(enabled));
        Assert.Contains("Kernel ETW", ModuleRegistry.GetPrivilegedModuleWarning(gated));
        Assert.Contains("WPR recording", ModuleRegistry.GetPrivilegedModuleWarning(gated));
        Assert.Contains("Storage latency probes", ModuleRegistry.GetPrivilegedModuleWarning(gated));
        Assert.Contains("Packet-drop capture", ModuleRegistry.GetPrivilegedModuleWarning(gated));
        Assert.Null(ModuleRegistry.GetPrivilegedModuleWarning(new VestigoOptions
        {
            ApiTokens = ["token-value"],
            Modules = { ["Performance"] = on }
        }));
        Assert.Equal("Jobs", ModuleSelection.Find("Performance")?.Parent);
        Assert.False(ModuleSelection.Defaults()["Performance"]);
        Assert.Equal("Performance", ModuleSelection.Find(ModuleSelection.PerformanceKernelTrace)?.Parent);
        Assert.True(ModuleSelection.PreservesExistingWhenUnselected(ModuleSelection.PerformanceRawEtlCapture));
        Assert.True(ModuleSelection.PreservesExistingWhenUnselected(ModuleSelection.PerformancePacketDropCapture));
    }

    [Fact]
    public async Task Tools_RefuseGatesProtectedPidsAndUnrootedPaths()
    {
        VestigoOptions options = new()
        {
            Roots = [new RootDefinition { Name = "out", Path = _root, Access = RootAccess.Read | RootAccess.Write }],
            Performance =
            {
                AllowKernelTrace = false,
                AllowRawEtlCapture = false,
                AllowStorageProbes = false
            }
        };
        RootPolicy roots = new(options);
        PerformanceKernelTools kernel = new(new FakeKernelCapture(), new FakeEtlAnalyzer(), roots, options);
        Assert.Equal("kernel_trace_disabled", (await kernel.CaptureKernelLatencySummary()).ErrorCode);

        PerformanceWprTools wpr = new(new FakeWpr(), roots, new JobManager(options, Path.Combine(_root, "jobs")), options, NullLogger<PerformanceWprTools>.Instance);
        Assert.Equal("raw_etl_disabled", (await wpr.RecordWprTrace("out:trace.etl")).ErrorCode);
        WindowsWprRecorder recorder = new(options, new FakeVolumeSpace(1L << 40), NullLogger<WindowsWprRecorder>.Instance);
        Assert.Equal("raw_etl_disabled", recorder.PlanStart("GeneralProfile", Path.Combine(_probeRoot, "trace.etl"), TimeSpan.FromSeconds(1)).ErrorCode);
        Assert.Equal("wpr_not_running", (await recorder.CancelRecordingAsync(CancellationToken.None)).ErrorCode);

        PerformanceStorageProbeTools storage = new(new FakeStorageProbe(), roots, options);
        Assert.Equal("storage_probes_disabled", (await storage.ProbeStorageLatency("out:")).ErrorCode);

        options.Performance.AllowStorageProbes = true;
        Assert.Equal("outside_root", (await storage.ProbeStorageLatency(Path.GetTempPath())).ErrorCode);
        Assert.Equal("protected_process", new FakeThreadInspector().Inspect(0, null).ErrorCode);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("protected_process", new WindowsThreadInspector().Inspect(4, null).ErrorCode);
        }
    }

    [Fact]
    public async Task ConfigPage_ExposesAndSavesPerformanceGates()
    {
        await using BunitContext context = new();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        JsonObject values = new()
        {
            ["AllowKernelTrace"] = false,
            ["AllowRawEtlCapture"] = false,
            ["AllowStorageProbes"] = false,
            ["AllowPacketDropCapture"] = false,
            ["MaxSampleDuration"] = "00:01:00",
            ["MaxPacketDropDuration"] = "00:00:15",
            ["MaxRawEtlBytes"] = 512L * 1024 * 1024
        };
        IRenderedComponent<ConfigFields> cut = context.Render<ConfigFields>(parameters => parameters
            .Add(field => field.Values, values)
            .Add(field => field.Prefix, "Performance"));
        Assert.Contains("Performance:AllowKernelTrace", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Performance:AllowRawEtlCapture", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Performance:AllowStorageProbes", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Performance:AllowPacketDropCapture", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Performance:MaxSampleDuration", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Performance:MaxPacketDropDuration", cut.Markup, StringComparison.Ordinal);

        ConfigurationEditor editor = new(new VestigoOptions(), ModuleRegistry.All);
        ConfigurationSnapshot snapshot = editor.Load();
        snapshot.Values["Performance"]!["AllowKernelTrace"] = true;
        snapshot.Values["Performance"]!["AllowPacketDropCapture"] = true;
        snapshot.Values["Performance"]!["MaxPacketDropDuration"] = "00:00:20";
        snapshot.Values["Modules"]!["Performance"]!["Enabled"] = true;
        ConfigurationSnapshot saved = editor.Save(snapshot);
        Assert.True(saved.Values["Performance"]!["AllowKernelTrace"]!.GetValue<bool>());
        Assert.True(saved.Values["Performance"]!["AllowPacketDropCapture"]!.GetValue<bool>());
        Assert.Equal("00:00:20", saved.Values["Performance"]!["MaxPacketDropDuration"]!.GetValue<string>());
        Assert.True(saved.Values["Modules"]!["Performance"]!["Enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task StorageProbe_CleansUpAndReportsModesOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        VestigoOptions options = new()
        {
            Roots = [new RootDefinition { Name = "out", Path = _probeRoot, Access = RootAccess.Write }],
            Performance = { AllowStorageProbes = true, MaxStorageProbeDuration = TimeSpan.FromSeconds(5) }
        };
        PerformanceStorageProbeTools tools = new(new WindowsStorageLatencyProbe(), new RootPolicy(options), options);
        OperationResult<StorageProbeResult> result = await tools.ProbeStorageLatency("out:", 4096, 1);
        Assert.True(result.Success, result.Error);
        Assert.True(result.Value!.CleanedUp);
        Assert.Contains(result.Value.Modes, mode => mode.Mode == "buffered");
        Assert.Contains(result.Value.Modes, mode => mode.Mode == "create-close");
        Assert.Empty(Directory.GetFiles(_probeRoot, "vestigo-*.tmp"));
    }

    private sealed class FakeVolumeSpace(long? free) : IVolumeSpace
    {
        public long? FreeBytes(string directory) => free;
    }

    private sealed class FakeKernelCapture : IKernelLatencyCapture
    {
        public Task<OperationResult<KernelLatencySummary>> CaptureAsync(
            IReadOnlyList<string> groups,
            TimeSpan duration,
            int maxEvents,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult<KernelLatencySummary>.Fail("should_not_run", "gate should refuse first."));
    }

    private sealed class FakeEtlAnalyzer : IEtlPerformanceAnalyzer
    {
        public OperationResult<KernelLatencySummary> Analyze(string fullPath, IReadOnlyList<string> groups, int maxEvents) =>
            OperationResult<KernelLatencySummary>.Fail("should_not_run", "gate should refuse first.");
    }

    private sealed class FakeWpr : IWprRecorder
    {
        public OperationResult<string> PlanStart(string profile, string outputPath, TimeSpan duration) =>
            OperationResult<string>.Fail("raw_etl_disabled", "WPR recording is disabled. Set Vestigo:Performance:AllowRawEtlCapture to true.");

        public Task<OperationResult<WprCopyOutcome>> RecordAsync(
            string profile,
            string outputPath,
            TimeSpan duration,
            long maxBytes,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult<WprCopyOutcome>.Fail("should_not_run", "gate should refuse first."));

        public OperationResult<WprStatus> Status() => OperationResult<WprStatus>.Ok(new WprStatus("idle", null, null, null));

        public Task<OperationResult<WprStatus>> StopRecordingAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Status());

        public Task<OperationResult<WprStatus>> CancelRecordingAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Status());
    }

    private sealed class FakeStorageProbe : IStorageLatencyProbe
    {
        public Task<OperationResult<StorageProbeResult>> ProbeAsync(
            string directory,
            long fileBytes,
            TimeSpan duration,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult<StorageProbeResult>.Fail("should_not_run", "gate should refuse first."));
    }

    private sealed class FakeThreadInspector : IThreadInspector
    {
        public OperationResult<ThreadInspection> Inspect(int pid, DateTimeOffset? expectedStartTime) =>
            pid is 0 or 4
                ? OperationResult<ThreadInspection>.Fail("protected_process", "PID 0 and 4 cannot be inspected.")
                : OperationResult<ThreadInspection>.Fail("should_not_run", "unexpected pid.");
    }

    private sealed class FakePdhQueryFactory(
        Func<string, (WindowsPdhSampler.IPdhCounter? Counter, uint Status)> addCounter,
        IReadOnlyList<uint>? collectStatuses = null) : WindowsPdhSampler.IPdhQueryFactory
    {
        public WindowsPdhSampler.IPdhQuery Open() => new FakePdhQuery(addCounter, collectStatuses ?? [0]);
    }

    private sealed class FakePdhQuery(
        Func<string, (WindowsPdhSampler.IPdhCounter? Counter, uint Status)> addCounter,
        IReadOnlyList<uint> collectStatuses) : WindowsPdhSampler.IPdhQuery
    {
        private int _collectIndex;

        public (WindowsPdhSampler.IPdhCounter? Counter, uint Status) TryAdd(string path) => addCounter(path);

        public uint Collect()
        {
            int index = Math.Min(_collectIndex, collectStatuses.Count - 1);
            _collectIndex++;
            return collectStatuses[index];
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakePdhCounter(string path, IReadOnlyList<WindowsPdhSampler.PdhSampleReading> readings)
        : WindowsPdhSampler.IPdhCounter
    {
        public string Path { get; } = path;

        public IReadOnlyList<WindowsPdhSampler.PdhSampleReading> ReadSamples() => readings;

        public void Dispose()
        {
        }
    }

    private sealed class SequencePdhCounter(string path, IReadOnlyList<IReadOnlyList<WindowsPdhSampler.PdhSampleReading>> sequence)
        : WindowsPdhSampler.IPdhCounter
    {
        private int _index;

        public string Path { get; } = path;

        public IReadOnlyList<WindowsPdhSampler.PdhSampleReading> ReadSamples()
        {
            int current = Math.Min(_index, sequence.Count - 1);
            _index++;
            return sequence[current];
        }

        public void Dispose()
        {
        }
    }

    private sealed class InstantTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero)
            {
                Interlocked.Add(ref _ticks, dueTime.Ticks);
            }

            callback(state);
            return NoopTimer.Instance;
        }

        private sealed class NoopTimer : ITimer
        {
            public static NoopTimer Instance { get; } = new();

            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
