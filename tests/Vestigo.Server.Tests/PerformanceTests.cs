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

public sealed class PerformanceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vestigo-perf-").FullName;
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
        aggregator.NoteImage(0x1000, @"C:\Windows\System32\drivers\disk.sys");
        aggregator.AddDpc(0x1000, 3);
        aggregator.AddCSwitch(100, 4, "app", "System", "WrFilterContext", 20, 5);
        aggregator.AddReadyDelay(7);
        aggregator.AddHardFault(100, "app", @"C:\page.sys");
        aggregator.Truncate(1);
        KernelLatencySummary summary = aggregator.Build("Vestigo-Perf-abc", 1000, KernelTraceGroups.All, null);
        Assert.True(summary.Truncated);
        Assert.Equal(12.5, summary.DiskIo[0].ServiceMs.Min);
        Assert.Equal(40, summary.SlowestDiskIo[0].ElapsedMs);
        Assert.Contains(summary.FileIo, item => item.Kind == "create");
        Assert.Equal("disk.sys", summary.Dpc[0].Image);
        Assert.Contains(summary.Scheduler[0].WaitReasons, item => item.Reason == "WrFilterContext");
        Assert.Equal(7, summary.ReadyDelay!.DelayMs.Max);
        Assert.Equal(1, summary.HardFaults[0].Count);
    }

    [Fact]
    public void WprPlanner_AllowsOnlyBuiltInProfilesAndFixedArguments()
    {
        Assert.Null(WprPlanner.ValidateProfile("DiskIO"));
        Assert.NotNull(WprPlanner.ValidateProfile("Custom"));
        Assert.Equal(["-start", "GeneralProfile", "-filemode"], WprPlanner.StartArguments("generalprofile"));
        Assert.Equal(["-stop", @"C:\out.etl"], WprPlanner.StopArguments(@"C:\out.etl"));
        Assert.Equal(["-cancel"], WprPlanner.CancelArguments());
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
            Performance = { AllowKernelTrace = true, AllowRawEtlCapture = true, AllowStorageProbes = true }
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
        Assert.Null(ModuleRegistry.GetPrivilegedModuleWarning(new VestigoOptions
        {
            ApiTokens = ["token-value"],
            Modules = { ["Performance"] = on }
        }));
        Assert.Equal("Jobs", ModuleSelection.Find("Performance")?.Parent);
        Assert.False(ModuleSelection.Defaults()["Performance"]);
        Assert.Equal("Performance", ModuleSelection.Find(ModuleSelection.PerformanceKernelTrace)?.Parent);
        Assert.True(ModuleSelection.PreservesExistingWhenUnselected(ModuleSelection.PerformanceRawEtlCapture));
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
            ["MaxSampleDuration"] = "00:01:00",
            ["MaxRawEtlBytes"] = 512L * 1024 * 1024
        };
        IRenderedComponent<ConfigFields> cut = context.Render<ConfigFields>(parameters => parameters
            .Add(field => field.Values, values)
            .Add(field => field.Prefix, "Performance"));
        Assert.Contains("Performance:AllowKernelTrace", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Performance:AllowRawEtlCapture", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Performance:AllowStorageProbes", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Performance:MaxSampleDuration", cut.Markup, StringComparison.Ordinal);

        ConfigurationEditor editor = new(new VestigoOptions(), ModuleRegistry.All);
        ConfigurationSnapshot snapshot = editor.Load();
        snapshot.Values["Performance"]!["AllowKernelTrace"] = true;
        snapshot.Values["Modules"]!["Performance"]!["Enabled"] = true;
        ConfigurationSnapshot saved = editor.Save(snapshot);
        Assert.True(saved.Values["Performance"]!["AllowKernelTrace"]!.GetValue<bool>());
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
            Roots = [new RootDefinition { Name = "out", Path = _root, Access = RootAccess.Write }],
            Performance = { AllowStorageProbes = true, MaxStorageProbeDuration = TimeSpan.FromSeconds(5) }
        };
        PerformanceStorageProbeTools tools = new(new WindowsStorageLatencyProbe(), new RootPolicy(options), options);
        OperationResult<StorageProbeResult> result = await tools.ProbeStorageLatency("out:", 4096, 1);
        Assert.True(result.Success, result.Error);
        Assert.True(result.Value!.CleanedUp);
        Assert.Contains(result.Value.Modes, mode => mode.Mode == "buffered");
        Assert.Contains(result.Value.Modes, mode => mode.Mode == "create-close");
        Assert.Empty(Directory.GetFiles(_root, "vestigo-*.tmp"));
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
}
