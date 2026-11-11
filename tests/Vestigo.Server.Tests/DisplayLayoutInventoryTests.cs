using Vestigo.Desktop;

namespace Vestigo.Server.Tests;

public sealed class DisplayLayoutInventoryTests
{
    [Fact]
    public void Annotate_NamesCloneExtendAndMixedFromSourceIdentity()
    {
        DisplayPathLayout left = Path(source: 1, active: true);
        DisplayPathLayout right = Path(source: 2, active: true);
        DisplayPathLayout copy = Path(source: 1, active: true, target: 9);
        DisplayPathLayout idle = Path(source: 3, active: false);

        (string extend, IReadOnlyList<DisplayPathLayout> extended) = DisplayLayoutPlanner.Annotate([left, right, idle]);
        Assert.Equal("extend", extend);
        Assert.Equal("extend", extended[0].TopologyRole);
        Assert.Equal("inactive", extended[2].TopologyRole);

        (string clone, IReadOnlyList<DisplayPathLayout> cloned) = DisplayLayoutPlanner.Annotate([left, copy]);
        Assert.Equal("clone", clone);
        Assert.All(cloned, path => Assert.Equal("clone", path.TopologyRole));

        (string mixed, _) = DisplayLayoutPlanner.Annotate([left, copy, right]);
        Assert.Equal("mixed", mixed);
        Assert.Equal("single", DisplayLayoutPlanner.Annotate([left]).Topology);
        Assert.Equal("inactive", DisplayLayoutPlanner.Annotate([idle]).Topology);
    }

    [Fact]
    public void Fingerprint_IgnoresInactivePathsAndKeepsUnknownFlags()
    {
        DisplayPathLayout active = Path(source: 1, active: true);
        DisplayPathLayout idle = Path(source: 4, active: false);
        Assert.Equal(DisplayLayoutPlanner.Fingerprint([active]), DisplayLayoutPlanner.Fingerprint([active, idle]));
        Assert.Equal("active,virtualMode,boostRefresh,0x20", DisplayLayoutPlanner.DescribePathFlags(0x1 | 0x8 | 0x10 | 0x20));
        Assert.Equal(string.Empty, DisplayLayoutPlanner.DescribePathFlags(0));
    }

    [Fact]
    public void WindowsCapture_ReportsTopologyAndWithholdsUnvalidatedEdid()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && Environment.UserInteractive, "CCD topology reads need an interactive Windows session.");
        OperationResult<DisplayLayoutSnapshot> result = WindowsDisplayConfig.Capture();
        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Value!.Topology, new[] { "single", "clone", "extend", "mixed", "inactive" });
        Assert.All(result.Value.Paths, path =>
        {
            Assert.Equal(path.Active, path.PathFlagNames!.Contains("active", StringComparison.Ordinal));
            if (path.EdidIdsValid != true)
            {
                Assert.Null(path.EdidManufacturer);
                Assert.Null(path.EdidProductCode);
            }
        });
    }

    [Fact]
    public void Validate_MatchesOnlyActivePaths()
    {
        DisplayPathLayout active = Path(source: 1, active: true);
        DisplayPathLayout idle = Path(source: 4, active: false, target: 9);
        DisplayLayoutSnapshot snapshot = Snap(active, idle);
        DisplayTargetChange change = new(active.Identity.MonitorDevicePath, null, null, null, 0, 0, 1920, 1080, null, null, null);

        DisplayLayoutValidation validation = DisplayLayoutPlanner.Validate(snapshot, [change]);

        Assert.True(validation.Valid, string.Join(' ', validation.Errors));
        DisplayLayoutValidation idleOnly = DisplayLayoutPlanner.Validate(snapshot, [change with { MonitorDevicePath = null, TargetId = idle.Identity.TargetId, AdapterLuid = idle.Identity.AdapterLuid }]);
        Assert.Contains(idleOnly.Errors, error => error.Contains("not in the active topology", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_DoesNotRequireAnInactivePathToRemain()
    {
        DisplayPathLayout active = Path(source: 1, active: true);
        DisplayLayoutSnapshot withIdle = Snap(active, Path(source: 4, active: false));
        DisplayLayoutSnapshot activeOnly = Snap(active);
        Assert.Empty(DisplayLayoutPlanner.Compare(withIdle, activeOnly));
    }

    private static DisplayLayoutSnapshot Snap(params DisplayPathLayout[] paths) =>
        DisplayLayoutPlanner.WithFingerprint(new DisplayLayoutSnapshot(
            DateTimeOffset.UnixEpoch,
            1,
            DesktopCoordinates.PhysicalScreenPixels,
            string.Empty,
            paths));

    private static DisplayPathLayout Path(uint source, bool active, uint target = 2) => new(
        new DisplayTargetIdentity(@"\\?\DISPLAY#TEST", 1, "00000000:00000001", target, source),
        @"\\.\DISPLAY1",
        true,
        0,
        0,
        active ? 1920 : 0,
        active ? 1080 : 0,
        60,
        1,
        "IDENTITY",
        "IDENTITY",
        DesktopCoordinates.PhysicalScreenPixels,
        active);
}
