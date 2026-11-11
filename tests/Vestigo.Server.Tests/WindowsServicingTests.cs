using System.Reflection;
using System.Runtime.InteropServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Dism;
using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Jobs;
using Vestigo.Modules;
using Vestigo.Security;
using Vestigo.WindowsServicing;

namespace Vestigo.Server.Tests;

public sealed class WindowsServicingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Vestigo-servicing-" + Guid.NewGuid().ToString("N"));

    public WindowsServicingTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class Registry : IServicingRegistry
    {
        public HashSet<string> Keys { get; } = [];
        public Dictionary<(string, string), object> Values { get; } = [];
        public string? Denied { get; set; }
        public bool KeyExists(string path) => path == Denied ? throw new UnauthorizedAccessException() : Keys.Contains(path);
        public object? Read(string path, string name)
        {
            if (path == Denied) throw new UnauthorizedAccessException();
            if (name == "ComputerName") return "test-machine";
            return Values.GetValueOrDefault((path, name));
        }
    }

    private sealed class Agent : IWindowsUpdateAgent
    {
        public bool Reboot { get; set; }
        public string? InventoryState { get; private set; }
        public int InventoryLimit { get; private set; }
        public bool RebootRequired() => Reboot;
        public WuaStatus Status() => new(Reboot, false, []);
        public UpdateInventory Inventory(string state, bool drivers, bool hidden, int limit)
        { InventoryState = state; InventoryLimit = limit; return new(2, new([], 0, false), []); }
        public BoundedList<UpdateHistoryEntry> History(int offset, int limit, bool failuresOnly) => new([], 0, false);
    }

    private sealed class Packages : IServicingPackageSource
    { public BoundedList<ServicingPackage> List(int limit) => new([new("test", "installPending", 5, 0)], 1, false); }

    private sealed class Lockers : IFileLockerSource
    {
        public int Calls { get; private set; }
        public FileLockers Query(string path, int limit) { Calls++; return new([], 0, false); }
    }

    private WindowsServicingTools Tools(Registry registry, Agent agent, JobManager jobs, VestigoOptions? options = null) =>
        new(new(registry), agent, new Packages(), new(), jobs, options ?? new());

    [Fact]
    public void RegistrySnapshot_ClearAndDeniedRemainDistinct()
    {
        Registry registry = new();
        RebootPendingService service = new(registry);
        Assert.Equal("notPending", service.RegistrySnapshot().State);
        registry.Denied = RebootPendingService.Cbs + @"\RebootPending";
        RebootSnapshot result = service.RegistrySnapshot();
        Assert.Equal("unknown", result.State);
        Assert.Equal("unknown", Assert.Single(result.Signals, s => s.Source == "CbsReboot").State);
    }

    [Fact]
    public void RegistrySnapshot_AnyPositiveWinsOverUnknown()
    {
        Registry registry = new() { Denied = RebootPendingService.Cbs + @"\PackagesPending" };
        registry.Keys.Add(RebootPendingService.Cbs + @"\RebootPending");
        Assert.Equal("pending", new RebootPendingService(registry).RegistrySnapshot().State);
    }

    [Fact]
    public void RegistrySnapshot_AdvisoryActivityDoesNotAssertReboot()
    {
        Registry registry = new();
        registry.Keys.Add(RebootPendingService.Cbs + @"\SessionsPending");
        RebootSignal signal = Assert.Single(new RebootPendingService(registry).RegistrySnapshot().Signals, s => s.Source == "CbsSessions");
        Assert.Equal("unknown", signal.State);
        Assert.Contains("activity", signal.Detail);
    }

    [Fact]
    public void PendingQueue_PreservesEmptyDeleteTargetReplacementAndMalformedPair()
    {
        IReadOnlyList<PendingFileOperation> operations = RebootPendingService.Parse("queue",
            [@"\??\C:\delete.sys", "", @"\??\C:\new.sys", @"!\??\C:\old.sys", @"\Device\Volume\orphan"], 10);
        Assert.Equal(["delete", "replace", "malformed"], operations.Select(x => x.Operation));
        Assert.Equal(@"C:\delete.sys", operations[0].SourcePath);
        Assert.Equal(@"!\??\C:\old.sys", operations[1].Destination);
        Assert.Equal(@"C:\old.sys", operations[1].DestinationPath);
        Assert.Null(operations[2].Destination);
        Assert.Null(operations[2].SourcePath);
        Assert.NotNull(operations[2].Warning);
        Assert.Equal([0, 1, 2], operations.Select(x => x.Index));
    }

    [Theory]
    [InlineData(@"\??\UNC\server\share\a", @"\\server\share\a")]
    [InlineData(@"\??\C:\a", @"C:\a")]
    [InlineData(@"C:\a", @"C:\a")]
    [InlineData(@"\Device\HarddiskVolume1\a", null)]
    [InlineData("relative", null)]
    public void Normalize_OnlyRecognizedNamespaces(string input, string? expected) => Assert.Equal(expected, RebootPendingService.Normalize(input));

    [Fact]
    public void PendingQueue_BothSourcesGlobalLimitAndWrongType()
    {
        Registry registry = new();
        foreach (string name in RebootPendingService.QueueNames)
            registry.Values[(RebootPendingService.SessionManager, name)] = new[] { @"\??\C:\a", "" };
        PendingFileQueue queue = new RebootPendingService(registry).PendingFiles(1);
        Assert.Single(queue.Operations);
        Assert.True(queue.Truncated);
        Assert.All(queue.Sources, s => Assert.Equal("pending", s.State));
        registry.Values[(RebootPendingService.SessionManager, RebootPendingService.QueueNames[0])] = "wrong registry type";
        Assert.Equal("unknown", new RebootPendingService(registry).PendingFiles(10).Sources[0].State);
    }

    [Fact]
    public async Task Worker_TimeoutRetainsSlotUntilNativeCompletion()
    {
        NativeDiagnosticWorker worker = new();
        using ManualResetEventSlim release = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task<OperationResult<int>> call = worker.Run(() => { started.SetResult(); release.Wait(); return 1; }, TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal("servicing_timeout", (await call).ErrorCode);
            Assert.Equal("servicing_busy", (await worker.Run(() => 2, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken)).ErrorCode);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task Worker_MtaAndOriginalComHResultAndRecovery()
    {
        NativeDiagnosticWorker worker = new();
        OperationResult<ApartmentState> apartment = await worker.Run(() => Thread.CurrentThread.GetApartmentState(), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(ApartmentState.MTA, apartment.Value);
        OperationResult<int> failure = await worker.Run<int>(() => throw new COMException("private message", unchecked((int)0x8024001E)), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains("0x8024001E", failure.Error);
        Assert.DoesNotContain("private message", failure.Error);
        Assert.True((await worker.Run(() => 1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Success);
    }

    [Fact]
    public async Task Worker_CancellationDoesNotReleaseNativeSlot()
    {
        NativeDiagnosticWorker worker = new();
        using ManualResetEventSlim release = new();
        using CancellationTokenSource cancel = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task<OperationResult<int>> call = worker.Run(() => { started.SetResult(); release.Wait(); return 1; }, TimeSpan.FromSeconds(10), cancel.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
            Assert.Equal("servicing_busy", (await worker.Run(() => 1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ErrorCode);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task RebootTool_AggregatesWuaAndRegistry()
    {
        using JobManager jobs = new(new(), _directory);
        OperationResult<RebootSnapshot> result = await Tools(new(), new() { Reboot = true }, jobs).GetRebootPending(TestContext.Current.CancellationToken);
        Assert.Equal("pending", result.Value!.State);
        Assert.Equal("pending", Assert.Single(result.Value.Signals, s => s.Source == "WuaSystemInfo").State);
    }

    [Fact]
    public async Task InventoryJob_BoundsValidatesAndSpoolsStructuredResult()
    {
        VestigoOptions options = new() { WindowsServicing = { MaxResults = 2 } };
        using JobManager jobs = new(options, _directory);
        Agent agent = new();
        WindowsServicingTools tools = Tools(new(), agent, jobs, options);
        Assert.Equal("invalid_request", tools.ListWindowsUpdates("arbitrary search").ErrorCode);
        Assert.Equal("invalid_request", (await tools.GetUpdateHistory(-1, cancellationToken: TestContext.Current.CancellationToken)).ErrorCode);
        Assert.Empty(jobs.List());
        OperationResult<JobInfo> job = tools.ListWindowsUpdates("installed", limit: 999);
        JobInfo finished = (await jobs.WaitAsync(job.Value!.Id, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Value!;
        Assert.Equal(JobState.Succeeded, finished.State);
        Assert.Equal("installed", agent.InventoryState);
        Assert.Equal(2, agent.InventoryLimit);
        Assert.Contains("\"ResultCode\":2", jobs.ReadOutput(finished.Id, 0, null).Value!.Text);
        JobInfo package = tools.ListServicingPackages().Value!;
        await jobs.WaitAsync(package.Id, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains("installPending", jobs.ReadOutput(package.Id, 0, null).Value!.Text);
    }

    [Theory]
    [InlineData("pending", false, false, "IsInstalled=0 AND Type='Software' AND IsHidden=0")]
    [InlineData("installed", true, true, "IsInstalled=1")]
    public void WuaCriteria_IsFixedAndOfflineInventoryHasNoOnlineSwitch(string state, bool drivers, bool hidden, string expected) =>
        Assert.Equal(expected, WindowsUpdateAgent.Criteria(state, drivers, hidden));

    [Fact]
    public async Task FileLockers_RequiresReadRootBeforeNativeCall()
    {
        Lockers source = new();
        VestigoOptions options = new() { Roots = [new() { Name = "files", Path = _directory, Access = RootAccess.Read }] };
        FileLockerTools tools = new(new(options), source, new(), options);
        Assert.False((await tools.GetFileLockers(@"C:\Windows\System32\ntdll.dll", cancellationToken: TestContext.Current.CancellationToken)).Success);
        Assert.Equal(0, source.Calls);
        File.WriteAllText(Path.Combine(_directory, "test.txt"), "test");
        Assert.True((await tools.GetFileLockers("files:test.txt", cancellationToken: TestContext.Current.CancellationToken)).Success);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public void Logs_EnforceRootsAndBoundFilter()
    {
        VestigoOptions options = new();
        UpdateDiagnosticsTools tools = new(options, new(options), new Agent(), new());
        Assert.Equal("invalid_request", tools.GetServicingLogs(new string('x', 129)).ErrorCode);
        Assert.All(tools.GetServicingLogs().Value!, row => Assert.False(row.Excerpt.Success));
    }

    [Fact]
    public void Module_IsOptInNeedsJobsAndRegistersServices()
    {
        VestigoOptions options = new();
        WindowsServicingModule module = new();
        Assert.False(module.IsEnabled(options));
        options.Modules["WindowsServicing"] = new() { Enabled = true };
        options.Modules["Jobs"] = new() { Enabled = false };
        Assert.Throws<InvalidOperationException>(() => ModuleRegistry.ValidateConfiguration(options));
        options.Modules["Jobs"].Enabled = true;
        ModuleRegistry.ValidateConfiguration(options);
        ServiceCollection services = new();
        module.ConfigureServices(services, options);
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<RebootPendingService>());
        Assert.NotNull(provider.GetRequiredService<IWindowsUpdateAgent>());
        Assert.NotNull(provider.GetRequiredService<IServicingPackageSource>());
        Assert.NotNull(provider.GetRequiredService<IFileLockerSource>());
        Assert.Contains("WindowsServicing", ModuleRegistry.GetPrivilegedModuleWarning(options));
        options.ApiTokens.Add("secret");
        Assert.Null(ModuleRegistry.GetPrivilegedModuleWarning(options));
    }

    [Fact]
    public void AllToolsAdvertiseReadOnlyAndNonDestructive()
    {
        MethodInfo[] tools = new WindowsServicingModule().ToolTypes.SelectMany(type => type.GetMethods())
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null).ToArray();
        Assert.Equal(11, tools.Length);
        foreach (MethodInfo method in tools)
        {
            McpServerToolAttribute attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;
            Assert.True(attribute.ReadOnly);
            Assert.False(attribute.Destructive);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2001)]
    public void Options_RejectUnboundedValues(int limit) =>
        Assert.Throws<InvalidOperationException>(new WindowsServicingOptions { MaxResults = limit }.Validate);

    [Theory]
    [InlineData(4, "installed")]
    [InlineData(5, "installPending")]
    [InlineData(6, "superseded")]
    [InlineData(99, "unknown")]
    public void DismStates_MatchDocumentedEnum(int state, string expected) => Assert.Equal(expected, ServicingPackageSource.StateName(state));

    [Fact]
    public async Task NativeSmoke_ReadOnlyApis()
    {
        if (Environment.GetEnvironmentVariable("VESTIGO_SERVICING_SMOKE") != "1") return;
        NativeDiagnosticWorker worker = new();
        WindowsUpdateAgent agent = new();
        OperationResult<bool> reboot = await worker.Run(agent.RebootRequired, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(reboot.Success, reboot.Error);
        OperationResult<WuaStatus> status = await worker.Run(agent.Status, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(status.Success, status.Error);
        OperationResult<BoundedList<UpdateHistoryEntry>> history = await worker.Run(() => agent.History(0, 5, false), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(history.Success, history.Error);
        Assert.InRange(history.Value!.Items.Count, 0, 5);
        string file = Path.Combine(_directory, "locked.txt");
        using FileStream stream = File.Create(file);
        OperationResult<FileLockers> lockers = await worker.Run(() => new RestartManagerSource().Query(file, 5), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(lockers.Success, lockers.Error);
    }

    [Fact]
    public async Task NativeSmoke_DismPackagesRequireElevation()
    {
        if (Environment.GetEnvironmentVariable("VESTIGO_SERVICING_SMOKE") != "1") return;
        using System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            Assert.Skip("DISM online package enumeration requires an elevated Windows token.");
        OperationResult<BoundedList<ServicingPackage>> packages = await new NativeDiagnosticWorker().Run(
            () => new ServicingPackageSource().List(5), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(packages.Success, packages.Error);
        Assert.NotEmpty(packages.Value!.Items);
        Assert.InRange(packages.Value.Items.Count, 1, 5);
        // A second complete cycle detects leaked sessions and unmatched initialization.
        OperationResult<BoundedList<ServicingPackage>> next = await new NativeDiagnosticWorker().Run(
            () => new ServicingPackageSource().List(1), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(next.Success, next.Error);
        Assert.Single(next.Value!.Items);
    }

    [Fact]
    public async Task NativeSmoke_OfflineApplicabilityInventory()
    {
        if (Environment.GetEnvironmentVariable("VESTIGO_SERVICING_SCAN_SMOKE") != "1") return;
        foreach (string state in new[] { "pending", "installed" })
        {
            OperationResult<UpdateInventory> result = await new NativeDiagnosticWorker().Run(
                () => new WindowsUpdateAgent().Inventory(state, true, false, 5), TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Error);
            Assert.InRange(result.Value!.Updates.Items.Count, 0, 5);
            Assert.InRange(result.Value.ResultCode, 2, 5);
            Assert.All(result.Value.Updates.Items, item => Assert.True(Guid.TryParse(item.Id, out _)));
        }
    }

    [Fact]
    public void DismLoader_RestrictsImportsToSystem32()
    {
        SystemDismLibrary.Configure();
        Assembly assembly = typeof(DismApi).Assembly;
        nint handle = SystemDismLibrary.Resolve("DismApi", assembly, null);
        Assert.NotEqual(nint.Zero, handle);
        Assert.Equal(handle, SystemDismLibrary.Resolve("DismApi.dll", assembly, null));
        using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
        System.Diagnostics.ProcessModule module = process.Modules.Cast<System.Diagnostics.ProcessModule>()
            .Single(item => item.BaseAddress == handle);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "DismApi.dll"), module.FileName, ignoreCase: true);
        foreach (string name in new[] { "kernel32", "./DismApi.dll", @"C:\untrusted\DismApi.dll", "unknown" })
            Assert.Throws<DllNotFoundException>(() => SystemDismLibrary.Resolve(name, assembly, null));
    }

    private sealed class DismTestSession(Action close) : IDisposable
    {
        public void Dispose() => close();
    }

    [Theory]
    [InlineData("none", "initialize,open,read,close,shutdown")]
    [InlineData("initialize", "initialize")]
    [InlineData("open", "initialize,open,shutdown")]
    [InlineData("read", "initialize,open,read,close,shutdown")]
    [InlineData("close", "initialize,open,read,close,shutdown")]
    [InlineData("shutdown", "initialize,open,read,close,shutdown")]
    public void DismLifecycle_CleansUpInOrderAndPreservesErrors(string failure, string expected)
    {
        List<string> calls = [];
        COMException error = new("private native message", unchecked((int)0x80070005));
        void Step(string name)
        {
            calls.Add(name);
            if (name == failure) throw error;
        }
        int Run() => ServicingPackageSource.WithSession(
            () => Step("initialize"),
            () => { Step("open"); return new DismTestSession(() => Step("close")); },
            _ => { Step("read"); return 42; },
            () => Step("shutdown"));
        if (failure == "none") Assert.Equal(42, Run());
        else Assert.Same(error, Assert.Throws<COMException>(() => Run()));
        Assert.Equal(expected, string.Join(',', calls));
        Assert.Equal(42, ServicingPackageSource.WithSession(() => { },
            () => new DismTestSession(() => { }), _ => 42, () => { }));
    }

    [Fact]
    public async Task DismLifecycle_SerializesSeparateCallers()
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim secondStarted = new();
        int active = 0;
        int cycles = 0;
        int Run(bool first) => ServicingPackageSource.WithSession(
            () => Assert.Equal(1, Interlocked.Increment(ref active)),
            () => new DismTestSession(() => Assert.Equal(1, Volatile.Read(ref active))),
            _ =>
            {
                if (first)
                {
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                }
                return Interlocked.Increment(ref cycles);
            },
            () => Assert.Equal(0, Interlocked.Decrement(ref active)));
        Task<int> first = Task.Run(() => Run(true), TestContext.Current.CancellationToken);
        Task<int>? second = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            second = Task.Run(() => { secondStarted.Set(); return Run(false); }, TestContext.Current.CancellationToken);
            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(second.Wait(TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            release.Set();
            await first;
            if (second is not null) await second;
        }
        Assert.Equal(2, cycles);
    }

    [Fact]
    public async Task NativeSmoke_DismInitializationFailurePreservesHResultAndRecovers()
    {
        if (Environment.GetEnvironmentVariable("VESTIGO_SERVICING_SMOKE") != "1") return;
        using System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            Assert.Skip("This failure-path smoke test requires a non-elevated Windows token.");
        NativeDiagnosticWorker worker = new();
        for (int i = 0; i < 2; i++)
        {
            OperationResult<BoundedList<ServicingPackage>> result = await worker.Run(
                () => new ServicingPackageSource().List(1), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.False(result.Success);
            Assert.Contains("0x800702E4", result.Error);
        }
    }

    [Fact]
    public async Task NativeSmoke_WuaRecoversAfterExceptionDuringOwnedComWork()
    {
        if (Environment.GetEnvironmentVariable("VESTIGO_SERVICING_SMOKE") != "1") return;
        OperationResult<WuaStatus> result = await new NativeDiagnosticWorker().Run(() =>
        {
            WindowsUpdateAgent agent = new();
            Assert.Throws<ArgumentException>(() => agent.Inventory("invalid", true, false, 5));
            return agent.Status();
        }, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error);
    }
}
