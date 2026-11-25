using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

using Vestigo.Configuration;
using Vestigo.Jobs;
using Vestigo.Modules;
using Vestigo.Processes;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

public class ProcessesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vestigo-proc-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private VestigoOptions Options(RootAccess access = RootAccess.Read | RootAccess.Execute, bool pathLookup = true) => new()
    {
        Roots = [new RootDefinition { Name = "tools", Path = _root, Access = access }],
        Processes = new ProcessesOptions { AllowPathLookup = pathLookup }
    };

    private (ProcessTools Tools, JobManager Jobs) CreateTools(VestigoOptions options, IProcessLauncher? launcher = null)
    {
        RootPolicy roots = new(options);
        JobManager jobs = new(options, Path.Combine(_root, "jobs"));
        launcher ??= new ProcessLauncher();
        return (new ProcessTools(roots, new ExecutableResolver(roots, options), jobs, launcher, new AppCloser(new SessionHelperClient(launcher))), jobs);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static string[] SplitWithWindows(string commandLine)
    {
        IntPtr list = CommandLineToArgvW(commandLine, out int count);
        try
        {
            return Enumerable.Range(0, count).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(list, index * IntPtr.Size))!).ToArray();
        }
        finally
        {
            LocalFree(list);
        }
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("with space")]
    [InlineData("quote\"inside")]
    [InlineData(@"trailing\")]
    [InlineData(@"trailing space\ ")]
    [InlineData(@"C:\Program Files\app\")]
    [InlineData(@"back\\""slash")]
    [InlineData("")]
    [InlineData("tab\there")]
    [InlineData("line\nbreak")]
    [InlineData("carriage\rreturn")]
    [InlineData("vertical\vtab")]
    [InlineData("form\ffeed")]
    [InlineData("nonbreaking\u00A0space")]
    [InlineData("\"")]
    [InlineData(@"ends with two\\")]
    public void CommandLineBuilder_RoundTripsThroughCommandLineToArgvW(string argument)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string line = CommandLineBuilder.Build(@"C:\Program Files\tool.exe", ["first", argument, "last"]);
        string[] parsed = SplitWithWindows(line);

        Assert.Equal([@"C:\Program Files\tool.exe", "first", argument, "last"], parsed);
    }

    [Fact]
    public void CommandLineBuilder_QuotesOnlyWhenNeeded()
    {
        Assert.Equal(@"a.exe b ""c d"" """"", CommandLineBuilder.Build("a.exe", ["b", "c d", ""]));
        Assert.Equal("a.exe \"x\\\\\\\"y\"", CommandLineBuilder.Build("a.exe", ["x\\\"y"]));
    }

    [Fact]
    public void LaunchPlanner_RunsPs1ThroughPwshWhenPresent()
    {
        LaunchPlan plan = LaunchPlanner.Plan(@"C:\r\a.ps1", ["-x", "1"], () => @"C:\pwsh.exe", @"C:\ps.exe");

        Assert.Equal(@"C:\pwsh.exe", plan.Executable);
        Assert.Equal(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @"C:\r\a.ps1", "-x", "1"], plan.Arguments);
    }

    [Fact]
    public void LaunchPlanner_FallsBackToWindowsPowerShell()
    {
        LaunchPlan plan = LaunchPlanner.Plan(@"C:\r\A.PS1", [], () => null, @"C:\ps.exe");

        Assert.Equal(@"C:\ps.exe", plan.Executable);
        Assert.Equal("-File", plan.Arguments[3]);
    }

    [Fact]
    public void LaunchPlanner_LeavesOtherFilesAlone()
    {
        LaunchPlan plan = LaunchPlanner.Plan(@"C:\r\a.exe", ["x"], () => @"C:\pwsh.exe", @"C:\ps.exe");

        Assert.Equal(@"C:\r\a.exe", plan.Executable);
        Assert.Equal(["x"], plan.Arguments);
    }

    [Fact]
    public void LaunchPlanner_ValidatesArgumentsAndEnvironment()
    {
        Dictionary<string, string> none = [];

        Assert.Null(LaunchPlanner.Validate(["a"], none, null));
        Assert.NotNull(LaunchPlanner.Validate(["a\0"], none, null));
        Assert.NotNull(LaunchPlanner.Validate(Enumerable.Repeat("a", LaunchPlanner.MaxArguments + 1).ToArray(), none, null));
        Assert.NotNull(LaunchPlanner.Validate([], new Dictionary<string, string> { ["A=B"] = "x" }, null));
        Assert.NotNull(LaunchPlanner.Validate([], new Dictionary<string, string> { [""] = "x" }, null));
        Assert.NotNull(LaunchPlanner.ValidateCommandLine(new LaunchPlan("a.exe", [new string('x', CommandLineBuilder.MaxLength)])));
    }

    [Fact]
    public void ProcessGuards_RefuseSystemOwnAndInvalidPids()
    {
        Assert.NotNull(ProcessGuards.CheckKillTarget(0, 100));
        Assert.NotNull(ProcessGuards.CheckKillTarget(-3, 100));
        Assert.NotNull(ProcessGuards.CheckKillTarget(4, 100));
        Assert.NotNull(ProcessGuards.CheckKillTarget(100, 100));
        Assert.Null(ProcessGuards.CheckKillTarget(1234, 100));
    }

    [Fact]
    public void EnvironmentBlock_ParsesAndMergesOverrides()
    {
        string block = "=C:=C:\\work\0Path=C:\\a\0TEMP=C:\\t\0\0";

        Dictionary<string, string> parsed = EnvironmentBlock.Parse(block);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("C:\\a", parsed["path"]);

        char[] merged = EnvironmentBlock.Build(parsed, new Dictionary<string, string> { ["PATH"] = "X", ["Extra"] = "1" });
        Assert.Equal("Extra=1\0Path=X\0TEMP=C:\\t\0\0", new string(merged));
        Assert.Equal("\0\0", new string(EnvironmentBlock.Build(new Dictionary<string, string>(), new Dictionary<string, string>())));
    }

    [Fact]
    public void ExecutableResolver_RequiresExecuteAccessForRootFiles()
    {
        File.WriteAllText(Path.Combine(_root, "tool.exe"), "x");
        RootPolicy readOnly = new(Options(RootAccess.Read));
        RootPolicy executable = new(Options());

        Assert.Equal("access_denied", new ExecutableResolver(readOnly, new ProcessesOptions(), () => null, null, File.Exists).Resolve("tools:tool.exe").ErrorCode);
        OperationResult<ResolvedExecutable> ok = new ExecutableResolver(executable, new ProcessesOptions(), () => null, null, File.Exists).Resolve("tools:tool.exe");
        Assert.True(ok.Success);
        Assert.True(ok.Value!.FromRoot);
        Assert.Equal("not_found", new ExecutableResolver(executable, new ProcessesOptions(), () => null, null, File.Exists).Resolve("tools:missing.exe").ErrorCode);
        Assert.Equal("outside_root", new ExecutableResolver(executable, new ProcessesOptions(), () => null, null, File.Exists).Resolve(@"tools:..\x.exe").ErrorCode);
    }

    [Fact]
    public void ExecutableResolver_ResolvesBareNamesFromPathOnlyWhenAllowed()
    {
        string bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "hello.exe"), "x");
        File.WriteAllText(Path.Combine(bin, "run.cmd"), "x");
        File.WriteAllText(Path.Combine(bin, "script.ps1"), "x");
        RootPolicy roots = new(Options());

        ExecutableResolver allowed = new(roots, new ProcessesOptions { AllowPathLookup = true }, () => "relative;" + bin, ".COM;.EXE;.PS1", File.Exists);
        ExecutableResolver denied = new(roots, new ProcessesOptions { AllowPathLookup = false }, () => bin, ".EXE", File.Exists);

        Assert.Equal(Path.Combine(bin, "hello.exe"), allowed.Resolve("hello").Value!.Path);
        Assert.False(allowed.Resolve("hello.exe").Value!.FromRoot);
        Assert.Equal(Path.Combine(bin, "run.cmd"), allowed.Resolve("run").Value!.Path);
        Assert.Equal("invalid_path", allowed.Resolve("script.ps1").ErrorCode);
        Assert.Equal("not_found", allowed.Resolve("nothere").ErrorCode);
        Assert.Equal("path_lookup_disabled", denied.Resolve("hello.exe").ErrorCode);
    }

    [Fact]
    public void SessionHelper_RoundTripsCloseArguments()
    {
        CloseSelector selector = new([12, 34], ["notepad"], "Untitled.*");

        string[] args = SessionHelper.BuildCloseArguments(selector, 7, 99).ToArray();

        Assert.True(SessionHelper.IsInvocation(args));
        Assert.True(SessionHelper.TryParseClose(args, out CloseSelector parsed, out int grace, out int exclude, out string? error));
        Assert.Null(error);
        Assert.Equal([12, 34], parsed.Pids);
        Assert.Equal(["notepad"], parsed.ProcessNames);
        Assert.Equal("Untitled.*", parsed.TitlePattern);
        Assert.Equal(7, grace);
        Assert.Equal(99, exclude);
    }

    [Fact]
    public void SessionHelper_RejectsBadArguments()
    {
        Assert.False(SessionHelper.IsInvocation(["--other"]));
        Assert.False(SessionHelper.TryParseClose([SessionHelper.Flag, "close-window"], out _, out _, out _, out string? empty));
        Assert.NotNull(empty);
        Assert.False(SessionHelper.TryParseClose([SessionHelper.Flag, "close-window", "--pid", "x"], out _, out _, out _, out _));
        Assert.False(SessionHelper.TryParseClose([SessionHelper.Flag, "close-window", "--pid"], out _, out _, out _, out _));
        Assert.False(SessionHelper.TryParseClose([SessionHelper.Flag, "other"], out _, out _, out _, out _));
    }

    [Fact]
    public void CloseSelector_CombinesProcessAndTitleFilters()
    {
        CloseSelector byName = new([], ["Notepad.exe"], "^Untitled");
        System.Text.RegularExpressions.Regex regex = CloseSelector.CompileTitle(byName.TitlePattern)!;

        Assert.True(byName.Matches(1, "notepad", "Untitled - Notepad", regex));
        Assert.False(byName.Matches(1, "notepad", "Readme", regex));
        Assert.False(byName.Matches(1, "calc", "Untitled", regex));
        Assert.True(new CloseSelector([5], [], null).Matches(5, "x", "", null));
        Assert.False(new CloseSelector([5], [], null).Matches(6, "x", "", null));
        Assert.True(new CloseSelector([], [], "tit").Matches(1, "any", "my title", CloseSelector.CompileTitle("tit")));
    }

    [Fact]
    public void AppCloser_ParsesHelperOutput()
    {
        const string json = """{"matched":[{"pid":5,"name":"notepad","windowsPosted":1}],"stillRunning":[{"pid":5,"name":"notepad"}],"windowsPosted":1}""";

        OperationResult<CloseReport> ok = AppCloser.ParseHelperOutput("noise\n" + json + "\n", 0);
        Assert.True(ok.Success);
        Assert.Equal(5, ok.Value!.StillRunning.Single().Pid);
        Assert.Null(ok.Value.Outcomes);
        const string detailed = """{"matched":[{"pid":5,"name":"notepad","windowsPosted":2}],"stillRunning":[],"windowsPosted":2,"outcomes":[{"pid":5,"name":"notepad","outcome":"exited","windowsPosted":2,"postError":"1400","verificationError":"pid_reused","pidReused":true}]}""";
        OperationResult<CloseReport> withOutcomes = AppCloser.ParseHelperOutput(detailed, 0);
        Assert.True(withOutcomes.Success, withOutcomes.Error);
        CloseProcessOutcome outcome = Assert.Single(withOutcomes.Value!.Outcomes!);
        Assert.Equal(CloseOutcome.Exited, outcome.Outcome);
        Assert.Equal("1400", outcome.PostError);
        Assert.Equal("pid_reused", outcome.VerificationError);
        Assert.True(outcome.PidReused);
        OperationResult<CloseReport> roundTrip = AppCloser.ParseHelperOutput(JsonSerializer.Serialize(withOutcomes.Value, SessionHelper.Json), 0);
        Assert.Equal(outcome, Assert.Single(roundTrip.Value!.Outcomes!));
        Assert.Equal("invalid_request", AppCloser.ParseHelperOutput("""{"error":"bad"}""", 2).ErrorCode);
        Assert.Equal("helper_failed", AppCloser.ParseHelperOutput("", 1).ErrorCode);
        Assert.Equal("helper_failed", AppCloser.ParseHelperOutput("{oops", 0).ErrorCode);
    }

    [Fact]
    public void Close_ExitedProcessIsNotStillRunning()
    {
        FakeWindowClosePlatform platform = Platform(
            new VisibleWindow(11, 7, "notepad", "Untitled"),
            new ProcessIdentity(7, 1, null, 0),
            new CloseWaitResult(CloseWaitKind.Exited, null));

        CloseReport report = WindowCloser.Close(new CloseSelector([7], [], null), TimeSpan.FromSeconds(1), 99, platform);

        CloseProcessOutcome outcome = Assert.Single(report.Outcomes!);
        Assert.Equal(CloseOutcome.Exited, outcome.Outcome);
        Assert.True(outcome.WindowsPosted >= 1);
        Assert.False(outcome.PidReused);
        Assert.Null(outcome.VerificationError);
        Assert.Empty(report.StillRunning);
        Assert.Equal(7, Assert.Single(report.Matched).Pid);
        Assert.Equal(outcome.WindowsPosted, report.WindowsPosted);
        int captured = platform.Events.IndexOf("capture:7");
        int posted = platform.Events.IndexOf("post:11");
        Assert.True(captured >= 0 && posted > captured);
    }

    [Fact]
    public void Close_DestroyedWindowStillRunningStaysInStillRunning()
    {
        FakeWindowClosePlatform platform = Platform(
            new VisibleWindow(4, 8, "notepad", "Gone"),
            new ProcessIdentity(8, 1, null, 0),
            new CloseWaitResult(CloseWaitKind.StillRunning, null));

        CloseReport report = WindowCloser.Close(new CloseSelector([8], [], null), TimeSpan.FromSeconds(1), 99, platform);

        CloseProcessOutcome outcome = Assert.Single(report.Outcomes!);
        Assert.Equal(CloseOutcome.StillRunning, outcome.Outcome);
        Assert.Equal(8, Assert.Single(report.StillRunning).Pid);
        Assert.Equal("notepad", report.StillRunning[0].Name);
    }

    [Fact]
    public void Close_IgnoredCloseStaysRunning()
    {
        FakeWindowClosePlatform platform = Platform(
            new VisibleWindow(4, 8, "notepad", "Untitled"),
            new ProcessIdentity(8, 1, null, 0),
            new CloseWaitResult(CloseWaitKind.StillRunning, null));

        CloseReport report = WindowCloser.Close(new CloseSelector([], ["notepad.exe"], null), TimeSpan.FromSeconds(1), 99, platform);

        CloseProcessOutcome outcome = Assert.Single(report.Outcomes!);
        Assert.Equal(CloseOutcome.StillRunning, outcome.Outcome);
        Assert.True(outcome.WindowsPosted >= 1);
        Assert.Null(outcome.PostError);
        Assert.Equal(8, Assert.Single(report.StillRunning).Pid);
        Assert.Equal(outcome.WindowsPosted, report.WindowsPosted);
    }

    [Fact]
    public void Close_UnknownVerificationIsNotStillRunning()
    {
        FakeWindowClosePlatform platform = Platform(
            new VisibleWindow(4, 8, "notepad", "Untitled"),
            new ProcessIdentity(8, 1, null, 0),
            new CloseWaitResult(CloseWaitKind.Unknown, "5"));

        CloseReport report = WindowCloser.Close(new CloseSelector([8], [], null), TimeSpan.Zero, 99, platform);

        CloseProcessOutcome outcome = Assert.Single(report.Outcomes!);
        Assert.Equal(CloseOutcome.Unknown, outcome.Outcome);
        Assert.Equal("5", outcome.VerificationError);
        Assert.Empty(report.StillRunning);
        Assert.Equal(8, Assert.Single(report.Matched).Pid);
    }

    [Fact]
    public void Close_ReusedPidIsExitedAndNotStillRunning()
    {
        FakeWindowClosePlatform platform = Platform(
            new VisibleWindow(4, 8, "notepad", "Untitled"),
            new ProcessIdentity(8, 1, null, 0),
            new CloseWaitResult(CloseWaitKind.Reused, "ignored"));

        CloseReport report = WindowCloser.Close(new CloseSelector([8], [], null), TimeSpan.FromSeconds(1), 99, platform);

        CloseProcessOutcome outcome = Assert.Single(report.Outcomes!);
        Assert.Equal(CloseOutcome.Exited, outcome.Outcome);
        Assert.True(outcome.PidReused);
        Assert.Equal("pid_reused", outcome.VerificationError);
        Assert.Empty(report.StillRunning);
    }

    [Fact]
    public void Close_AlreadyGoneAtCaptureIsExited()
    {
        FakeWindowClosePlatform platform = new();
        platform.Windows.Add(new VisibleWindow(4, 8, "notepad", "Untitled"));
        platform.Identities[8] = new ProcessIdentity(8, 0, null, 87);

        CloseReport report = WindowCloser.Close(new CloseSelector([8], [], null), TimeSpan.FromSeconds(1), 99, platform);

        CloseProcessOutcome outcome = Assert.Single(report.Outcomes!);
        Assert.Equal(CloseOutcome.Exited, outcome.Outcome);
        Assert.False(outcome.PidReused);
        Assert.Empty(report.StillRunning);
        Assert.DoesNotContain(platform.Events, item => item.StartsWith("wait:", StringComparison.Ordinal));
    }

    [Fact]
    public void Close_TwoProcesses_PopulateMatchedStillRunningAndOutcomes()
    {
        FakeWindowClosePlatform platform = new();
        platform.Windows.Add(new VisibleWindow(1, 0, "idle", "zero"));
        platform.Windows.Add(new VisibleWindow(2, 50, "own", "self"));
        platform.Windows.Add(new VisibleWindow(3, 10, "notepad", "Untitled"));
        platform.Windows.Add(new VisibleWindow(4, 10, "", "Second"));
        platform.Windows.Add(new VisibleWindow(5, 20, "calc", "Calculator"));
        platform.Windows.Add(new VisibleWindow(6, 30, "other", "Nope"));
        platform.Identities[10] = new ProcessIdentity(10, 1, null, 0);
        platform.Identities[20] = new ProcessIdentity(20, 1, null, 0);
        platform.Waits[10] = new CloseWaitResult(CloseWaitKind.Exited, null);
        platform.Waits[20] = new CloseWaitResult(CloseWaitKind.StillRunning, null);

        CloseReport report = WindowCloser.Close(new CloseSelector([10, 20], [], null), TimeSpan.FromSeconds(2), 50, platform);

        Assert.Equal([10, 20], report.Matched.Select(item => item.Pid).ToArray());
        Assert.Equal("notepad", report.Matched[0].Name);
        Assert.Equal(2, report.Matched[0].WindowsPosted);
        Assert.Equal(1, report.Matched[1].WindowsPosted);
        Assert.Equal(3, report.WindowsPosted);
        Assert.Equal(report.Matched.Sum(item => item.WindowsPosted), report.WindowsPosted);
        Assert.Equal(20, Assert.Single(report.StillRunning).Pid);
        Assert.Equal([CloseOutcome.Exited, CloseOutcome.StillRunning], report.Outcomes!.Select(item => item.Outcome).ToArray());
        Assert.Equal([2, 1], report.Outcomes!.Select(item => item.WindowsPosted).ToArray());
        Assert.Equal(["capture:10", "capture:20", "post:3", "post:4", "post:5", "wait:10", "wait:20"], platform.Events);
    }

    [Fact]
    public void Close_PostFailureStaysMatchedAndStillVerifies()
    {
        FakeWindowClosePlatform platform = new();
        platform.Windows.Add(new VisibleWindow(1, 9, "notepad", "A"));
        platform.Windows.Add(new VisibleWindow(2, 9, "notepad", "B"));
        platform.Identities[9] = new ProcessIdentity(9, 1, null, 0);
        platform.PostErrors[1] = 5;
        platform.PostErrors[2] = 1400;
        platform.Waits[9] = new CloseWaitResult(CloseWaitKind.Exited, null);

        CloseReport report = WindowCloser.Close(new CloseSelector([9], [], null), TimeSpan.FromSeconds(1), 99, platform);

        MatchedProcess matched = Assert.Single(report.Matched);
        Assert.Equal(9, matched.Pid);
        Assert.Equal(0, matched.WindowsPosted);
        Assert.Equal(0, report.WindowsPosted);
        CloseProcessOutcome outcome = Assert.Single(report.Outcomes!);
        Assert.Equal("5", outcome.PostError);
        Assert.Equal(CloseOutcome.Exited, outcome.Outcome);
        Assert.Contains("wait:9", platform.Events);
        Assert.Empty(report.StillRunning);
    }

    [Fact]
    public void WindowCloser_CloseFixture_DoesNotCloseUnrelatedProcesses()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("VESTIGO_CLOSE_FIXTURES") != "1")
        {
            Assert.Skip("Set VESTIGO_CLOSE_FIXTURES=1 on Windows to exercise WindowCloser against a ping process this test starts.");
        }

        string pingPath = Path.Combine(Environment.SystemDirectory, "ping.exe");
        if (!File.Exists(pingPath))
        {
            Assert.Skip("ping.exe was not found. This fixture only closes a ping process it starts.");
        }

        using Process ping = Process.Start(new ProcessStartInfo(pingPath, "-n 30 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("ping.exe did not start.");
        try
        {
            CloseReport report = WindowCloser.Close(new CloseSelector([ping.Id], [], null), TimeSpan.Zero, Environment.ProcessId);
            Assert.NotNull(report.Outcomes);
            Assert.DoesNotContain(report.Matched, item => item.Pid != ping.Id);
            Assert.DoesNotContain(report.StillRunning, item => item.Pid != ping.Id);
            Assert.DoesNotContain(report.Outcomes, item => item.Pid != ping.Id);
            Assert.DoesNotContain(report.Matched, item => item.Pid == ping.Id);
        }
            finally
            {
                try
                {
                    if (!ping.HasExited)
                    {
                        ping.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }

                if (!ping.HasExited && !ping.WaitForExit(5000))
                {
                    throw new InvalidOperationException("The ping fixture was still running after kill.");
                }
            }
    }

    [Fact]
    public void Module_IsRegisteredAndExposesProcessTools()
    {
        VestigoModule module = Assert.Single(ModuleRegistry.All, candidate => candidate.Name == "Processes");

        Assert.Contains(typeof(ProcessTools), module.ToolTypes);
        Assert.True(new VestigoOptions().Processes.AllowPathLookup);
        Assert.False(module.EnabledByDefault);
    }

    [Fact]
    public async Task RunProcess_ServiceContext_WaitsAndReturnsOutputTail()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        (ProcessTools tools, _) = CreateTools(Options());

        OperationResult<RunProcessResult> result = await tools.RunProcess(
            "cmd.exe",
            ["/c", "echo %VESTIGO_TEST% & echo err 1>&2 & exit 3"],
            env: new Dictionary<string, string> { ["VESTIGO_TEST"] = "hello world" },
            wait: 30);

        Assert.True(result.Success, result.Error);
        Assert.Equal(JobState.Failed, result.Value!.Job.State);
        Assert.Equal(3, result.Value.Job.ExitCode);
        Assert.Contains("hello world", result.Value.OutputTail);
        Assert.Contains("err", result.Value.OutputTail);
        Assert.False(result.Value.StillRunning);
    }

    [Fact]
    public async Task RunProcess_WithoutWait_ReturnsJobAndCancelKillsTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        (ProcessTools tools, JobManager jobs) = CreateTools(Options());

        OperationResult<RunProcessResult> started = await tools.RunProcess("cmd.exe", ["/c", "ping -n 60 127.0.0.1 >nul"]);
        Assert.True(started.Success, started.Error);
        Assert.True(started.Value!.StillRunning);

        string id = started.Value.Job.Id;
        await Task.Delay(500);
        jobs.Cancel(id);
        OperationResult<JobInfo> final = await jobs.WaitAsync(id, TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.Equal(JobState.Cancelled, final.Value!.State);
    }

    [Fact]
    public async Task RunProcess_RunsRootScriptAndHonorsWorkingDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(Path.Combine(_root, "hi.cmd"), "@echo off\r\ncd\r\necho arg=%1\r\n");
        Directory.CreateDirectory(Path.Combine(_root, "work"));
        (ProcessTools tools, _) = CreateTools(Options());

        OperationResult<RunProcessResult> result = await tools.RunProcess("tools:hi.cmd", ["one two"], workingDirectory: "tools:work", wait: 30);

        Assert.True(result.Success, result.Error);
        Assert.Equal(JobState.Succeeded, result.Value!.Job.State);
        Assert.Contains(Path.Combine(_root, "work"), result.Value.OutputTail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("arg=\"one two\"", result.Value.OutputTail);
    }

    [Fact]
    public async Task RunProcess_EnforcesPathPolicy()
    {
        File.WriteAllText(Path.Combine(_root, "tool.exe"), "x");
        (ProcessTools execute, _) = CreateTools(Options());
        (ProcessTools readOnly, _) = CreateTools(Options(RootAccess.Read));
        (ProcessTools noLookup, _) = CreateTools(Options(pathLookup: false));

        Assert.Equal("access_denied", (await readOnly.RunProcess("tools:tool.exe")).ErrorCode);
        Assert.Equal("path_lookup_disabled", (await noLookup.RunProcess("cmd.exe")).ErrorCode);
        Assert.Equal("outside_root", (await execute.RunProcess(@"C:\Windows\System32\cmd.exe")).ErrorCode);
        Assert.Equal("unknown_root", (await execute.RunProcess("cmd.exe", workingDirectory: "other:x")).ErrorCode);
        Assert.Equal("not_found", (await execute.RunProcess("cmd.exe", workingDirectory: "tools:absent")).ErrorCode);
        Assert.Equal("invalid_request", (await execute.RunProcess("cmd.exe", context: "root")).ErrorCode);
        Assert.Equal("invalid_request", (await execute.RunProcess("cmd.exe", ["a\0"])).ErrorCode);
    }

    [Fact]
    public async Task RunProcess_ReportsNoInteractiveSession()
    {
        (ProcessTools tools, JobManager jobs) = CreateTools(Options(), new NoSessionLauncher());

        OperationResult<RunProcessResult> result = await tools.RunProcess("cmd.exe", context: "interactive", elevated: true);

        Assert.False(result.Success);
        Assert.Equal("no_interactive_session", result.ErrorCode);
        Assert.Empty(jobs.List());
    }

    [Fact]
    public async Task CloseApp_ReportsNoInteractiveSession()
    {
        (ProcessTools tools, _) = CreateTools(Options(), new NoSessionLauncher());

        OperationResult<CloseReport> result = await tools.CloseApp(processName: "notepad");

        Assert.Equal("no_interactive_session", result.ErrorCode);
        Assert.Equal("invalid_request", (await tools.CloseApp()).ErrorCode);
        Assert.Equal("invalid_request", (await tools.CloseApp(titleRegex: "(", context: "service")).ErrorCode);
        Assert.Equal("refused", (await tools.CloseApp(pid: Environment.ProcessId)).ErrorCode);
    }

    [Fact]
    public void ListProcesses_FindsOwnProcess()
    {
        (ProcessTools tools, _) = CreateTools(Options());

        IReadOnlyList<ProcessSummary> own = tools.ListProcesses(pid: Environment.ProcessId);

        ProcessSummary summary = Assert.Single(own);
        Assert.Equal(Environment.ProcessId, summary.Pid);
        Assert.NotNull(summary.StartTime);
        Assert.Contains(tools.ListProcesses(nameFilter: summary.Name.ToUpperInvariant()), item => item.Pid == summary.Pid);
        Assert.Empty(tools.ListProcesses(nameFilter: "no-such-process-name-xyz"));
    }

    [Fact]
    public void KillProcess_RefusesProtectedTargetsAndValidatesInput()
    {
        (ProcessTools tools, _) = CreateTools(Options());

        Assert.Equal("refused", tools.KillProcess(pid: Environment.ProcessId).ErrorCode);
        Assert.Equal("refused", tools.KillProcess(pid: 0).ErrorCode);
        Assert.Equal("refused", tools.KillProcess(pid: 4).ErrorCode);
        Assert.Equal("invalid_request", tools.KillProcess().ErrorCode);
        Assert.Equal("invalid_request", tools.KillProcess(pid: 5, processName: "x").ErrorCode);
        Assert.Equal("not_found", tools.KillProcess(processName: "no-such-process-name-xyz").ErrorCode);
    }

    [Fact]
    public void KillProcess_KillsByPidAndEntireTreeByName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        (ProcessTools tools, _) = CreateTools(Options());
        using Process single = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;

        OperationResult<IReadOnlyList<KillOutcome>> byPid = tools.KillProcess(pid: single.Id);
        Assert.True(byPid.Success, byPid.Error);
        Assert.True(byPid.Value!.Single().Killed);
        Assert.True(single.WaitForExit(5000));

        using Process parent = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
        Thread.Sleep(700);
        OperationResult<IReadOnlyList<KillOutcome>> tree = tools.KillProcess(pid: parent.Id, entireTree: true);
        Assert.True(tree.Success, tree.Error);
        Assert.True(parent.WaitForExit(5000));
    }

    [Fact]
    public void InteractiveAvailability_ReportsAStableErrorWhenUnavailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        OperationResult<bool> result = new ProcessLauncher().CheckAvailability(LaunchContext.Interactive, elevated: false);

        if (!result.Success)
        {
            Assert.Contains(result.ErrorCode, new[] { "no_interactive_session", "token_unavailable" });
        }
    }

    [InteractiveSystemFact]
    [Trait("Category", "WindowsElevated")]
    public async Task Interactive_RunsAsConsoleUserAndCapturesOutput()
    {
        using JobManagerScope scope = new(_root);
        ProcessLauncher launcher = new();
        JobContext output = scope.Context();

        int exitCode = await launcher.RunAsync(
            new LaunchRequest(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                ["/c", "whoami & echo %VESTIGO_TEST%"],
                Environment.SystemDirectory,
                new Dictionary<string, string> { ["VESTIGO_TEST"] = "interactive-ok" },
                LaunchContext.Interactive,
                Elevated: false),
            output,
            CancellationToken.None);

        string text = File.ReadAllText(output.OutputPath);
        Assert.Equal(0, exitCode);
        Assert.Contains("interactive-ok", text);
        Assert.DoesNotContain("nt authority\\system", text, StringComparison.OrdinalIgnoreCase);
    }

    private static FakeWindowClosePlatform Platform(VisibleWindow window, ProcessIdentity identity, CloseWaitResult wait)
    {
        FakeWindowClosePlatform platform = new();
        platform.Windows.Add(window);
        platform.Identities[identity.Pid] = identity;
        platform.Waits[identity.Pid] = wait;
        return platform;
    }

    private sealed class FakeWindowClosePlatform : IWindowClosePlatform
    {
        public List<VisibleWindow> Windows { get; } = [];

        public Dictionary<int, ProcessIdentity> Identities { get; } = [];

        public Dictionary<int, CloseWaitResult> Waits { get; } = [];

        public Dictionary<nint, int> PostErrors { get; } = [];

        public List<string> Events { get; } = [];

        public IReadOnlyList<VisibleWindow> EnumerateVisibleWindows() => Windows;

        public bool TryPostClose(nint hwnd, out int error)
        {
            Events.Add("post:" + hwnd.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (PostErrors.TryGetValue(hwnd, out error))
            {
                return false;
            }

            error = 0;
            return true;
        }

        public ProcessIdentity CaptureIdentity(int pid)
        {
            Events.Add("capture:" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Identities[pid];
        }

        public CloseWaitResult Wait(ProcessIdentity identity, TimeSpan remaining)
        {
            Events.Add("wait:" + identity.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(remaining >= TimeSpan.Zero);
            return Waits[identity.Pid];
        }
    }

    private sealed class NoSessionLauncher : IProcessLauncher
    {
        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) =>
            context == LaunchContext.Interactive
                ? OperationResult<bool>.Fail("no_interactive_session", "No user is logged on at the console.")
                : OperationResult<bool>.Ok(true);

        public Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken) =>
            throw new ProcessLaunchException("no_interactive_session", "No user is logged on at the console.");
    }

    private sealed class JobManagerScope : IDisposable
    {
        private readonly string _directory;

        public JobManagerScope(string root) => _directory = Directory.CreateDirectory(Path.Combine(root, "interactive")).FullName;

        public JobContext Context()
        {
            string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".log");
            File.WriteAllBytes(path, []);
            return new JobContext("test", path, 1024 * 1024);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Runs only as SYSTEM with a user logged on at the console (needs VESTIGO_TEST_INTERACTIVE=1).</summary>
    private sealed class InteractiveSystemFactAttribute : FactAttribute
    {
        public InteractiveSystemFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("VESTIGO_TEST_INTERACTIVE") != "1")
            {
                Skip = "Set VESTIGO_TEST_INTERACTIVE=1 and run as SYSTEM with a console user logged on.";
                return;
            }

            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (!identity.IsSystem)
            {
                Skip = "Requires running as SYSTEM.";
            }
        }
    }
}
