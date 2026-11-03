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
        Assert.Equal("10.0.0", AspNetRuntimePrerequisite.RuntimeVersion);
        Assert.Equal("minor", AspNetRuntimePrerequisite.RollForward);
        Assert.Equal("aspnet", AspNetRuntimePrerequisite.RuntimeType);
        Assert.Equal("x64", AspNetRuntimePrerequisite.Platform);
    }

    [Theory]
    [InlineData("10.0.0", true)]
    [InlineData("10.0.14", true)]
    [InlineData("10.0.14.1", false)]
    [InlineData("10.0", false)]
    [InlineData("10.1.0", false)]
    [InlineData("11.0.0", false)]
    [InlineData("9.0.14", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCompatibleValueName_AcceptsAspNet10PatchNames(string? name, bool expected)
    {
        Assert.Equal(expected, AspNetRuntimePrerequisite.IsCompatibleValueName(name));
    }

    [Fact]
    public void HasCompatibleRuntime_AcceptsATenZeroPatchAmongOtherVersions()
    {
        Assert.True(AspNetRuntimePrerequisite.HasCompatibleRuntime(["8.0.33", "10.0.14", "9.0.13"]));
        Assert.False(AspNetRuntimePrerequisite.HasCompatibleRuntime(["8.0.33", "9.0.13"]));
        Assert.False(AspNetRuntimePrerequisite.HasCompatibleRuntime(null));
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
        string actions = File.ReadAllText(Path.Combine(root, "installer", "Vestigo.Installer.Actions", "CustomActions.cs"));

        Assert.Contains("AspNetRuntimeCheck.Apply(project)", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("SetNetPrerequisite", installer);
        Assert.Contains("DotNetCompatibilityCheck", helper, StringComparison.Ordinal);
        Assert.Contains("QueryNativeMachine", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("RegistrySearch", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("RegistryValueName", helper, StringComparison.Ordinal);
        Assert.Contains("AspNetRuntimePrerequisite.DetectAction", helper, StringComparison.Ordinal);
        Assert.Contains("Wix4QueryNativeMachine_X64", helper, StringComparison.Ordinal);
        Assert.Contains("override \" + QueryNativeMachineAction", helper, StringComparison.Ordinal);
        Assert.Contains("Secure", helper, StringComparison.Ordinal);
        Assert.Contains("After\", \"AppSearch\"", helper, StringComparison.Ordinal);
        Assert.Contains("After\", QueryNativeMachineAction", helper, StringComparison.Ordinal);
        Assert.Contains("Wix + \"LaunchConditions\"", helper, StringComparison.Ordinal);
        Assert.Contains("After\", AspNetRuntimePrerequisite.DetectAction", helper, StringComparison.Ordinal);
        Assert.Contains("NotInstalled", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Before\"", helper, StringComparison.Ordinal);
        Assert.Contains("WixExtension.Util", helper, StringComparison.Ordinal);
        Assert.Contains("WixExtension.NetFx", helper, StringComparison.Ordinal);
        Assert.Contains("PreferredVersion = \"5.0.2\"", helper, StringComparison.Ordinal);
        Assert.Contains("AspNetRuntimePrerequisite.LaunchCondition", helper, StringComparison.Ordinal);
        Assert.Contains("HasCompatibleRuntime(ReadX64AspNetValueNames())", actions, StringComparison.Ordinal);
        Assert.Contains("RegistryView.Registry32", actions, StringComparison.Ordinal);
        Assert.Contains("AspNetRuntimePrerequisite.RegistryKey", actions, StringComparison.Ordinal);
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
