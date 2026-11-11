using Vestigo.Configuration;
using Vestigo.Desktop;

namespace Vestigo.Server.Tests;

public class VirtualDisplayTests : IDisposable
{
    private readonly string _previousSettings = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR") ?? string.Empty;
    private readonly string _settings;

    public VirtualDisplayTests()
    {
        _settings = Directory.CreateTempSubdirectory("vestigo-vdisplay-").FullName;
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", string.IsNullOrEmpty(_previousSettings) ? null : _previousSettings);
        Directory.Delete(_settings, recursive: true);
    }

    [Fact]
    public async Task UnavailableBackend_DoesNotTouchTheTopology()
    {
        RecordingDisplays displays = new();
        VirtualDisplayCoordinator coordinator = Coordinator(new UnavailableVirtualDisplayBackend(), displays, new ManualClock());

        OperationResult<VirtualDisplaySession> created = await coordinator.CreateAsync(Request(), CancellationToken.None);

        Assert.Equal(UnavailableVirtualDisplayBackend.Code, created.ErrorCode);
        Assert.Empty(displays.Operations);
        OperationResult<VirtualDisplayBackendStatus> status = await coordinator.StatusAsync(CancellationToken.None);
        Assert.False(status.Value!.CanCreate);
        Assert.Equal("none", status.Value.Backend);
        Assert.Equal(UnavailableVirtualDisplayBackend.Prerequisite, status.Value.Prerequisite);
        Assert.Contains("not that component", status.Value.Prerequisite, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shutdown_DisposeAndStopShareOneLifetime()
    {
        RecordingDisplays displays = new();
        VirtualDisplayCoordinator coordinator = Coordinator(new UnavailableVirtualDisplayBackend(), displays, new ManualClock());
        await coordinator.StartAsync(CancellationToken.None);
        coordinator.Dispose();
        await coordinator.StopAsync(CancellationToken.None);

        VirtualDisplayCoordinator second = Coordinator(new UnavailableVirtualDisplayBackend(), displays, new ManualClock());
        await second.StartAsync(CancellationToken.None);
        await second.StopAsync(CancellationToken.None);
        second.Dispose();
        await second.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Create_RestoresBeforeRemovingOwnedAndPreservesForeignDisplays()
    {
        List<string> log = [];
        RecordingDisplays displays = new(log);
        RecordingBackend backend = new(log);
        VirtualDisplayCoordinator coordinator = Coordinator(backend, displays, new ManualClock());

        OperationResult<VirtualDisplaySession> created = await coordinator.CreateAsync(Request(), CancellationToken.None);
        Assert.True(created.Success, created.Error);
        Assert.Equal(["snapshot", "create", "arrange"], log);
        Assert.Equal(["owned-key"], displays.ArrangedPaths);

        OperationResult<VirtualDisplaySession> closed = await coordinator.CloseAsync(created.Value!.Id, CancellationToken.None);

        Assert.True(closed.Success, closed.Error);
        Assert.Equal(["snapshot", "create", "arrange", "restore", "remove"], log);
        Assert.Equal(["disp-owned"], backend.Removed);
        Assert.Contains(@"\\.\DISPLAY1", displays.RestoredPaths);
        Assert.DoesNotContain(@"\\.\DISPLAY-IDLE", displays.RestoredPaths);
        Assert.DoesNotContain("disp-owned", displays.RestoredPaths);
    }

    [Fact]
    public async Task Close_RemovesAnOwnedDisplayWhenTheCheckpointHasNoActivePath()
    {
        List<string> log = [];
        RecordingDisplays displays = new(log) { ActivePaths = false };
        RecordingBackend backend = new(log);
        VirtualDisplayCoordinator coordinator = Coordinator(backend, displays, new ManualClock());

        OperationResult<VirtualDisplaySession> created = await coordinator.CreateAsync(Request(), CancellationToken.None);
        OperationResult<VirtualDisplaySession> closed = await coordinator.CloseAsync(created.Value!.Id, CancellationToken.None);

        Assert.True(closed.Success, closed.Error);
        Assert.Equal("closed", closed.Value!.State);
        Assert.Equal(["snapshot", "create", "arrange", "remove"], log);
        Assert.Equal(["disp-owned"], backend.Removed);
        Assert.Empty(displays.RestoredPaths);
    }

    [Fact]
    public async Task ArrangeFailure_RestoresThenRemoves()
    {
        List<string> log = [];
        RecordingDisplays displays = new(log) { RejectArrange = true };
        RecordingBackend backend = new(log);
        VirtualDisplayCoordinator coordinator = Coordinator(backend, displays, new ManualClock());

        OperationResult<VirtualDisplaySession> created = await coordinator.CreateAsync(Request(), CancellationToken.None);

        Assert.Equal("display_config_rejected", created.ErrorCode);
        Assert.Equal(["snapshot", "create", "arrange", "restore", "remove"], log);
        Assert.Equal(["disp-owned"], backend.Removed);
    }

    [Fact]
    public async Task Cancellation_RestoresThenRemoves()
    {
        List<string> log = [];
        RecordingDisplays displays = new(log);
        RecordingBackend backend = new(log) { CancelOnCreate = true };
        VirtualDisplayCoordinator coordinator = Coordinator(backend, displays, new ManualClock());
        using CancellationTokenSource cancellation = new();
        backend.Cancellation = cancellation;

        OperationResult<VirtualDisplaySession> created = await coordinator.CreateAsync(Request(), cancellation.Token);

        Assert.Equal("cancelled", created.ErrorCode);
        Assert.Equal(["disp-owned"], backend.Removed);
        Assert.Equal(["snapshot", "create", "restore", "remove"], log);
    }

    [Fact]
    public async Task LeaseExpiry_UsesTheSameCleanupOrder()
    {
        ManualClock clock = new();
        List<string> log = [];
        RecordingDisplays displays = new(log);
        RecordingBackend backend = new(log);
        VirtualDisplayCoordinator coordinator = Coordinator(backend, displays, clock);
        Assert.True((await coordinator.CreateAsync(Request(), CancellationToken.None)).Success);
        log.Clear();
        backend.Removed.Clear();
        clock.Now += TimeSpan.FromMinutes(11);

        await coordinator.SweepAsync();

        Assert.Equal(["restore", "remove"], log);
        Assert.Equal(["disp-owned"], backend.Removed);
    }

    [Fact]
    public async Task Restart_RestoresBeforeRemovingTheOwnedDisplay()
    {
        ManualClock clock = new();
        List<string> log = [];
        RecordingDisplays displays = new(log);
        RecordingBackend backend = new(log);
        VirtualDisplayCoordinator first = Coordinator(backend, displays, clock);
        Assert.True((await first.CreateAsync(Request(), CancellationToken.None)).Success);
        log.Clear();
        backend.Removed.Clear();

        VirtualDisplayCoordinator second = Coordinator(backend, displays, clock);
        await second.StartAsync(CancellationToken.None);
        await second.StopAsync(CancellationToken.None);

        Assert.Equal(["restore", "remove"], log);
        Assert.Equal(["disp-owned"], backend.Removed);
        Assert.Contains(@"\\.\DISPLAY1", displays.RestoredPaths);
    }

    [Fact]
    public async Task InvalidRequest_DoesNotTouchTheTopology()
    {
        List<string> log = [];
        RecordingDisplays displays = new(log);
        VirtualDisplayCoordinator coordinator = Coordinator(new RecordingBackend(log), displays, new ManualClock());

        Assert.Equal("invalid_request", (await coordinator.CreateAsync(new VirtualDisplayRequest(0, 1080, 60, 1, 0, 0, false), CancellationToken.None)).ErrorCode);
        Assert.Equal("invalid_request", (await coordinator.CreateAsync(Request() with { Primary = true }, CancellationToken.None)).ErrorCode);
        Assert.Empty(log);
    }

    [Fact]
    public async Task RestoreFailure_KeepsTheSessionAndDoesNotRemove()
    {
        List<string> log = [];
        RecordingDisplays displays = new(log) { RejectRestore = true };
        RecordingBackend backend = new(log);
        VirtualDisplayCoordinator coordinator = Coordinator(backend, displays, new ManualClock());
        OperationResult<VirtualDisplaySession> created = await coordinator.CreateAsync(Request(), CancellationToken.None);
        Assert.True(created.Success, created.Error);

        OperationResult<VirtualDisplaySession> closed = await coordinator.CloseAsync(created.Value!.Id, CancellationToken.None);

        Assert.Equal("display_config_rejected", closed.ErrorCode);
        Assert.Contains("\"state\":\"open\"", File.ReadAllText(VestigoPaths.OwnedVirtualDisplaysPath));
        Assert.Empty(backend.Removed);
        Assert.Equal("restore", log[^1]);
    }

    [Fact]
    public async Task RemoveFailure_KeepsTheSessionForRetry()
    {
        List<string> log = [];
        RecordingBackend backend = new(log) { FailRemove = true };
        VirtualDisplayCoordinator coordinator = Coordinator(backend, new RecordingDisplays(log), new ManualClock());
        OperationResult<VirtualDisplaySession> created = await coordinator.CreateAsync(Request(), CancellationToken.None);

        OperationResult<VirtualDisplaySession> closed = await coordinator.CloseAsync(created.Value!.Id, CancellationToken.None);

        Assert.Equal("virtual_display_remove_failed", closed.ErrorCode);
        Assert.Contains("\"state\":\"open\"", File.ReadAllText(VestigoPaths.OwnedVirtualDisplaysPath));
        Assert.Empty(backend.Removed);
        Assert.Contains("remove", log);
    }

    [Fact]
    public async Task UnreadableCheckpoint_DoesNotStopStartup()
    {
        Directory.CreateDirectory(VestigoPaths.SettingsDirectory);
        await File.WriteAllTextAsync(VestigoPaths.OwnedVirtualDisplaysPath, """{"version":"nope"}""");
        VirtualDisplayCoordinator coordinator = Coordinator(new RecordingBackend(), new RecordingDisplays(), new ManualClock());

        await coordinator.StartAsync(CancellationToken.None);

        Assert.Equal("virtual_display_checkpoint_unsupported", (await coordinator.CreateAsync(Request(), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task IdentityConflict_DoesNotRemoveTheExistingDisplay()
    {
        RecordingBackend backend = new() { IdentityKey = @"adapter|7|1|\\.\DISPLAY1", FailRemove = true };
        RecordingDisplays displays = new();
        VirtualDisplayCoordinator coordinator = Coordinator(backend, displays, new ManualClock());

        OperationResult<VirtualDisplaySession> created = await coordinator.CreateAsync(Request(), CancellationToken.None);

        Assert.Equal("virtual_display_identity_conflict", created.ErrorCode);
        Assert.Contains("not removed", created.Error);
        Assert.Empty(backend.Removed);
        Assert.DoesNotContain("remove", backend.Operations);
        Assert.DoesNotContain("arrange", displays.Operations);
        Assert.False(File.Exists(VestigoPaths.OwnedVirtualDisplaysPath));
    }

    private static VirtualDisplayCoordinator Coordinator(IVirtualDisplayBackend backend, RecordingDisplays displays, TimeProvider time) =>
        new(backend, displays, new VestigoOptions(), time);

    private static VirtualDisplayRequest Request() => new(1920, 1080, 60, 1, 1920, 0, false);

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingBackend(List<string>? log = null) : IVirtualDisplayBackend
    {
        public List<string> Operations { get; } = log ?? [];

        public List<string> Removed { get; } = [];

        public bool CancelOnCreate { get; init; }

        public bool FailRemove { get; init; }

        public string IdentityKey { get; init; } = "owned-key";

        public CancellationTokenSource? Cancellation { get; set; }

        public string Name => "fake";

        public bool CanCreate => true;

        public Task<OperationResult<VirtualDisplayBackendStatus>> StatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult<VirtualDisplayBackendStatus>.Ok(new("fake", true, null, null)));

        public Task<OperationResult<OwnedVirtualDisplay>> CreateAsync(VirtualDisplayRequest request, CancellationToken cancellationToken)
        {
            Operations.Add("create");
            if (CancelOnCreate)
            {
                Cancellation?.Cancel();
            }

            return Task.FromResult(OperationResult<OwnedVirtualDisplay>.Ok(new OwnedVirtualDisplay("disp-owned", IdentityKey)));
        }

        public Task<OperationResult<bool>> RemoveAsync(string displayId, CancellationToken cancellationToken)
        {
            Operations.Add("remove");
            if (FailRemove)
            {
                return Task.FromResult(OperationResult<bool>.Fail("remove_failed", "The backend kept the display."));
            }

            Removed.Add(displayId);
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }
    }

    private sealed class RecordingDisplays(List<string>? log = null) : IDisplayLayoutClient
    {
        public List<string> Operations { get; } = log ?? [];

        public List<string?> ArrangedPaths { get; } = [];

        public List<string?> RestoredPaths { get; } = [];

        public bool RejectArrange { get; init; }

        public bool RejectRestore { get; init; }

        public bool ActivePaths { get; init; } = true;

        public Task<OperationResult<DisplayLayoutSnapshot>> SnapshotAsync(bool elevated, CancellationToken cancellationToken)
        {
            Operations.Add("snapshot");
            DisplayTargetIdentity identity = new(@"\\.\DISPLAY1", 1, "adapter", 7, 1);
            DisplayPathLayout path = new(identity, @"\\.\DISPLAY1", true, 0, 0, 1920, 1080, 60, 1, "identity", "default", DesktopCoordinates.PhysicalScreenPixels);
            DisplayPathLayout idle = path with
            {
                Active = false,
                Width = 0,
                Height = 0,
                Identity = new DisplayTargetIdentity(@"\\.\DISPLAY-IDLE", 1, "adapter", 8, 2)
            };
            DisplayPathLayout[] paths = ActivePaths ? [path, idle] : [idle];
            return Task.FromResult(OperationResult<DisplayLayoutSnapshot>.Ok(
                new DisplayLayoutSnapshot(DateTimeOffset.UnixEpoch, 1, DesktopCoordinates.PhysicalScreenPixels, "fp", paths)));
        }

        public Task<OperationResult<DisplayLayoutApplyResult>> ApplyAsync(DisplayLayoutRequest request, bool elevated, CancellationToken cancellationToken)
        {
            if (request.Targets.Count == 0)
            {
                Operations.Add("restore");
                return Task.FromResult(OperationResult<DisplayLayoutApplyResult>.Fail("invalid_request", "A layout change needs 1 to 16 targets."));
            }

            bool arrange = request.Targets.Any(target => target.MonitorDevicePath == "owned-key");
            Operations.Add(arrange ? "arrange" : "restore");
            if (arrange)
            {
                ArrangedPaths.AddRange(request.Targets.Select(target => target.MonitorDevicePath));
                if (RejectArrange)
                {
                    return Task.FromResult(OperationResult<DisplayLayoutApplyResult>.Ok(
                        new DisplayLayoutApplyResult(false, true, false, 0, "display_config_rejected", "rejected", null, null)));
                }
            }
            else
            {
                RestoredPaths.AddRange(request.Targets.Select(target => target.MonitorDevicePath));
                if (RejectRestore)
                {
                    return Task.FromResult(OperationResult<DisplayLayoutApplyResult>.Fail("display_config_rejected", "The checkpoint could not be restored."));
                }
            }

            return Task.FromResult(OperationResult<DisplayLayoutApplyResult>.Ok(
                new DisplayLayoutApplyResult(true, true, !arrange, null, null, null, null, null)));
        }
    }
}
