using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

internal sealed class FakeVolumeEnumerator : IVolumeEnumerator
{
    public List<VolumeInfo> Volumes { get; } = [];

    public string? SystemDrive { get; set; }

    public Exception? Failure { get; set; }

    public VolumeEnumeration Enumerate() =>
        Failure is null ? VolumeEnumeration.Succeeded([.. Volumes]) : VolumeEnumeration.FromError(Failure);

    public string? ResolveSystemDrive() => SystemDrive;
}

internal sealed class ControllableTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan delta) => UtcNow += delta;
}

public class VolumeRootSourceTests
{
    private static VestigoOptions Options(
        VolumeRootMode mode,
        RootAccess access = RootAccess.Read | RootAccess.Write | RootAccess.Execute) =>
        new() { VolumeRoots = mode, VolumeRootAccess = access };

    [Fact]
    public void None_ReturnsNoRoots()
    {
        FakeVolumeEnumerator enumerator = new()
        {
            SystemDrive = @"C:\",
            Volumes = { new VolumeInfo(@"C:\", DriveType.Fixed, true) }
        };

        IReadOnlyList<RootDefinition> roots = new VolumeRootSource(Options(VolumeRootMode.None), enumerator).Current;

        Assert.Empty(roots);
    }

    [Fact]
    public void SystemDrive_UsesResolvedPath()
    {
        FakeVolumeEnumerator enumerator = new() { SystemDrive = @"C:\" };

        RootDefinition root = Assert.Single(new VolumeRootSource(Options(VolumeRootMode.SystemDrive), enumerator).Current);

        Assert.Equal("drive-c", root.Name);
        Assert.Equal(@"C:\", root.Path);
        Assert.Equal(RootAccess.Read | RootAccess.Write | RootAccess.Execute, root.Access);
    }

    [Fact]
    public void FixedVolumes_IncludesOnlyReadyFixedDrives()
    {
        FakeVolumeEnumerator enumerator = new()
        {
            Volumes =
            {
                new VolumeInfo(@"C:\", DriveType.Fixed, true),
                new VolumeInfo(@"D:\", DriveType.Fixed, false),
                new VolumeInfo(@"E:\", DriveType.Removable, true),
                new VolumeInfo(@"Z:\", DriveType.Network, true)
            }
        };

        IReadOnlyList<RootDefinition> roots = new VolumeRootSource(Options(VolumeRootMode.FixedVolumes), enumerator).Current;

        RootDefinition root = Assert.Single(roots);
        Assert.Equal("drive-c", root.Name);
    }

    [Fact]
    public void AllVolumes_IncludesReadyFixedRemovableNetworkCdRomAndRam()
    {
        FakeVolumeEnumerator enumerator = new()
        {
            Volumes =
            {
                new VolumeInfo(@"C:\", DriveType.Fixed, true),
                new VolumeInfo(@"E:\", DriveType.Removable, true),
                new VolumeInfo(@"Z:\", DriveType.Network, true),
                new VolumeInfo(@"F:\", DriveType.CDRom, true),
                new VolumeInfo(@"R:\", DriveType.Ram, true),
                new VolumeInfo(@"X:\", DriveType.Unknown, true),
                new VolumeInfo(@"Y:\", DriveType.Removable, false)
            }
        };

        IReadOnlyList<RootDefinition> roots = new VolumeRootSource(Options(VolumeRootMode.AllVolumes), enumerator).Current;

        Assert.Equal(["drive-c", "drive-e", "drive-z", "drive-f", "drive-r"], roots.Select(root => root.Name));
    }

    [Fact]
    public void AllVolumes_SkipsUncAndUnqualifiedPaths()
    {
        FakeVolumeEnumerator enumerator = new()
        {
            Volumes =
            {
                new VolumeInfo(@"\\server\share", DriveType.Network, true),
                new VolumeInfo("relative", DriveType.Fixed, true),
                new VolumeInfo(@"D:\", DriveType.Fixed, true)
            }
        };

        RootDefinition root = Assert.Single(new VolumeRootSource(Options(VolumeRootMode.AllVolumes), enumerator).Current);

        Assert.Equal("drive-d", root.Name);
    }

    [Fact]
    public void SnapshotModes_DoNotSeeLaterArrivals()
    {
        FakeVolumeEnumerator enumerator = new()
        {
            Volumes = { new VolumeInfo(@"C:\", DriveType.Fixed, true) }
        };
        VolumeRootSource source = new(Options(VolumeRootMode.AllVolumes), enumerator);

        Assert.Single(source.Current);
        enumerator.Volumes.Add(new VolumeInfo(@"E:\", DriveType.Removable, true));

        Assert.Equal("drive-c", Assert.Single(source.Current).Name);
    }

    [Fact]
    public void LiveMode_PicksUpArrivalsAndRemovalsAfterCacheExpires()
    {
        FakeVolumeEnumerator enumerator = new()
        {
            Volumes = { new VolumeInfo(@"C:\", DriveType.Fixed, true) }
        };
        ControllableTimeProvider clock = new();
        VolumeRootSource source = new(Options(VolumeRootMode.AllVolumesLive), enumerator, clock);

        Assert.Equal("drive-c", Assert.Single(source.Current).Name);

        enumerator.Volumes.Add(new VolumeInfo(@"E:\", DriveType.Removable, true));
        Assert.Equal("drive-c", Assert.Single(source.Current).Name);

        clock.Advance(VolumeRootSource.LiveCacheDuration);
        Assert.Equal(["drive-c", "drive-e"], source.Current.Select(root => root.Name));

        enumerator.Volumes.RemoveAt(0);
        clock.Advance(VolumeRootSource.LiveCacheDuration);
        Assert.Equal("drive-e", Assert.Single(source.Current).Name);
    }

    [Fact]
    public void SnapshotModes_RetryAfterEnumerationFailure()
    {
        FakeVolumeEnumerator enumerator = new()
        {
            Failure = new IOException("busy"),
            Volumes = { new VolumeInfo(@"C:\", DriveType.Fixed, true) }
        };
        VolumeRootSource source = new(Options(VolumeRootMode.FixedVolumes), enumerator);

        Assert.Empty(source.Current);

        enumerator.Failure = null;
        Assert.Equal("drive-c", Assert.Single(source.Current).Name);

        enumerator.Volumes.Add(new VolumeInfo(@"D:\", DriveType.Fixed, true));
        Assert.Equal("drive-c", Assert.Single(source.Current).Name);
    }

    [Fact]
    public void AppliesConfiguredAccessFlags()
    {
        FakeVolumeEnumerator enumerator = new() { SystemDrive = @"C:\" };

        RootDefinition root = Assert.Single(
            new VolumeRootSource(Options(VolumeRootMode.SystemDrive, RootAccess.Read), enumerator).Current);

        Assert.Equal(RootAccess.Read, root.Access);
    }

    [Fact]
    public void DriveInfoEnumerator_ResolvesSystemDrive()
    {
        string? root = new DriveInfoVolumeEnumerator().ResolveSystemDrive();

        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.True(Path.IsPathFullyQualified(root!));
        string trimmed = root!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.Equal(2, trimmed.Length);
        Assert.Equal(':', trimmed[1]);
    }
}
