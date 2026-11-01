using Vestigo.Configuration;
using Vestigo.Jobs;
using Vestigo.Modules;
using Vestigo.Processes;
using Vestigo.RemoteDebugger;

namespace Vestigo.Server.Tests;

public class RemoteDebuggerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vestigo-rdbg-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("2022", "x64", "https://aka.ms/vs/17/release/RemoteTools.amd64ret.enu.exe")]
    [InlineData("2022", "arm64", "https://aka.ms/vs/17/release/RemoteTools.arm64ret.enu.exe")]
    [InlineData("2022", "x86", "https://aka.ms/vs/17/release/RemoteTools.x86ret.enu.exe")]
    [InlineData("2026", "x64", "https://aka.ms/vs/18/release/RemoteTools.amd64ret.enu.exe")]
    [InlineData("2026", "x86", "https://aka.ms/vs/18/release/RemoteTools.x86ret.enu.exe")]
    [InlineData("2026", "arm64", "https://aka.ms/vs/18/release/RemoteTools.arm64ret.enu.exe")]
    public void InstallerUrl_SelectsPerVersionAndArch(string version, string arch, string expected)
    {
        Assert.True(RemoteDebuggerPlanner.TryParseVersion(version, out DebuggerVersion v));
        Assert.True(RemoteDebuggerPlanner.TryParseArch(arch, out DebuggerArch a));

        Assert.Equal(expected, RemoteDebuggerPlanner.InstallerUrl(v, a).ToString());
    }

    [Fact]
    public void Parsing_AcceptsKnownValuesAndDefaults()
    {
        Assert.True(RemoteDebuggerPlanner.TryParseArch(null, out DebuggerArch arch));
        Assert.Equal(DebuggerArch.X64, arch);
        Assert.False(RemoteDebuggerPlanner.TryParseVersion("2019", out _));
        Assert.False(RemoteDebuggerPlanner.TryParseVersion("stable", out _));
        Assert.False(RemoteDebuggerPlanner.TryParseArch("mips", out _));
        Assert.True(RemoteDebuggerPlanner.TryParseAuth(null, out DebuggerAuth auth));
        Assert.Equal(DebuggerAuth.Windows, auth);
        Assert.False(RemoteDebuggerPlanner.TryParseAuth("basic", out _));
    }

    [Fact]
    public void StartArguments_WindowsAuth_UsesAllowEntries()
    {
        IReadOnlyList<string> args = RemoteDebuggerPlanner.BuildStartArguments(4026, 900, DebuggerAuth.Windows, [@"CORP\dev", "me@corp.example"]);

        Assert.Equal(
            ["/nostatus", "/silent", "/nofirewallwarn", "/port", "4026", "/timeout", "900", "/allow", @"CORP\dev", "/allow", "me@corp.example"],
            args);
        Assert.DoesNotContain("/noauth", args);
        Assert.DoesNotContain("/nofirewallwarning", args);
    }

    [Fact]
    public void StartArguments_NoAuth_UsesNoAuthAnyUser()
    {
        IReadOnlyList<string> args = RemoteDebuggerPlanner.BuildStartArguments(5000, 60, DebuggerAuth.None, []);

        Assert.Equal(["/nostatus", "/silent", "/nofirewallwarn", "/port", "5000", "/timeout", "60", "/noauth", "/anyuser"], args);
        Assert.Equal("/nostatus /silent /nofirewallwarn /port 5000 /timeout 60 /noauth /anyuser", RemoteDebuggerPlanner.Describe(args));
    }

    [Fact]
    public void FixedArguments_MatchDocumentedSwitches()
    {
        Assert.Equal(["/install", "/quiet", "/norestart"], RemoteDebuggerPlanner.InstallArguments);
        Assert.Equal(["/prepcomputer", "/private", "/quiet"], RemoteDebuggerPlanner.PrepareArguments);
    }

    [Theory]
    [InlineData(0, 900, "windows", false, null, "port")]
    [InlineData(70000, 900, "windows", false, null, "port")]
    [InlineData(4026, 5, "windows", false, null, "timeoutSeconds")]
    [InlineData(4026, 900, "none", false, null, "iUnderstandNoAuthIsInsecure")]
    [InlineData(4026, 900, "none", true, "CORP\\dev", "allowUsers")]
    [InlineData(4026, 900, "windows", false, "dev /noauth", "allowUsers")]
    [InlineData(4026, 900, "windows", false, "a\"b", "allowUsers")]
    [InlineData(4026, 900, "windows", false, "dev\n", "allowUsers")]
    public void ValidateStart_RefusesUnsafeRequests(int port, int timeout, string auth, bool confirm, string? user, string expected)
    {
        RemoteDebuggerPlanner.TryParseAuth(auth, out DebuggerAuth parsed);

        string? error = RemoteDebuggerPlanner.ValidateStart(port, timeout, parsed, confirm, user is null ? [] : [user]);

        Assert.NotNull(error);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateStart_AcceptsConfirmedNoAuthAndWindowsAuth()
    {
        Assert.Null(RemoteDebuggerPlanner.ValidateStart(4026, 900, DebuggerAuth.None, true, []));
        Assert.Null(RemoteDebuggerPlanner.ValidateStart(4026, 900, DebuggerAuth.Windows, false, [@"DOMAIN\user.name"]));
    }

    [Theory]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", true)]
    [InlineData("CN=\"Microsoft Corporation\", OU=MOPR, O=Microsoft Corporation, C=US", true)]
    [InlineData("CN=microsoft corporation", true)]
    [InlineData("CN=Evil Microsoft Corporation Ltd, O=Evil", false)]
    [InlineData("CN=Contoso, OU=Microsoft Corporation", false)]
    [InlineData("CN=Microsoft Windows", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not a distinguished name", false)]
    public void IsMicrosoftSigner_ChecksSubjectComponents(string? subject, bool expected) =>
        Assert.Equal(expected, RemoteDebuggerPlanner.IsMicrosoftSigner(subject));

    [Theory]
    [InlineData("\"C:\\x\\msvsmon.exe\" /port 4030 /noauth", 4030)]
    [InlineData("msvsmon.exe /PORT:4031", 4031)]
    [InlineData("msvsmon.exe /silent", null)]
    [InlineData("msvsmon.exe /port 99999", null)]
    [InlineData(null, null)]
    public void ParsePort_ReadsCommandLine(string? line, int? expected) => Assert.Equal(expected, RemoteDebuggerPlanner.ParsePort(line));

    [Fact]
    public void ParseCommandLines_HandlesObjectAndArray()
    {
        Assert.Equal("a /port 1", MsvsmonProcesses.ParseCommandLines("""{"ProcessId":7,"CommandLine":"a /port 1"}""")[7]);
        Dictionary<int, string> many = MsvsmonProcesses.ParseCommandLines("""[{"ProcessId":1,"CommandLine":"x"},{"ProcessId":2,"CommandLine":null}]""");
        Assert.Single(many);
        Assert.Empty(MsvsmonProcesses.ParseCommandLines("{oops"));
        Assert.Empty(MsvsmonProcesses.ParseCommandLines(""));
    }

    private sealed class FakeProbe : IMsvsmonProbe
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string[]> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string[] Vswhere { get; set; } = [];

        public string[] Registry { get; set; } = [];

        public Dictionary<string, int> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => Files.Contains(path);

        public IReadOnlyList<string> ChildDirectories(string path) => Directories.TryGetValue(path, out string[]? children) ? children : [];

        public IReadOnlyList<string> VswhereInstallationPaths(int major) => Vswhere;

        public IReadOnlyList<string> UninstallInstallLocations(DebuggerVersion version) => Registry;

        public int? FileMajorVersion(string path) => Versions.TryGetValue(path, out int major) ? major : null;
    }

    private const string Pf = @"C:\Program Files";

    [Fact]
    public void Locator_FindsStandaloneInstallPerVersionAndArch()
    {
        FakeProbe probe = new();
        probe.Files.Add(Pf + @"\Microsoft Visual Studio 17.0\Common7\IDE\Remote Debugger\x64\msvsmon.exe");
        probe.Files.Add(Pf + @"\Microsoft Visual Studio 18.0\Common7\IDE\Remote Debugger\arm64\msvsmon.exe");
        MsvsmonLocator locator = new(probe, [Pf], []);

        Assert.Equal("standalone", locator.Find(DebuggerVersion.Vs2022, DebuggerArch.X64)!.Source);
        Assert.Contains("Visual Studio 18.0", locator.Find(DebuggerVersion.Vs2026, DebuggerArch.Arm64)!.Path, StringComparison.Ordinal);
        Assert.Null(locator.Find(DebuggerVersion.Vs2022, DebuggerArch.Arm64));
        Assert.Null(locator.Find(DebuggerVersion.Vs2026, DebuggerArch.X64));
    }

    [Fact]
    public void Locator_FindsIdeEditionVswhereAndRegistryFallbacks()
    {
        FakeProbe probe = new();
        probe.Directories[Pf + @"\Microsoft Visual Studio\18"] = [Pf + @"\Microsoft Visual Studio\18\Enterprise"];
        probe.Files.Add(Pf + @"\Microsoft Visual Studio\18\Enterprise\Common7\IDE\Remote Debugger\x64\msvsmon.exe");
        probe.Vswhere = [@"D:\VS\17\Community"];
        probe.Files.Add(@"D:\VS\17\Community\Common7\IDE\Remote Debugger\x86\msvsmon.exe");
        probe.Registry = [@"E:\RemoteTools"];
        probe.Files.Add(@"E:\RemoteTools\Remote Debugger\arm64\msvsmon.exe");
        MsvsmonLocator locator = new(probe, [Pf], []);

        Assert.Equal("ide", locator.Find(DebuggerVersion.Vs2026, DebuggerArch.X64)!.Source);
        Assert.Equal("vswhere", locator.Find(DebuggerVersion.Vs2022, DebuggerArch.X86)!.Source);
        Assert.Equal("registry", locator.Find(DebuggerVersion.Vs2022, DebuggerArch.Arm64)!.Source);
    }

    [Fact]
    public void Locator_ExtraPathsRequireMatchingMajorVersion()
    {
        FakeProbe probe = new();
        probe.Files.Add(@"F:\tools\x64\msvsmon.exe");
        probe.Versions[@"F:\tools\x64\msvsmon.exe"] = 17;
        MsvsmonLocator locator = new(probe, [Pf], [@"F:\tools"]);

        Assert.Equal("extra", locator.Find(DebuggerVersion.Vs2022, DebuggerArch.X64)!.Source);
        Assert.Null(locator.Find(DebuggerVersion.Vs2026, DebuggerArch.X64));
    }

    [Fact]
    public void Options_ValidateAndDefaults()
    {
        VestigoOptions options = new();
        Assert.Equal(4026, options.RemoteDebugger.DefaultPort);
        options.Validate();

        options.RemoteDebugger.DefaultPort = 0;
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.RemoteDebugger.DefaultPort = 4026;
        options.RemoteDebugger.InstallerDownloadTimeout = TimeSpan.Zero;
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.RemoteDebugger.InstallerDownloadTimeout = TimeSpan.FromMinutes(1);
        options.RemoteDebugger.ExtraSearchPaths = ["relative"];
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Module_IsRegisteredAndExposesTools()
    {
        VestigoModule module = Assert.Single(ModuleRegistry.All, candidate => candidate.Name == "RemoteDebugger");

        Assert.Contains(typeof(RemoteDebuggerTools), module.ToolTypes);
    }

    private sealed class NoopLauncher : IProcessLauncher
    {
        public int Runs { get; private set; }

        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) => OperationResult<bool>.Ok(true);

        public Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken)
        {
            Runs++;
            return Task.FromResult(0);
        }
    }

    private sealed class FakeVerifier : IAuthenticodeVerifier
    {
        public OperationResult<string> Verify(string path) => OperationResult<string>.Ok("CN=Microsoft Corporation");
    }

    private sealed class FakeHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private (RemoteDebuggerTools Tools, NoopLauncher Launcher, FakeProbe Probe) CreateTools()
    {
        VestigoOptions options = new();
        NoopLauncher launcher = new();
        FakeProbe probe = new();
        JobManager jobs = new(options, Path.Combine(_root, "jobs"));
        RemoteDebuggerService service = new(
            options, jobs, launcher, new AppCloser(new SessionHelperClient(launcher)), new MsvsmonLocator(probe, [Pf], []), new FakeVerifier(), new FakeHttpFactory());
        return (new RemoteDebuggerTools(service), launcher, probe);
    }

    [Fact]
    public async Task Start_RefusesNoAuthWithoutConfirmation_AndBadInputs()
    {
        (RemoteDebuggerTools tools, NoopLauncher launcher, FakeProbe probe) = CreateTools();
        probe.Files.Add(Pf + @"\Microsoft Visual Studio 17.0\Common7\IDE\Remote Debugger\x64\msvsmon.exe");

        OperationResult<StartRemoteDebuggerResult> noConfirm = await tools.Start("2022", auth: "none");
        OperationResult<StartRemoteDebuggerResult> badVersion = await tools.Start("2019");
        OperationResult<StartRemoteDebuggerResult> badAuth = await tools.Start("2022", auth: "kerberos");
        OperationResult<StartRemoteDebuggerResult> badContext = await tools.Start("2022", context: "desktop");

        Assert.Equal("invalid_request", noConfirm.ErrorCode);
        Assert.Contains("iUnderstandNoAuthIsInsecure", noConfirm.Error, StringComparison.Ordinal);
        Assert.Equal("invalid_request", badVersion.ErrorCode);
        Assert.Equal("invalid_request", badAuth.ErrorCode);
        Assert.Equal("invalid_request", badContext.ErrorCode);
        Assert.Equal(0, launcher.Runs);
    }

    [Fact]
    public async Task Start_WithoutInstallation_ReturnsNotFound()
    {
        (RemoteDebuggerTools tools, NoopLauncher launcher, _) = CreateTools();

        OperationResult<StartRemoteDebuggerResult> result = await tools.Start("2026", "arm64");

        Assert.Equal("not_found", result.ErrorCode);
        Assert.Equal(0, launcher.Runs);
    }

    [Fact]
    public void Install_SkipsWhenInstalledUnlessForced_AndStopRejectsBadSelectors()
    {
        (RemoteDebuggerTools tools, _, FakeProbe probe) = CreateTools();
        probe.Files.Add(Pf + @"\Microsoft Visual Studio 17.0\Common7\IDE\Remote Debugger\x64\msvsmon.exe");

        OperationResult<InstallRemoteDebuggerResult> skipped = tools.Install("2022");
        OperationResult<InstallRemoteDebuggerResult> invalid = tools.Install("2022", "ppc");

        Assert.True(skipped.Value!.Skipped);
        Assert.Null(skipped.Value.Job);
        Assert.Equal("invalid_request", invalid.ErrorCode);
        Assert.Equal("invalid_request", tools.Stop().Result.ErrorCode);
        Assert.Equal("refused", tools.Stop(pid: Environment.ProcessId).Result.ErrorCode);
        Assert.Equal("not_found", tools.Stop(pid: 999_999).Result.ErrorCode);
    }

    [Fact]
    public void Status_ListsEveryVersionAndArchitecture()
    {
        (RemoteDebuggerTools tools, _, FakeProbe probe) = CreateTools();
        probe.Files.Add(Pf + @"\Microsoft Visual Studio 17.0\Common7\IDE\Remote Debugger\x64\msvsmon.exe");

        RemoteDebuggerStatus status = tools.GetStatus().Value!;

        Assert.Equal(6, status.Installations.Count);
        Assert.Single(status.Installations, item => item.Installed);
        Assert.Single(tools.GetStatus("2026", "x86").Value!.Installations);
        Assert.Equal("invalid_request", tools.GetStatus("1999").ErrorCode);
    }
}
