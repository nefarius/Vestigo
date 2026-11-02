using Vestigo.Installer.Actions.Logic;

namespace Vestigo.Server.Tests;

public class AspNetRuntimePrerequisiteTests
{
    [Fact]
    public void LaunchCondition_KeepsX64CheckAndScopesRegistryFallbackToArm64()
    {
        Assert.Equal(
            "Installed OR DotNetCheckResult = 0 OR (WIX_NATIVE_MACHINE = 43620 AND VESTIGO_X64_ASPNET10)",
            AspNetRuntimePrerequisite.LaunchCondition);
        Assert.Equal(43620, AspNetRuntimePrerequisite.NativeArm64Machine);
        Assert.Equal(34404, AspNetRuntimePrerequisite.NativeX64Machine);
        Assert.Equal("DotNetCheckResult", AspNetRuntimePrerequisite.CheckProperty);
        Assert.Equal("VESTIGO_X64_ASPNET10", AspNetRuntimePrerequisite.RegistryProperty);
    }

    [Fact]
    public void RegistryMarker_IsOfficialX64AspNet10SharedFxKey()
    {
        Assert.Equal(
            @"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.AspNetCore.App",
            AspNetRuntimePrerequisite.RegistryPath);
        Assert.Equal(
            @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.AspNetCore.App",
            AspNetRuntimePrerequisite.RegistryKey);
        Assert.Equal("10.0.0", AspNetRuntimePrerequisite.RegistryValueName);
        Assert.Equal("10.0.0", AspNetRuntimePrerequisite.RuntimeVersion);
        Assert.Equal("minor", AspNetRuntimePrerequisite.RollForward);
        Assert.Equal("aspnet", AspNetRuntimePrerequisite.RuntimeType);
        Assert.Equal("x64", AspNetRuntimePrerequisite.Platform);
    }

    [Theory]
    [InlineData(false, 12290, 34404, false, false)]
    [InlineData(false, 0, 34404, false, true)]
    [InlineData(false, 12290, 43620, true, true)]
    [InlineData(false, 12290, 43620, false, false)]
    [InlineData(true, 12290, 34404, false, true)]
    public void IsSatisfied_MatchesTheFourPlatformCases(
        bool alreadyInstalled,
        int checkResult,
        int nativeMachine,
        bool x64AspNet10Present,
        bool expected)
    {
        Assert.Equal(
            expected,
            AspNetRuntimePrerequisite.IsSatisfied(alreadyInstalled, checkResult, nativeMachine, x64AspNet10Present));
    }

    [Fact]
    public void Installer_EmitsNetFxCheckUtilQueryAndArm64RegistryFallback()
    {
        string root = FindRepositoryRoot();
        string installer = File.ReadAllText(Path.Combine(root, "installer", "Program.cs"));
        string helper = File.ReadAllText(Path.Combine(root, "installer", "AspNetRuntimeCheck.cs"));

        Assert.Contains("AspNetRuntimeCheck.Apply(project)", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("SetNetPrerequisite", installer);
        Assert.Contains("DotNetCompatibilityCheck", helper, StringComparison.Ordinal);
        Assert.Contains("QueryNativeMachine", helper, StringComparison.Ordinal);
        Assert.Contains("Bitness", helper, StringComparison.Ordinal);
        Assert.Contains("always32", helper, StringComparison.Ordinal);
        Assert.Contains("WixExtension.Util", helper, StringComparison.Ordinal);
        Assert.Contains("WixExtension.NetFx", helper, StringComparison.Ordinal);
        Assert.Contains("PreferredVersion = \"5.0.2\"", helper, StringComparison.Ordinal);
        Assert.Contains("AspNetRuntimePrerequisite.LaunchCondition", helper, StringComparison.Ordinal);
        Assert.Contains("AspNetRuntimePrerequisite.RegistryKey", helper, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vestigo.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
