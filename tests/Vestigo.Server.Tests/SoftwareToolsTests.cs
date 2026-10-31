using Vestigo.Configuration;
using Vestigo.Jobs;
using Vestigo.Modules;
using Vestigo.Software;

namespace Vestigo.Server.Tests;

public class SoftwareToolsTests : IDisposable
{
    private const string Guid1 = "{11111111-2222-3333-4444-555555555555}";
    private readonly string _root = Directory.CreateTempSubdirectory("vestigo-software-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static InstalledSoftware Item(
        string id = "machine-x64:App",
        string name = "App",
        string type = InstallerTypes.Unknown,
        string scope = "machine",
        string? uninstall = null,
        string? quiet = null,
        bool noRemove = false,
        bool system = false,
        string? parent = null,
        string? publisher = "Acme") =>
        new(id, id.Split(':')[^1], name, "1.0", publisher, null, null, null, uninstall, quiet, null, null, scope, "x64",
            null, type == InstallerTypes.Msi, type == InstallerTypes.Msi ? Guid1 : null, type, system, noRemove, parent);

    private string TempExe(string name, string content = "MZ")
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class FakeInventory(params InstalledSoftware[] items) : ISoftwareInventory
    {
        public List<InstalledSoftware> Items { get; } = [.. items];

        public IReadOnlyList<InstalledSoftware> List() => Items;
    }

    private sealed class FakeRunner(int exitCode = 0) : IUninstallRunner
    {
        public UninstallPlan? Last { get; private set; }

        public Task<int> RunAsync(UninstallPlan plan, JobContext output, CancellationToken cancellationToken)
        {
            Last = plan;
            output.WriteLine("ran");
            return Task.FromResult(exitCode);
        }
    }

    private SoftwareTools Tools(FakeInventory inventory, FakeRunner? runner = null, bool allow = true, Action<VestigoOptions>? tweak = null)
    {
        VestigoOptions options = new() { Software = { AllowUninstall = allow } };
        tweak?.Invoke(options);
        return new SoftwareTools(
            inventory,
            new UninstallPlanner(),
            runner ?? new FakeRunner(),
            new JobManager(options, Path.Combine(_root, "jobs")),
            options)
        {
            LogDirectory = Path.Combine(_root, "logs")
        };
    }

    [Fact]
    public void List_FiltersAndPages()
    {
        FakeInventory inventory = new(
            Item("machine-x64:a", "Alpha"),
            Item("machine-x64:b", "Beta", publisher: "Other"),
            Item("user:S-1:c", "Gamma", scope: "user"),
            Item("machine-x64:d", "Delta", system: true),
            Item("machine-x64:e", "Update", parent: "Alpha"));
        SoftwareTools tools = Tools(inventory);

        Assert.Equal(["Alpha", "Beta", "Gamma"], tools.ListInstalledSoftware().Value!.Software.Select(s => s.Name));
        Assert.Equal(["Gamma"], tools.ListInstalledSoftware(scope: "user").Value!.Software.Select(s => s.Name));
        Assert.Equal(["Beta"], tools.ListInstalledSoftware(publisherContains: "oth").Value!.Software.Select(s => s.Name));
        Assert.Equal(5, tools.ListInstalledSoftware(includeSystemComponents: true, includeUpdates: true).Value!.Total);

        SoftwareList page = tools.ListInstalledSoftware(limit: 1).Value!;
        Assert.True(page.Truncated);
        Assert.Equal(3, page.Total);
        Assert.Equal("invalid_scope", tools.ListInstalledSoftware(scope: "x").ErrorCode);
    }

    [Fact]
    public void Get_ReturnsPlanOrReason()
    {
        SoftwareTools tools = Tools(new FakeInventory(Item("machine-x64:m", "Msi", InstallerTypes.Msi), Item()));

        Assert.NotNull(tools.GetInstalledSoftware("machine-x64:m").Value!.Plan);
        Assert.Equal("no_uninstall_string", tools.GetInstalledSoftware("machine-x64:App").Value!.PlanErrorCode);
        Assert.Equal("not_found", tools.GetInstalledSoftware("nope").ErrorCode);
    }

    [Fact]
    public void Detector_ClassifiesInstallers()
    {
        string inno = TempExe("unins000.exe");
        File.WriteAllText(Path.Combine(_root, "unins000.dat"), "x");
        string nsis = TempExe("uninst.exe", "stub Nullsoft Install System v3 stub");
        string plain = TempExe("other.exe");

        static Dictionary<string, object?> V(string uninstall, params (string, object)[] extra)
        {
            Dictionary<string, object?> values = new(StringComparer.OrdinalIgnoreCase) { ["UninstallString"] = uninstall };
            foreach ((string key, object value) in extra)
            {
                values[key] = value;
            }

            return values;
        }

        Assert.Equal(InstallerTypes.Msi, InstallerDetector.Detect("App", V("x", ("WindowsInstaller", 1))));
        Assert.Equal(InstallerTypes.Msi, InstallerDetector.Detect(Guid1, V("MsiExec.exe /I" + Guid1)));
        Assert.Equal(InstallerTypes.Burn, InstallerDetector.Detect("App", V(plain, ("BundleCachePath", "c"))));
        Assert.Equal(InstallerTypes.Inno, InstallerDetector.Detect("App", V($"\"{inno}\"")));
        Assert.Equal(InstallerTypes.Nsis, InstallerDetector.Detect("App", V(nsis)));
        Assert.Equal(InstallerTypes.InstallShield, InstallerDetector.Detect("App", V(@"C:\Program Files (x86)\InstallShield Installation Information\setup.exe -runfromtemp")));
        Assert.Equal(InstallerTypes.Unknown, InstallerDetector.Detect("App", V(plain)));
    }

    [Fact]
    public void Planner_SplitsQuotedAndUnquotedPaths()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Some App"));
        string exe = TempExe(Path.Combine("Some App", "un.exe"));

        SplitCommand? quoted = UninstallPlanner.SplitCommandLine($"\"{exe}\" /x \"a b\"");
        SplitCommand? unquoted = UninstallPlanner.SplitCommandLine($"{exe} /silent");

        Assert.Equal(exe, quoted!.Executable);
        Assert.Equal(["/x", "a b"], quoted.Arguments);
        Assert.Equal(exe, unquoted!.Executable);
        Assert.Equal(["/silent"], unquoted.Arguments);
        Assert.Null(UninstallPlanner.SplitCommandLine("\"unterminated"));
    }

    [Fact]
    public void Planner_TokenizesBurnWithCrtRulesAndOthersByQuoteToggle()
    {
        string exe = TempExe("t.exe");

        Assert.Equal(["/x", "a\"b"], UninstallPlanner.SplitCommandLine($"\"{exe}\" /x \"a\\\"b\"", InstallerTypes.Burn)!.Arguments);
        Assert.Equal([@"C:\dir\", "next"], UninstallPlanner.SplitCommandLine($"\"{exe}\" \"C:\\dir\\\\\" next", InstallerTypes.Burn)!.Arguments);
        Assert.Equal([@"/LOG=C:\logs\", "next"], UninstallPlanner.SplitCommandLine($"\"{exe}\" \"/LOG=C:\\logs\\\" next", InstallerTypes.Inno)!.Arguments);
        Assert.Equal([@"/D=C:\a b\"], UninstallPlanner.SplitCommandLine($"\"{exe}\" /D=\"C:\\a b\\\"", InstallerTypes.Nsis)!.Arguments);
    }

    [Fact]
    public void Planner_QuietStringGetsRestartSuppressionAndLocalPathsOnly()
    {
        UninstallPlanner planner = new();
        string exe = TempExe("q.exe");

        Assert.Equal(["/quiet", "/norestart"], planner.Plan(Item(type: InstallerTypes.Burn, quiet: $"\"{exe}\" /quiet"), null).Value!.Arguments);
        Assert.Equal(["/SILENT", "/NORESTART"], planner.Plan(Item(type: InstallerTypes.Inno, quiet: $"\"{exe}\" /SILENT"), null).Value!.Arguments);
        Assert.Equal(["/q"], planner.Plan(Item(type: InstallerTypes.Nsis, quiet: $"\"{exe}\" /q"), null).Value!.Arguments);
        Assert.Equal("invalid_uninstall_string", planner.Plan(Item(type: InstallerTypes.Nsis, uninstall: @"""\\server\share\u.exe"""), null).ErrorCode);
        Assert.False(InstallerDetector.IsLocalDrivePath(@"\\server\share\u.exe"));
        Assert.False(InstallerDetector.IsLocalDrivePath(@"\\?\C:\u.exe"));
        Assert.True(InstallerDetector.IsLocalDrivePath(@"C:\u.exe"));
    }

    [Fact]
    public void SafeLocalPath_RejectsReparsePointsAndAcceptsPlainFiles()
    {
        string target = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        string exe = TempExe(Path.Combine("real", "u.exe"));
        string link = Path.Combine(_root, "link");
        bool linked = true;
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            linked = false; // No symlink privilege on this machine.
        }

        Assert.True(InstallerDetector.IsSafeLocalPath(exe));
        Assert.False(InstallerDetector.IsSafeLocalPath(@"\\server\share\u.exe"));
        if (linked)
        {
            Assert.False(InstallerDetector.IsSafeLocalPath(Path.Combine(link, "u.exe")));
        }
    }

    [Fact]
    public void Detector_IgnoresMalformedWindowsInstallerValue()
    {
        Dictionary<string, object?> values = new(StringComparer.OrdinalIgnoreCase)
        {
            ["WindowsInstaller"] = "oops",
            ["UninstallString"] = "x"
        };
        Assert.Equal(InstallerTypes.Unknown, InstallerDetector.Detect("App", values));
        values["WindowsInstaller"] = new byte[] { 1 };
        Assert.Equal(InstallerTypes.Unknown, InstallerDetector.Detect("App", values));
    }

    [Fact]
    public void Planner_MsiIgnoresStoredStringAndUsesQuiet()
    {
        UninstallPlanner planner = new();
        UninstallPlan msi = planner.Plan(Item(type: InstallerTypes.Msi, uninstall: "MsiExec.exe /I" + Guid1), @"C:\l.log").Value!;

        Assert.EndsWith("msiexec.exe", msi.Executable, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["/x", Guid1, "/qn", "/norestart", "/l*v", @"C:\l.log"], msi.Arguments);

        string exe = TempExe("quiet.exe");
        UninstallPlan quiet = planner.Plan(Item(uninstall: "\"c:\\nope.exe\"", quiet: $"\"{exe}\" --silent"), null).Value!;
        Assert.Equal(exe, quiet.Executable);
        Assert.Equal(["--silent"], quiet.Arguments);
    }

    [Fact]
    public void Planner_AppendsKnownSwitchesAndRefusesUnknown()
    {
        UninstallPlanner planner = new();
        string exe = TempExe("u.exe");

        Assert.Equal(["/uninstall", "/quiet", "/norestart"], planner.Plan(Item(type: InstallerTypes.Burn, uninstall: $"\"{exe}\""), null).Value!.Arguments);
        Assert.Equal(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"], planner.Plan(Item(type: InstallerTypes.Inno, uninstall: $"\"{exe}\""), null).Value!.Arguments);
        Assert.Equal(["/S"], planner.Plan(Item(type: InstallerTypes.Nsis, uninstall: $"\"{exe}\" /S"), null).Value!.Arguments);
        Assert.Equal("unsupported_installer", planner.Plan(Item(type: InstallerTypes.InstallShield, uninstall: $"\"{exe}\""), null).ErrorCode);
        Assert.Equal("invalid_uninstall_string", planner.Plan(Item(type: InstallerTypes.Nsis, uninstall: "\"C:\\missing\\x.exe\""), null).ErrorCode);
        Assert.Equal("invalid_uninstall_string", planner.Plan(Item(type: InstallerTypes.Nsis, uninstall: "\"rel.exe\""), null).ErrorCode);
    }

    [Fact]
    public async Task Uninstall_EnforcesGates()
    {
        string exe = TempExe("g.exe");
        string cmd = $"\"{exe}\"";
        FakeInventory inventory = new(
            Item("machine-x64:ok", "Ok", InstallerTypes.Nsis, uninstall: cmd),
            Item("machine-x64:p", "Vestigo Server", InstallerTypes.Nsis, uninstall: cmd),
            Item("machine-x64:n", "NoRem", InstallerTypes.Nsis, uninstall: cmd, noRemove: true),
            Item("machine-x64:s", "Sys", InstallerTypes.Nsis, uninstall: cmd, system: true),
            Item("user:S-1:u", "User", InstallerTypes.Nsis, scope: "user", uninstall: cmd));

        Assert.Equal("uninstall_disabled", (await Tools(inventory, allow: false).UninstallSoftware("machine-x64:ok")).ErrorCode);
        SoftwareTools tools = Tools(inventory);
        Assert.Equal("not_found", (await tools.UninstallSoftware("x")).ErrorCode);
        Assert.Equal("protected_software", (await tools.UninstallSoftware("machine-x64:p")).ErrorCode);
        Assert.Equal("not_removable", (await tools.UninstallSoftware("machine-x64:n")).ErrorCode);
        Assert.Equal("not_removable", (await tools.UninstallSoftware("machine-x64:s")).ErrorCode);
        Assert.Equal("unsupported_scope", (await tools.UninstallSoftware("user:S-1:u")).ErrorCode);
    }

    [Fact]
    public async Task Uninstall_DryRunDoesNotStartJob()
    {
        string exe = TempExe("d.exe");
        FakeRunner runner = new();
        SoftwareTools tools = Tools(new FakeInventory(Item(type: InstallerTypes.Nsis, uninstall: $"\"{exe}\"")), runner);

        OperationResult<UninstallResult> result = await tools.UninstallSoftware("machine-x64:App", dryRun: true);

        Assert.True(result.Value!.DryRun);
        Assert.Null(result.Value.Job);
        Assert.Null(runner.Last);
    }

    [Fact]
    public async Task Uninstall_RunsJobAndReportsOutcome()
    {
        string exe = TempExe("r.exe");
        FakeInventory inventory = new(Item(type: InstallerTypes.Nsis, uninstall: $"\"{exe}\""));
        FakeRunner runner = new(3010);
        SoftwareTools tools = Tools(inventory, runner);

        UninstallResult result = (await tools.UninstallSoftware("machine-x64:App", wait: 10)).Value!;

        Assert.Equal(JobState.Succeeded, result.Job!.State);
        Assert.Equal(3010, result.ExitCode);
        Assert.True(result.RebootRequired);
        Assert.True(result.StillInstalled);
        Assert.Contains("ran", result.OutputTail, StringComparison.Ordinal);
        Assert.Equal(["/S"], runner.Last!.Arguments);
    }

    [Fact]
    public async Task Uninstall_FailureExitCodeFailsJob()
    {
        string exe = TempExe("f.exe");
        SoftwareTools tools = Tools(new FakeInventory(Item(type: InstallerTypes.Nsis, uninstall: $"\"{exe}\"")), new FakeRunner(5));

        UninstallResult result = (await tools.UninstallSoftware("machine-x64:App", wait: 10)).Value!;

        Assert.Equal(JobState.Failed, result.Job!.State);
        Assert.False(result.RebootRequired);
    }

    [Fact]
    public void Modules_RequireTokensForUninstall()
    {
        VestigoOptions open = new() { Software = { AllowUninstall = true } };
        VestigoOptions tokens = new() { ApiTokens = ["t"], Software = { AllowUninstall = true } };

        Assert.Throws<InvalidOperationException>(() => ModuleRegistry.ValidateConfiguration(open));
        ModuleRegistry.ValidateConfiguration(tokens);
        ModuleRegistry.ValidateConfiguration(new VestigoOptions());
        Assert.Throws<InvalidOperationException>(() => new SoftwareOptions { MaxUninstallDuration = TimeSpan.Zero }.Validate());
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void RegistryInventory_ReadsMachineEntries()
    {
        IReadOnlyList<InstalledSoftware> all = new RegistrySoftwareInventory().List();

        Assert.NotEmpty(all);
        Assert.All(all, item => Assert.False(string.IsNullOrWhiteSpace(item.Name)));
    }
}
