using System.Text.Json;

using Vestigo.Configuration;
using Vestigo.Desktop;
using Vestigo.Jobs;
using Vestigo.Processes;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

public class ManagedDesktopTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-desktops-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");

    public ManagedDesktopTests() => Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _previous);
        Directory.Delete(_settings, recursive: true);
    }

    [Theory]
    [InlineData("Winlogon", true)]
    [InlineData("winlogon", true)]
    [InlineData("Default", false)]
    [InlineData("Vestigo-0123456789ab", false)]
    public void SecureDesktopNames_AreOnlyWinlogon(string name, bool secure) =>
        Assert.Equal(secure, DesktopSessionPolicy.IsSecure(name));

    [Fact]
    public void OwnedNames_AreDistinctFromUserDesktops()
    {
        Assert.Equal("owned", DesktopSessionPolicy.Classify("Vestigo-0123456789ab"));
        Assert.Equal("user", DesktopSessionPolicy.Classify("Default"));
        Assert.Equal("secure", DesktopSessionPolicy.Classify("Winlogon"));
        Assert.False(DesktopSessionPolicy.IsOwnedName("Default"));
        Assert.False(DesktopSessionPolicy.IsOwnedName(@"Vestigo-0123456789ab\extra"));
        Assert.True(DesktopSessionPolicy.IsLaunchPath(@"winsta0\default"));
        Assert.True(DesktopSessionPolicy.IsLaunchPath(@"winsta0\Vestigo-0123456789ab"));
        Assert.False(DesktopSessionPolicy.IsLaunchPath(@"winsta0\Winlogon"));
        Assert.False(DesktopSessionPolicy.IsLaunchPath(@"winsta0\Screen-saver"));
    }

    [Fact]
    public void ResolveDesktop_RefusesSecureAndForeignDesktops()
    {
        Assert.Equal(@"winsta0\default", InteractiveProcessRunner.ResolveDesktop(Request(null)));
        Assert.Equal(@"winsta0\Vestigo-0123456789ab", InteractiveProcessRunner.ResolveDesktop(Request(@"winsta0\Vestigo-0123456789ab")));
        ProcessLaunchException denied = Assert.Throws<ProcessLaunchException>(() => InteractiveProcessRunner.ResolveDesktop(Request(@"winsta0\Winlogon")));
        Assert.Equal("invalid_request", denied.Code);
    }

    [Fact]
    public async Task Create_DoesNotActivate_AndRemembersTheOriginalDesktop()
    {
        RecordingHost host = new();
        ManagedDesktopSessionManager manager = Manager(host, allow: true);
        OperationResult<ManagedDesktopSession> created = await manager.CreateAsync(null, CancellationToken.None);

        Assert.True(created.Success, created.Error);
        Assert.Equal("open", created.Value!.State);
        Assert.False(created.Value.InputSwitched);
        Assert.Equal("Default", created.Value.OriginalInputDesktop);
        Assert.StartsWith("Vestigo-", created.Value.DesktopName, StringComparison.Ordinal);
        Assert.Equal(["create"], host.Operations);
        Assert.True(File.Exists(VestigoPaths.OwnedDesktopsPath));
    }

    [Fact]
    public async Task ClosedRetention_ClearsAfterTheDesktopDisappears()
    {
        RecordingHost host = new() { RetainOnClose = true };
        ManagedDesktopSessionManager manager = Manager(host, allow: true);
        OperationResult<ManagedDesktopSession> created = await manager.CreateAsync("terminate-owned", CancellationToken.None);
        OperationResult<ManagedDesktopSession> closed = await manager.CloseAsync(created.Value!.Id, null, CancellationToken.None);

        Assert.True(closed.Value!.DesktopRetained);
        Assert.Equal("close-time-retained", closed.Value.DesktopPresence);
        Assert.Equal("Windows kept the desktop because another handle or thread still uses it.", closed.Value.LastError);

        OperationResult<ManagedDesktopSession> pending = await manager.GetAsync(created.Value.Id);
        Assert.True(pending.Value!.DesktopRetained);
        Assert.Equal("cleanup-pending", pending.Value.DesktopPresence);
        Assert.NotNull(pending.Value.LastError);

        host.ExternallyPresent.Clear();
        host.FailEnumerate = true;
        OperationResult<ManagedDesktopSession> unknown = await manager.GetAsync(created.Value.Id);
        Assert.Equal("enumeration-unknown", unknown.Value!.DesktopPresence);
        Assert.True(unknown.Value.DesktopRetained);
        Assert.NotNull(unknown.Value.LastError);

        host.FailEnumerate = false;
        OperationResult<ManagedDesktopSession> gone = await manager.GetAsync(created.Value.Id);
        Assert.False(gone.Value!.DesktopRetained);
        Assert.Equal("confirmed-absent", gone.Value.DesktopPresence);
        Assert.Null(gone.Value.LastError);
    }

    [Fact]
    public async Task Create_RefusesASecureInputDesktop()
    {
        RecordingHost host = new() { Secure = true };
        ManagedDesktopSessionManager manager = Manager(host, allow: true);

        OperationResult<ManagedDesktopSession> created = await manager.CreateAsync("terminate-owned", CancellationToken.None);

        Assert.Equal("secure_desktop_active", created.ErrorCode);
        Assert.Empty((await manager.ListAsync()).Value!.Where(session => session.State is "open" or "active"));
    }

    [Fact]
    public async Task ActivateAndRestore_AreExplicit()
    {
        RecordingHost host = new();
        ManagedDesktopSessionManager manager = Manager(host, allow: true);
        ManagedDesktopSession created = (await manager.CreateAsync(null, CancellationToken.None)).Value!;

        OperationResult<ManagedDesktopSession> active = await manager.ActivateAsync(created.Id, CancellationToken.None);
        OperationResult<ManagedDesktopSession> restored = await manager.RestoreAsync(created.Id, CancellationToken.None);

        Assert.True(active.Success, active.Error);
        Assert.True(active.Value!.InputSwitched);
        Assert.Equal("active", active.Value.State);
        Assert.True(restored.Success, restored.Error);
        Assert.False(restored.Value!.InputSwitched);
        Assert.Equal(["create", "activate", "restore"], host.Operations);
    }

    [Fact]
    public async Task Close_FailIfRunning_DoesNotChangeTheInputDesktop()
    {
        RecordingHost host = new();
        RunningCleanup cleanup = new() { Alive = [42] };
        ManagedDesktopSessionManager manager = Manager(host, allow: true, cleanup);
        ManagedDesktopSession created = (await manager.CreateAsync("fail-if-running", CancellationToken.None)).Value!;
        await AttachAsync(manager, created.Id, new OwnedDesktopProcess("job1", 42, false));
        await manager.ActivateAsync(created.Id, CancellationToken.None);
        host.Operations.Clear();

        OperationResult<ManagedDesktopSession> closed = await manager.CloseAsync(created.Id, null, CancellationToken.None);

        Assert.Equal("processes_still_running", closed.ErrorCode);
        Assert.Empty(host.Operations);
        Assert.True((await manager.GetAsync(created.Id)).Value!.InputSwitched);
        Assert.Empty(cleanup.Terminated);
    }

    [Fact]
    public async Task Close_TerminateOwned_RestoresBeforeStoppingProcesses()
    {
        RecordingHost host = new();
        RunningCleanup cleanup = new();
        ManagedDesktopSessionManager manager = Manager(host, allow: true, cleanup);
        ManagedDesktopSession created = (await manager.CreateAsync("terminate-owned", CancellationToken.None)).Value!;
        await AttachAsync(manager, created.Id, new OwnedDesktopProcess("job1", 42, false));
        await manager.ActivateAsync(created.Id, CancellationToken.None);

        OperationResult<ManagedDesktopSession> closed = await manager.CloseAsync(created.Id, null, CancellationToken.None);

        Assert.True(closed.Success, closed.Error);
        Assert.Equal("closed", closed.Value!.State);
        Assert.Equal(["restore", "close", "shutdown"], host.Operations.TakeLast(3));
        Assert.Equal([42], cleanup.Terminated);
        Assert.True(cleanup.TerminatedBeforeClose);
    }

    [Fact]
    public async Task CancellationDuringCreate_ClosesTheDesktop()
    {
        RecordingHost host = new();
        using CancellationTokenSource cancellation = new();
        host.Cancellation = cancellation;
        ManagedDesktopSessionManager manager = Manager(host, allow: true);

        OperationResult<ManagedDesktopSession> created = await manager.CreateAsync(null, cancellation.Token);

        Assert.Equal("cancelled", created.ErrorCode);
        Assert.Contains("close", host.Operations);
        Assert.Empty((await manager.ListAsync()).Value!.Where(session => session.State is "open" or "active"));
    }

    [Fact]
    public async Task LeaseExpiry_UsesTerminateOwned()
    {
        RecordingHost host = new();
        ManualTime time = new();
        RunningCleanup cleanup = new();
        ManagedDesktopSessionManager manager = Manager(host, allow: true, cleanup, time);
        ManagedDesktopSession created = (await manager.CreateAsync("fail-if-running", CancellationToken.None)).Value!;
        await AttachAsync(manager, created.Id, new OwnedDesktopProcess("job1", 7, true));
        time.Now = time.Now.AddMinutes(31);

        await manager.SweepAsync();

        Assert.Equal("closed", (await manager.GetAsync(created.Id)).Value!.State);
        Assert.Equal([7], cleanup.Terminated);
    }

    [Fact]
    public async Task Startup_RecoversPersistedSessionsWithoutTouchingUserDesktops()
    {
        RecordingHost host = new();
        ManagedDesktopSession session = new(
            "abc123",
            "Vestigo-abc123abc123",
            "WinSta0",
            1,
            "Default",
            "active",
            "terminate-owned",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-01-01T00:30:00Z"),
            true,
            true,
            false,
            [new OwnedDesktopProcess("job", 9, false)],
            null);
        Directory.CreateDirectory(_settings);
        await File.WriteAllTextAsync(
            VestigoPaths.OwnedDesktopsPath,
            """{"version":1,"sessions":[{"id":"abc123","desktopName":"Vestigo-abc123abc123","windowStation":"WinSta0","windowsSessionId":1,"originalInputDesktop":"Default","state":"active","cleanupPolicy":"terminate-owned","createdAt":"2026-01-01T00:00:00Z","leaseExpiresAt":"2026-01-01T00:30:00Z","inputSwitched":true,"handleHeld":true,"desktopRetained":false,"processes":[{"jobId":"job","processId":9,"elevated":false}]}]}""");
        RunningCleanup cleanup = new();
        ManagedDesktopSessionManager manager = Manager(host, allow: true, cleanup);

        await manager.StartAsync(CancellationToken.None);

        Assert.Equal(["recover:restore", "recover:close"], host.Operations);
        Assert.Equal([9], cleanup.Terminated);
        Assert.Equal("closed", (await manager.GetAsync(session.Id)).Value!.State);
        Assert.DoesNotContain(host.RecoveredNames, name => name.Equals("Winlogon", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Startup_KeepsTheSessionWhenInputRestoreFails()
    {
        RecordingHost host = new() { FailRestore = true };
        Directory.CreateDirectory(_settings);
        await File.WriteAllTextAsync(
            VestigoPaths.OwnedDesktopsPath,
            """{"version":1,"sessions":[{"id":"abc123","desktopName":"Vestigo-abc123abc123","windowStation":"WinSta0","windowsSessionId":1,"originalInputDesktop":"Default","state":"active","cleanupPolicy":"terminate-owned","createdAt":"2026-01-01T00:00:00Z","leaseExpiresAt":"2026-01-01T00:30:00Z","inputSwitched":true,"handleHeld":true,"desktopRetained":false,"processes":[{"jobId":"job","processId":9,"elevated":false}]}]}""");
        RunningCleanup cleanup = new();
        ManagedDesktopSessionManager manager = Manager(host, allow: true, cleanup);

        await manager.StartAsync(CancellationToken.None);

        ManagedDesktopSession session = (await manager.GetAsync("abc123")).Value!;
        Assert.Equal("recovery-failed", session.State);
        Assert.True(session.InputSwitched);
        Assert.Empty(cleanup.Terminated);
        Assert.Equal(["recover:restore"], host.Operations);

        await manager.StopAsync(CancellationToken.None);
        manager.Dispose();
        host.FailRestore = false;
        ManagedDesktopSessionManager retry = Manager(host, allow: true, cleanup);
        await retry.StartAsync(CancellationToken.None);

        ManagedDesktopSession recovered = (await retry.GetAsync("abc123")).Value!;
        Assert.Equal("closed", recovered.State);
        Assert.False(recovered.InputSwitched);
        Assert.Equal([9], cleanup.Terminated);
        await retry.StopAsync(CancellationToken.None);
        retry.Dispose();
    }

    [Fact]
    public async Task Shutdown_DisposeAndStopShareOneLifetime()
    {
        RecordingHost host = new();
        ManagedDesktopSessionManager first = Manager(host, allow: true);
        await first.StartAsync(CancellationToken.None);
        first.Dispose();
        await first.StopAsync(CancellationToken.None);

        ManagedDesktopSessionManager second = Manager(host, allow: true);
        await second.StartAsync(CancellationToken.None);
        await second.StopAsync(CancellationToken.None);
        second.Dispose();
        await second.StopAsync(CancellationToken.None);

        Assert.Contains(DesktopHostOperations.Shutdown, host.Operations);
    }

    [Fact]
    public async Task Close_ReturnsTheHostErrorWhenRestoreIsWrapped()
    {
        RecordingHost host = new() { RestoreCode = "desktop_unavailable" };
        ManagedDesktopSessionManager manager = Manager(host, allow: true);
        ManagedDesktopSession created = (await manager.CreateAsync(null, CancellationToken.None)).Value!;
        Assert.True((await manager.ActivateAsync(created.Id, CancellationToken.None)).Success);

        OperationResult<ManagedDesktopSession> closed = await manager.CloseAsync(created.Id, "terminate-owned", CancellationToken.None);

        Assert.Equal("desktop_unavailable", closed.ErrorCode);
        Assert.Equal("The original desktop could not be opened.", closed.Error);
        Assert.Equal("active", (await manager.GetAsync(created.Id)).Value!.State);
    }

    [Fact]
    public async Task Activate_TimeoutKeepsTheSwitchIntent()
    {
        RecordingHost host = new() { RejectOp = DesktopHostOperations.Activate };
        ManagedDesktopSessionManager manager = Manager(host, allow: true);
        ManagedDesktopSession created = (await manager.CreateAsync(null, CancellationToken.None)).Value!;

        OperationResult<ManagedDesktopSession> active = await manager.ActivateAsync(created.Id, CancellationToken.None);

        Assert.Equal("helper_timeout", active.ErrorCode);
        ManagedDesktopSession stored = (await manager.GetAsync(created.Id)).Value!;
        Assert.Equal("open", stored.State);
        Assert.True(stored.InputSwitched);
        Assert.Contains("\"inputSwitched\":true", await File.ReadAllTextAsync(VestigoPaths.OwnedDesktopsPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Close_UnrestoredInputStaysOpenAndKeepsItsProcesses()
    {
        RecordingHost host = new() { RejectOp = DesktopHostOperations.Restore, RejectCode = "desktop_host_lost", FailRestore = true };
        RunningCleanup cleanup = new();
        ManagedDesktopSessionManager manager = Manager(host, allow: true, cleanup);
        ManagedDesktopSession created = (await manager.CreateAsync("terminate-owned", CancellationToken.None)).Value!;
        await AttachAsync(manager, created.Id, new OwnedDesktopProcess("job1", 42, false));
        Assert.True((await manager.ActivateAsync(created.Id, CancellationToken.None)).Success);

        OperationResult<ManagedDesktopSession> closed = await manager.CloseAsync(created.Id, "terminate-owned", CancellationToken.None);

        Assert.Equal("desktop_unavailable", closed.ErrorCode);
        ManagedDesktopSession stored = (await manager.GetAsync(created.Id)).Value!;
        Assert.Equal("recovery-failed", stored.State);
        Assert.True(stored.InputSwitched);
        Assert.Empty(cleanup.Terminated);
        Assert.DoesNotContain("close", host.Operations);
    }

    [Fact]
    public void RecoveryResult_WithNullError_IsSuccess()
    {
        string json = JsonSerializer.Serialize(DesktopCommandResult.Ready(4), SessionHelper.Json);
        OperationResult<DesktopCommandResult> parsed = SessionHelperClient.Parse<DesktopCommandResult>(json, 0);

        Assert.True(parsed.Success, parsed.Error);
        Assert.True(parsed.Value!.Success);
        Assert.Equal(4, parsed.Value.HostProcessId);
    }

    [Fact]
    public async Task RunProcess_ReleasesTheDesktopLeaseBeforeWaiting()
    {
        string root = Directory.CreateTempSubdirectory("vestigo-lease-").FullName;
        try
        {
            string executable = Path.Combine(root, "tool.exe");
            await File.WriteAllBytesAsync(executable, [0x4D, 0x5A]);
            RecordingLauncher launcher = new() { Hold = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously) };
            RecordingDirectory desktops = new();
            VestigoOptions options = new()
            {
                Roots = [new RootDefinition { Name = "tools", Path = root, Access = RootAccess.Read | RootAccess.Execute }],
                Processes = new ProcessesOptions { AllowPathLookup = false }
            };
            RootPolicy policy = new(options);
            ProcessTools tools = new(policy, new ExecutableResolver(policy, options), new JobManager(options, Path.Combine(root, "jobs")), launcher, new AppCloser(new SessionHelperClient(launcher)), desktops);
            Task<OperationResult<RunProcessResult>> running = tools.RunProcess(executable, context: "interactive", desktopSessionId: "abc", wait: 30);

            for (int attempt = 0; attempt < 50 && desktops.Attached.Count == 0; attempt++)
            {
                await Task.Delay(10);
            }

            Assert.NotEmpty(desktops.Attached);
            Assert.True(desktops.Released);
            launcher.Hold.TrySetResult(0);
            Assert.True((await running).Success);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunProcess_CancelBeforePublishKeepsTheLeaseUntilAttach()
    {
        string root = Directory.CreateTempSubdirectory("vestigo-lease-cancel-").FullName;
        try
        {
            string executable = Path.Combine(root, "tool.exe");
            await File.WriteAllBytesAsync(executable, [0x4D, 0x5A]);
            TaskCompletionSource publish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<int> hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            RecordingLauncher launcher = new() { PublishWhen = publish.Task, Hold = hold };
            RecordingDirectory desktops = new();
            VestigoOptions options = new()
            {
                Roots = [new RootDefinition { Name = "tools", Path = root, Access = RootAccess.Read | RootAccess.Execute }],
                Processes = new ProcessesOptions { AllowPathLookup = false }
            };
            RootPolicy policy = new(options);
            using CancellationTokenSource cancellation = new();
            ProcessTools tools = new(policy, new ExecutableResolver(policy, options), new JobManager(options, Path.Combine(root, "jobs")), launcher, new AppCloser(new SessionHelperClient(launcher)), desktops);
            Task<OperationResult<RunProcessResult>> running = tools.RunProcess(executable, context: "interactive", desktopSessionId: "abc", wait: 30, cancellationToken: cancellation.Token);
            await launcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            Assert.False(desktops.Released);
            Assert.Empty(desktops.Attached);
            publish.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running.ConfigureAwait(false));
            Assert.NotEmpty(desktops.Attached);
            Assert.True(desktops.Released);
            hold.TrySetResult(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnsupportedCheckpoint_BlocksNewSessionsAndStaysOnDisk()
    {
        Directory.CreateDirectory(_settings);
        await File.WriteAllTextAsync(VestigoPaths.OwnedDesktopsPath, """{"version":99}""");
        RecordingHost host = new();
        ManagedDesktopSessionManager manager = Manager(host, allow: true);
        await manager.StartAsync(CancellationToken.None);

        OperationResult<ManagedDesktopSession> created = await manager.CreateAsync(null, CancellationToken.None);

        Assert.Equal("desktop_checkpoint_unsupported", created.ErrorCode);
        Assert.Contains("\"version\":99", await File.ReadAllTextAsync(VestigoPaths.OwnedDesktopsPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enumeration_ClassifiesOwnedUserAndSecure()
    {
        RecordingHost host = new();
        ManagedDesktopSessionManager manager = Manager(host, allow: true);
        await manager.CreateAsync(null, CancellationToken.None);

        OperationResult<WindowStationDesktopList> listed = await manager.EnumerateAsync(CancellationToken.None);

        Assert.True(listed.Success, listed.Error);
        Assert.Contains(listed.Value!.Desktops, desktop => desktop.Kind == "user" && desktop.Name == "Default");
        Assert.Contains(listed.Value.Desktops, desktop => desktop.Kind == "secure" && desktop.Name == "Winlogon");
        Assert.Contains(listed.Value.Desktops, desktop => desktop.Kind == "owned" && desktop.HandleHeld);
    }

    [Fact]
    public async Task RunProcess_RejectsServiceContextAndTargetsTheManagedDesktop()
    {
        string root = Directory.CreateTempSubdirectory("vestigo-launch-").FullName;
        try
        {
            string executable = Path.Combine(root, "tool.exe");
            await File.WriteAllBytesAsync(executable, [0x4D, 0x5A]);
            RecordingLauncher launcher = new();
            RecordingDirectory desktops = new();
            VestigoOptions options = new()
            {
                Roots = [new RootDefinition { Name = "tools", Path = root, Access = RootAccess.Read | RootAccess.Execute }],
                Processes = new ProcessesOptions { AllowPathLookup = false }
            };
            RootPolicy policy = new(options);
            ProcessTools tools = new(policy, new ExecutableResolver(policy, options), new JobManager(options, Path.Combine(root, "jobs")), launcher, new AppCloser(new SessionHelperClient(launcher)), desktops);

            OperationResult<RunProcessResult> service = await tools.RunProcess(executable, context: "service", desktopSessionId: "abc");
            Assert.Equal("invalid_request", service.ErrorCode);
            Assert.Null(launcher.Desktop);

            OperationResult<RunProcessResult> interactive = await tools.RunProcess(executable, context: "interactive", desktopSessionId: "abc", env: new Dictionary<string, string> { ["VESTIGO_TEST"] = "1" });
            Assert.True(interactive.Success, interactive.Error);
            Assert.Equal(@"winsta0\Vestigo-0123456789ab", launcher.Desktop);
            Assert.Equal(77, interactive.Value!.Launch!.ProcessId);
            Assert.Equal(2, interactive.Value.Launch.SessionId);
            Assert.Equal("winsta0", interactive.Value.Launch.WindowStation);
            Assert.Equal("Vestigo-0123456789ab", interactive.Value.Launch.Desktop);
            Assert.False(interactive.Value.Launch.Elevated);
            Assert.Equal("limited", interactive.Value.Launch.ElevationType);
            OwnedDesktopProcess owned = Assert.Single(desktops.Attached);
            Assert.Equal(77, owned.ProcessId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Gate_IsOffByDefaultAndWarnsWhenEnabledWithoutTokens()
    {
        Assert.False(new DesktopOptions().AllowDesktopSessions);
        new DesktopOptions().Validate();
        Assert.Throws<InvalidOperationException>(() => new DesktopOptions { MaxDesktopSessions = 0 }.Validate());
        VestigoOptions open = new() { Modules = { ["Desktop"] = new ModuleSettings { Enabled = true } } };
        Assert.DoesNotContain("named test desktops", ModuleRegistryWarning(open), StringComparison.Ordinal);
        open.Desktop.AllowDesktopSessions = true;
        Assert.Contains("named test desktops", ModuleRegistryWarning(open), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsStation_RoundTripsWhenOptedIn()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive || Environment.GetEnvironmentVariable("VESTIGO_DESKTOP_SESSION_TESTS") != "1")
        {
            Assert.Skip("Set VESTIGO_DESKTOP_SESSION_TESTS=1 on an interactive Windows desktop.");
        }

        using WindowsDesktopStation station = new();
        string name = "Vestigo-" + Guid.NewGuid().ToString("N")[..12];
        DesktopCommandResult created = station.Execute(new DesktopCommand("create", DesktopHostOperations.Create, "create", name));
        try
        {
            Assert.True(created.Success, created.Error);
            Assert.Equal("Default", created.OriginalInputDesktop, StringComparer.OrdinalIgnoreCase);
            DesktopCommandResult activated = station.Execute(new DesktopCommand("activate", DesktopHostOperations.Activate, "create", name));
            Assert.True(activated.Success, activated.Error);
            Assert.True(activated.Activated);
            DesktopCommandResult restored = station.Execute(new DesktopCommand("restore", DesktopHostOperations.Restore, "create", name));
            Assert.True(restored.Success, restored.Error);
            Assert.True(restored.InputRestored);
        }
        finally
        {
            station.Execute(new DesktopCommand("close", DesktopHostOperations.Close, "create", name));
        }
    }

    private static string ModuleRegistryWarning(VestigoOptions options) =>
        Vestigo.Modules.ModuleRegistry.GetPrivilegedModuleWarning(options) ?? string.Empty;

    private ManagedDesktopSessionManager Manager(RecordingHost host, bool allow, IOwnedProcessCleanup? cleanup = null, TimeProvider? time = null) =>
        new(host, cleanup ?? new NoOwnedProcessCleanup(), new VestigoOptions { Desktop = { AllowDesktopSessions = allow } }, time ?? TimeProvider.System);

    private static LaunchRequest Request(string? desktop) =>
        new("tool.exe", [], null, new Dictionary<string, string>(), LaunchContext.Interactive, false, desktop);

    private static async Task AttachAsync(ManagedDesktopSessionManager manager, string id, OwnedDesktopProcess process)
    {
        OperationResult<ManagedDesktopLease> lease = await manager.LeaseForLaunchAsync(id, CancellationToken.None);
        Assert.True(lease.Success, lease.Error);
        lease.Value!.Attach(process);
        lease.Value.Dispose();
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RunningCleanup : IOwnedProcessCleanup
    {
        public List<int> Alive { get; init; } = [];

        public List<int> Terminated { get; } = [];

        public bool TerminatedBeforeClose { get; private set; }

        public IReadOnlyList<int> StillRunning(IReadOnlyList<OwnedDesktopProcess> processes) =>
            processes.Select(process => process.ProcessId).Where(Alive.Contains).ToList();

        public Task TerminateAsync(IReadOnlyList<OwnedDesktopProcess> processes, CancellationToken cancellationToken)
        {
            Terminated.AddRange(processes.Select(process => process.ProcessId));
            TerminatedBeforeClose = true;
            Alive.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHost : IDesktopHostClient
    {
        private readonly Dictionary<string, string> _owned = new(StringComparer.OrdinalIgnoreCase);
        private string _input = "Default";

        public List<string> Operations { get; } = [];

        public List<string> RecoveredNames { get; } = [];

        public bool Secure { get; init; }

        public bool RetainOnClose { get; set; }

        public bool FailEnumerate { get; set; }

        public HashSet<string> ExternallyPresent { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FailRestore { get; set; }

        public string? RestoreCode { get; init; }

        public CancellationTokenSource? Cancellation { get; set; }

        public string? RejectOp { get; set; }

        public string RejectCode { get; set; } = "helper_timeout";

        public Task<OperationResult<DesktopCommandResult>> SendAsync(DesktopCommand command, CancellationToken cancellationToken)
        {
            Operations.Add(command.Op);
            if (RejectOp is not null && command.Op == RejectOp)
            {
                return Task.FromResult(OperationResult<DesktopCommandResult>.Fail(RejectCode, "The desktop host did not answer in time."));
            }

            if (command.Op == DesktopHostOperations.Create && Cancellation is not null)
            {
                Cancellation.Cancel();
            }

            if (Secure && command.Op is DesktopHostOperations.Create or DesktopHostOperations.Activate)
            {
                return Task.FromResult(OperationResult<DesktopCommandResult>.Ok(DesktopCommandResult.Fail(command.RequestId, "secure_desktop_active", "Secure desktop.")));
            }

            DesktopCommandResult result = command.Op switch
            {
                DesktopHostOperations.Create => Create(command),
                DesktopHostOperations.Activate => Activate(command),
                DesktopHostOperations.Restore => Restore(command),
                DesktopHostOperations.Close => Close(command),
                DesktopHostOperations.Enumerate => FailEnumerate
                    ? DesktopCommandResult.Fail(command.RequestId, "desktop_unavailable", "Enumeration failed.")
                    : Enumerate(command),
                DesktopHostOperations.Shutdown => new DesktopCommandResult(command.RequestId, true, null, null, null, null, null, "WinSta0", 1, "Default", _input, true, true, false, false, 1, null),
                _ => DesktopCommandResult.Fail(command.RequestId, "invalid_request", "Unknown.")
            };
            return Task.FromResult(OperationResult<DesktopCommandResult>.Ok(result));
        }

        public Task<OperationResult<DesktopCommandResult>> RecoverAsync(DesktopCommand command, CancellationToken cancellationToken)
        {
            Operations.Add("recover:" + command.Op);
            RecoveredNames.Add(command.DesktopName ?? string.Empty);
            if (FailRestore && command.Op == DesktopHostOperations.RestorePhase)
            {
                return Task.FromResult(OperationResult<DesktopCommandResult>.Ok(
                    DesktopCommandResult.Fail(command.RequestId, "desktop_unavailable", "The original desktop could not be opened.")));
            }
            bool close = command.Op == DesktopHostOperations.ClosePhase;
            return Task.FromResult(OperationResult<DesktopCommandResult>.Ok(new DesktopCommandResult(command.RequestId, true, null, null, null, command.SessionId, command.DesktopName, "WinSta0", 1, command.OriginalInputDesktop, "Default", !close, close, false, false, 1, null)));
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            Operations.Add(DesktopHostOperations.Shutdown);
            return Task.CompletedTask;
        }

        private DesktopCommandResult Create(DesktopCommand command)
        {
            _owned[command.DesktopName!] = command.SessionId!;
            return new DesktopCommandResult(command.RequestId, true, null, null, null, command.SessionId, command.DesktopName, "WinSta0", 1, "Default", _input, false, false, false, false, 1, null);
        }

        private DesktopCommandResult Activate(DesktopCommand command)
        {
            _input = command.DesktopName!;
            return new DesktopCommandResult(command.RequestId, true, null, null, null, command.SessionId, command.DesktopName, "WinSta0", 1, "Default", _input, false, false, false, true, 1, null);
        }

        private DesktopCommandResult Restore(DesktopCommand command)
        {
            if (RestoreCode is not null)
            {
                return DesktopCommandResult.Fail(command.RequestId, RestoreCode, "The original desktop could not be opened.");
            }

            if (!string.Equals(_input, command.DesktopName, StringComparison.OrdinalIgnoreCase))
            {
                return DesktopCommandResult.Fail(command.RequestId, "input_desktop_changed", "Input moved.");
            }

            _input = "Default";
            return new DesktopCommandResult(command.RequestId, true, null, null, null, command.SessionId, command.DesktopName, "WinSta0", 1, "Default", _input, true, false, false, false, 1, null);
        }

        private DesktopCommandResult Close(DesktopCommand command)
        {
            _owned.Remove(command.DesktopName!);
            if (string.Equals(_input, command.DesktopName, StringComparison.OrdinalIgnoreCase))
            {
                _input = "Default";
            }

            if (RetainOnClose)
            {
                ExternallyPresent.Add(command.DesktopName!);
            }

            return new DesktopCommandResult(command.RequestId, true, null, null, null, command.SessionId, command.DesktopName, "WinSta0", 1, "Default", _input, true, true, RetainOnClose, false, 1, null);
        }

        private DesktopCommandResult Enumerate(DesktopCommand command)
        {
            List<EnumeratedDesktop> desktops =
            [
                new("Default", "user", false),
                new("Winlogon", "secure", false),
                .. _owned.Keys.Select(name => new EnumeratedDesktop(name, "owned", true)),
                .. ExternallyPresent.Select(name => new EnumeratedDesktop(name, "owned", false))
            ];
            return new DesktopCommandResult(command.RequestId, true, null, null, null, null, null, "WinSta0", 1, "Default", _input, false, false, false, false, 1, desktops);
        }
    }

    private sealed class RecordingDirectory : IManagedDesktopDirectory
    {
        public List<OwnedDesktopProcess> Attached { get; } = [];

        public bool Released { get; private set; }

        public Task<OperationResult<ManagedDesktopLease>> LeaseForLaunchAsync(string sessionId, CancellationToken cancellationToken)
        {
            ManagedDesktopLease lease = new(sessionId, @"winsta0\Vestigo-0123456789ab", Attached.Add, () => Released = true);
            return Task.FromResult(OperationResult<ManagedDesktopLease>.Ok(lease));
        }
    }

    private sealed class RecordingLauncher : IProcessLauncher
    {
        public string? Desktop { get; private set; }

        public TaskCompletionSource<int>? Hold { get; init; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? PublishWhen { get; init; }

        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) => OperationResult<bool>.Ok(true);

        public async Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken)
        {
            Desktop = request.Desktop;
            Started.TrySetResult();
            if (PublishWhen is not null)
            {
                await PublishWhen.ConfigureAwait(false);
            }

            request.Report?.Publish(new ProcessLaunchReport(77, 2, "winsta0", "Vestigo-0123456789ab", false, "limited"));
            return Hold is null ? 0 : await Hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
