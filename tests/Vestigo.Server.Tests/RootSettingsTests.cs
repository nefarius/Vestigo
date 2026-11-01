using Microsoft.Extensions.Configuration;

using Vestigo.Configuration;
using Vestigo.Installer.Actions.Logic;

namespace Vestigo.Server.Tests;

public class RootSettingsTests
{
    [Fact]
    public void ReadExisting_ParsesModeAccessAndCustomRoots()
    {
        ExistingRootSettings? existing = RootSettings.ReadExisting(
            """{ "Vestigo": { "VolumeRoots": "AllVolumes", "VolumeRootAccess": "Read, Write", "Roots": [ { "Name": "drops", "Path": "D:\\drops" } ] } }""");

        Assert.NotNull(existing);
        Assert.Equal("AllVolumes", existing.VolumeRoots);
        Assert.Equal("Read, Write", existing.VolumeRootAccess);
        Assert.True(existing.HasCustomRoots);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "Other": 1 }""")]
    public void ReadExisting_ReturnsNullWhenUnusable(string? json)
    {
        Assert.Null(RootSettings.ReadExisting(json));
    }

    [Fact]
    public void ReadExisting_ReportsNoCustomRootsWhenRootsMissingOrEmpty()
    {
        ExistingRootSettings? missing = RootSettings.ReadExisting("""{ "Vestigo": { "ListenPort": 1 } }""");
        ExistingRootSettings? empty = RootSettings.ReadExisting("""{ "Vestigo": { "Roots": [] } }""");

        Assert.NotNull(missing);
        Assert.Null(missing.VolumeRoots);
        Assert.False(missing.HasCustomRoots);
        Assert.NotNull(empty);
        Assert.False(empty.HasCustomRoots);
    }

    [Fact]
    public void Resolve_DefaultsToFixedWithReadWriteExecute()
    {
        ResolvedRootSettings resolved = RootSettings.Resolve(null, null, null);

        Assert.Equal(RootSettings.ModeFixed, resolved.Mode);
        Assert.Equal(RootSettings.AccessReadWriteExecute, resolved.Access);
        Assert.Equal(RootSettings.ServerFixedVolumes, resolved.VolumeRootsValue);
        Assert.Equal(RootSettings.ServerReadWriteExecute, resolved.VolumeRootAccessValue);
    }

    [Fact]
    public void Resolve_UpgradeWithCustomRootsAndNoVolumeKeyDefaultsToCustom()
    {
        ExistingRootSettings existing = new(null, null, hasCustomRoots: true);

        ResolvedRootSettings resolved = RootSettings.Resolve(null, null, existing, isUpgrade: true);

        Assert.Equal(RootSettings.ModeCustom, resolved.Mode);
        Assert.Equal(RootSettings.ServerNone, resolved.VolumeRootsValue);
    }

    [Fact]
    public void Resolve_UpgradeWithoutCustomRootsDefaultsToFixed()
    {
        ExistingRootSettings existing = new(null, null, hasCustomRoots: false);

        ResolvedRootSettings resolved = RootSettings.Resolve(null, null, existing, isUpgrade: true);

        Assert.Equal(RootSettings.ModeFixed, resolved.Mode);
    }

    [Fact]
    public void Resolve_UsesExistingModeAndAccess()
    {
        ExistingRootSettings existing = new("SystemDrive", "Read", hasCustomRoots: true);

        ResolvedRootSettings resolved = RootSettings.Resolve(null, null, existing, isUpgrade: true);

        Assert.Equal(RootSettings.ModeSystem, resolved.Mode);
        Assert.Equal(RootSettings.AccessRead, resolved.Access);
        Assert.Equal(RootSettings.ServerSystemDrive, resolved.VolumeRootsValue);
        Assert.Equal(RootSettings.ServerRead, resolved.VolumeRootAccessValue);
    }

    [Theory]
    [InlineData("CUSTOM", "r", RootSettings.ModeCustom, RootSettings.AccessRead, RootSettings.ServerNone, RootSettings.ServerRead)]
    [InlineData("system", "RW", RootSettings.ModeSystem, RootSettings.AccessReadWrite, RootSettings.ServerSystemDrive, RootSettings.ServerReadWrite)]
    [InlineData("all", "rwx", RootSettings.ModeAll, RootSettings.AccessReadWriteExecute, RootSettings.ServerAllVolumes, RootSettings.ServerReadWriteExecute)]
    [InlineData("live", "r", RootSettings.ModeLive, RootSettings.AccessRead, RootSettings.ServerAllVolumesLive, RootSettings.ServerRead)]
    public void Resolve_AcceptsExplicitValues(
        string mode,
        string access,
        string expectedMode,
        string expectedAccess,
        string serverMode,
        string serverAccess)
    {
        ResolvedRootSettings resolved = RootSettings.Resolve(mode, access, null);

        Assert.Equal(expectedMode, resolved.Mode);
        Assert.Equal(expectedAccess, resolved.Access);
        Assert.Equal(serverMode, resolved.VolumeRootsValue);
        Assert.Equal(serverAccess, resolved.VolumeRootAccessValue);
    }

    [Theory]
    [InlineData("sideways", "rwx")]
    [InlineData("fixed", "write")]
    public void Resolve_RejectsInvalidValues(string mode, string access)
    {
        Assert.Throws<ArgumentException>(() => RootSettings.Resolve(mode, access, null));
    }

    [Fact]
    public void Merge_WritesRootsKeysAndPreservesOthers()
    {
        ResolvedSettings listen = ListenSettings.Resolve("localhost", "none", "5098", null, null);
        ResolvedRootSettings roots = RootSettings.Resolve("live", "rw", null);
        const string existing = """{ "Vestigo": { "SymbolServer": "https://sym", "Roots": [ { "Name": "drops", "Path": "D:\\drops" } ] } }""";

        string merged = ListenSettings.Merge(existing, listen, roots: roots);
        ExistingRootSettings? read = RootSettings.ReadExisting(merged);

        Assert.NotNull(read);
        Assert.Equal("AllVolumesLive", read.VolumeRoots);
        Assert.Equal("Read, Write", read.VolumeRootAccess);
        Assert.True(read.HasCustomRoots);
        Assert.Contains("\"SymbolServer\"", merged);
        Assert.Contains("\"drops\"", merged);
    }

    [Fact]
    public void Merge_WithoutRoots_LeavesVolumeKeysAlone()
    {
        const string existing = """{ "Vestigo": { "VolumeRoots": "SystemDrive", "VolumeRootAccess": "Read" } }""";

        string merged = ListenSettings.Merge(existing, ListenSettings.Resolve("localhost", "none", "5098", null, null));
        ExistingRootSettings? read = RootSettings.ReadExisting(merged);

        Assert.NotNull(read);
        Assert.Equal("SystemDrive", read.VolumeRoots);
        Assert.Equal("Read", read.VolumeRootAccess);
    }

    [Fact]
    public void Merge_MatchesVolumeKeysCaseInsensitively()
    {
        const string existing = """{ "Vestigo": { "volumeroots": "None", "volumerootaccess": "Read" } }""";
        ResolvedRootSettings roots = RootSettings.Resolve("fixed", "rwx", null);

        string merged = ListenSettings.Merge(
            existing,
            ListenSettings.Resolve("localhost", "none", "5098", null, null),
            roots: roots);

        Assert.Contains("\"volumeroots\": \"FixedVolumes\"", merged);
        Assert.Contains("\"volumerootaccess\": \"Read, Write, Execute\"", merged);
        Assert.DoesNotContain("\"VolumeRoots\"", merged);
    }

    [Fact]
    public void Merge_BindsToServerOptions()
    {
        string merged = ListenSettings.Merge(
            null,
            ListenSettings.Resolve("localhost", "none", "5098", null, null),
            roots: RootSettings.Resolve("all", "rw", null));

        VestigoOptions options = new();
        new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(merged)))
            .Build()
            .GetSection("Vestigo")
            .Bind(options);

        options.Validate();
        Assert.Equal(VolumeRootMode.AllVolumes, options.VolumeRoots);
        Assert.Equal(RootAccess.Read | RootAccess.Write, options.VolumeRootAccess);
    }
}
