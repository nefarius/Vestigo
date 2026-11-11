using Vestigo.Abstractions;
using Vestigo.Configuration;
using Vestigo.Modules;
using Vestigo.ScheduledTasks;

using Scheduler = Microsoft.Win32.TaskScheduler;

namespace Vestigo.Server.Tests;

public sealed class ScheduledTaskToolsTests
{
    private readonly FakeScheduler _scheduler = new();
    private readonly VestigoOptions _options = new();
    private readonly ScheduledTaskTools _tools;

    public ScheduledTaskToolsTests()
    {
        _tools = new ScheduledTaskTools(_scheduler, new ScheduledTaskHistoryReader(), _options)
        {
            PollInterval = TimeSpan.FromMilliseconds(5)
        };
        _scheduler.Add("\\Vendor\\Nightly", ScheduledTaskStates.Ready);
        _scheduler.Add("\\Vendor\\Busy", ScheduledTaskStates.Running);
        _scheduler.Add("\\Microsoft\\Windows\\Defrag\\ScheduledDefrag", ScheduledTaskStates.Ready);
    }

    private static string[] Paths(OperationResult<ScheduledTaskList> result) =>
        result.Value!.Tasks.Select(task => task.Path).ToArray();

    [Fact]
    public void List_FiltersAndTruncates()
    {
        Assert.Equal(3, _tools.ListScheduledTasks().Value!.Total);
        Assert.Equal(["\\Vendor\\Nightly"], Paths(_tools.ListScheduledTasks(nameContains: "night")));
        Assert.Equal(["\\Vendor\\Busy"], Paths(_tools.ListScheduledTasks(state: "Running")));
        Assert.Equal("invalid_state", _tools.ListScheduledTasks(state: "bogus").ErrorCode);
        ScheduledTaskList page = _tools.ListScheduledTasks(limit: 1).Value!;
        Assert.Equal(3, page.Total);
        Assert.Equal(1, page.Count);
        Assert.True(page.Truncated);
    }

    [Fact]
    public void Get_ReturnsTaskOrNotFound_AndValidatesPath()
    {
        Assert.True(_tools.GetScheduledTask("Vendor/Nightly").Success);
        Assert.Equal("not_found", _tools.GetScheduledTask("\\Vendor\\Missing").ErrorCode);
        Assert.Equal("invalid_path", _tools.GetScheduledTask("\\..\\x").ErrorCode);
        Assert.Equal("invalid_path", _tools.GetScheduledTask("").ErrorCode);
        Assert.Equal("invalid_path", _tools.GetScheduledTask("\\\\").ErrorCode);
        Assert.Equal("invalid_path", _tools.GetScheduledTask("\\").ErrorCode);
        Assert.Equal("invalid_path", _tools.GetScheduledTaskHistory("\\a' or '1'='1").ErrorCode);
    }

    [Fact]
    public async Task ChangeTools_AreRefused_WhenChangesDisabled()
    {
        Assert.Equal("changes_disabled", (await _tools.RunScheduledTask("\\Vendor\\Nightly")).ErrorCode);
        Assert.Equal("changes_disabled", _tools.StopScheduledTask("\\Vendor\\Busy").ErrorCode);
        Assert.Equal("changes_disabled", _tools.EnableScheduledTask("\\Vendor\\Nightly").ErrorCode);
        Assert.Equal("changes_disabled", _tools.DisableScheduledTask("\\Vendor\\Nightly").ErrorCode);
        Assert.Equal("changes_disabled", _tools.DeleteScheduledTask("\\Vendor\\Nightly").ErrorCode);
        Assert.Empty(_scheduler.Calls);
    }

    [Fact]
    public async Task ProtectedPaths_RefuseDeleteAndDisable_ButAllowRunStopEnable()
    {
        _options.ScheduledTasks.AllowChanges = true;
        const string Protected = "\\microsoft\\windows\\Defrag\\ScheduledDefrag";

        Assert.Equal("protected_task", _tools.DeleteScheduledTask(Protected).ErrorCode);
        Assert.Equal("protected_task", _tools.DisableScheduledTask(Protected).ErrorCode);
        Assert.True((await _tools.RunScheduledTask(Protected)).Success);
        Assert.True(_tools.StopScheduledTask(Protected).Success);
        Assert.True(_tools.EnableScheduledTask(Protected).Success);
    }

    [Fact]
    public void DeleteDisableEnable_ChangeScheduler()
    {
        _options.ScheduledTasks.AllowChanges = true;

        Assert.False(_tools.DisableScheduledTask("\\Vendor\\Nightly").Value!.Enabled);
        Assert.True(_tools.EnableScheduledTask("\\Vendor\\Nightly").Value!.Enabled);
        Assert.True(_tools.DeleteScheduledTask("\\Vendor\\Nightly").Success);
        Assert.Equal("not_found", _tools.DeleteScheduledTask("\\Vendor\\Nightly").ErrorCode);
    }

    [Fact]
    public async Task Run_WaitsUntilTaskFinishes()
    {
        _options.ScheduledTasks.AllowChanges = true;
        _scheduler.FinishAfterPolls["\\Vendor\\Busy"] = 3;

        OperationResult<ScheduledTaskRunResult> immediate = await _tools.RunScheduledTask("\\Vendor\\Busy");
        Assert.True(immediate.Success);
        Assert.False(immediate.Value!.Waited);
        Assert.True(immediate.Value.StillRunning);

        OperationResult<ScheduledTaskRunResult> waited = await _tools.RunScheduledTask("\\Vendor\\Busy", wait: 5);
        Assert.True(waited.Success);
        Assert.False(waited.Value!.StillRunning);
        Assert.Equal(0x41301, waited.Value.LastTaskResult);
    }

    [Fact]
    public async Task Run_MapsSchedulerErrors()
    {
        _options.ScheduledTasks.AllowChanges = true;
        _scheduler.Failure = new ScheduledTaskException("task_disabled", "disabled");

        Assert.Equal("task_disabled", (await _tools.RunScheduledTask("\\Vendor\\Nightly")).ErrorCode);
    }

    [Fact]
    public void Options_ValidateProtectedPaths()
    {
        Assert.Throws<InvalidOperationException>(() => new ScheduledTasksOptions { ProtectedPaths = [" "] }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ScheduledTasksOptions { ProtectedPaths = ["Vendor"] }.Validate());
        new ScheduledTasksOptions { ProtectedPaths = ["\\Vendor\\"] }.Validate();
        Assert.True(new ScheduledTasksOptions().IsProtected("\\MICROSOFT\\Windows\\X"));
    }

    [Fact]
    public void PrivilegedWarning_NamesScheduledTaskChanges()
    {
        VestigoOptions options = new()
        {
            ListenAddress = "127.0.0.1",
            ScheduledTasks = { AllowChanges = true },
            Modules = ModuleRegistry.All.ToDictionary(
                module => module.Name,
                module => new ModuleSettings { Enabled = module.Name == "ScheduledTasks" },
                StringComparer.OrdinalIgnoreCase)
        };

        string? warning = ModuleRegistry.GetPrivilegedModuleWarning(options);

        Assert.NotNull(warning);
        Assert.Contains("Scheduled task changes", warning, StringComparison.Ordinal);
        options.ScheduledTasks.AllowChanges = false;
        Assert.Null(ModuleRegistry.GetPrivilegedModuleWarning(options));
    }

    [Fact]
    public void Parse_ReadsPrincipalActionsTriggersAndSettings()
    {
        const string Xml = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><TimeTrigger><StartBoundary>2026-01-01T00:00:00</StartBoundary><Repetition><Interval>PT1H</Interval></Repetition></TimeTrigger></Triggers>
              <Principals><Principal id="A"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings><StartWhenAvailable>true</StartWhenAvailable><ExecutionTimeLimit>PT72H</ExecutionTimeLimit></Settings>
              <Actions><Exec><Command>C:\tool.exe</Command><Arguments>/x</Arguments></Exec></Actions>
            </Task>
            """;

        ScheduledTaskDetails details = ComScheduledTaskService.Parse(FakeScheduler.Info("\\T", ScheduledTaskStates.Ready), Xml);

        Assert.Equal("S-1-5-18", details.Principal.UserId);
        Assert.Equal("HighestAvailable", details.Principal.RunLevel);
        ScheduledTaskAction action = Assert.Single(details.Actions);
        Assert.Equal("C:\\tool.exe", action.Command);
        Assert.Equal("/x", action.Arguments);
        Assert.Equal("TimeTrigger", Assert.Single(details.Triggers).Type);
        Assert.True(details.Settings.StartWhenAvailable);
        Assert.Equal("PT72H", details.Settings.ExecutionTimeLimit);
    }

    [Fact]
    public void RealScheduler_ListsAndRoundTripsATask()
    {
        ComScheduledTaskService service = new();
        IReadOnlyList<ScheduledTaskInfo> tasks = service.List("\\", true, false);

        Assert.NotEmpty(tasks);
        ScheduledTaskInfo first = tasks[0];
        ScheduledTaskDetails? details = service.Get(first.Path);
        Assert.NotNull(details);
        Assert.Equal(first.Path, details.Task.Path);
        Assert.Contains("<Task", details.Xml, StringComparison.Ordinal);
        Assert.Null(service.Get("\\Vestigo-does-not-exist\\nope"));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070002), "not_found")]
    [InlineData(unchecked((int)0x80070003), "not_found")]
    [InlineData(unchecked((int)0x80070005), "access_denied")]
    [InlineData(unchecked((int)0x8007007B), "invalid_path")]
    [InlineData(unchecked((int)0x8004131F), "already_running")]
    [InlineData(unchecked((int)0x8004130B), "not_running")]
    [InlineData(unchecked((int)0x80041326), "task_disabled")]
    [InlineData(unchecked((int)0x80040154), "scheduler_unavailable")]
    [InlineData(unchecked((int)0x80004005), "task_error")]
    public void Adapter_PreservesNativeErrorCodes(int hresult, string code)
    {
        Assert.Equal(code, ComScheduledTaskService.Map(new System.Runtime.InteropServices.COMException("test", hresult)).Code);
    }

    [Fact]
    public void Parse_KeepsOmittedValuesNullAndExplicitValuesUnchanged()
    {
        const string xml = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Principals><Principal><GroupId>S-1-5-32-545</GroupId><LogonType>Group</LogonType></Principal></Principals>
              <Settings><WakeToRun>false</WakeToRun><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy></Settings>
              <Triggers><EventTrigger><Enabled>false</Enabled><EndBoundary>2026-12-01T00:00:00Z</EndBoundary>
                <Repetition><Interval>PT30M</Interval><Duration>P1D</Duration></Repetition></EventTrigger></Triggers>
              <Actions><ComHandler><ClassId>{11111111-1111-1111-1111-111111111111}</ClassId></ComHandler></Actions>
            </Task>
            """;

        ScheduledTaskDetails details = ComScheduledTaskService.Parse(FakeScheduler.Info("\\T", ScheduledTaskStates.Ready), xml);

        Assert.Equal(xml, details.Xml);
        Assert.Null(details.Principal.UserId);
        Assert.Null(details.Principal.RunLevel);
        Assert.Equal("S-1-5-32-545", details.Principal.GroupId);
        Assert.Equal("Group", details.Principal.LogonType);
        Assert.Null(details.Settings.AllowStartOnDemand);
        Assert.Null(details.Settings.ExecutionTimeLimit);
        Assert.False(details.Settings.WakeToRun);
        Assert.Equal("IgnoreNew", details.Settings.MultipleInstancesPolicy);
        ScheduledTaskTrigger trigger = Assert.Single(details.Triggers);
        Assert.False(trigger.Enabled);
        Assert.Null(trigger.StartBoundary);
        Assert.Equal("2026-12-01T00:00:00Z", trigger.EndBoundary);
        Assert.Equal("every PT30M for P1D", trigger.Repetition);
        ScheduledTaskAction action = Assert.Single(details.Actions);
        Assert.Equal("ComHandler", action.Type);
        Assert.Null(action.Command);
        Assert.Equal("{11111111-1111-1111-1111-111111111111}", action.ClassId);
    }

    [Fact]
    public void Adapter_PreservesNeverRunAndLocalTimeSemantics()
    {
        Assert.Null(ComScheduledTaskService.ToTime(new DateTime(1899, 12, 30)));
        DateTime time = new(2026, 1, 15, 12, 30, 0, DateTimeKind.Unspecified);
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Local)), ComScheduledTaskService.ToTime(time));
    }

    [Fact]
    public void RealScheduler_MissingTaskAndFolderPreserveNotFound()
    {
        ComScheduledTaskService service = new();
        string path = "\\Vestigo-missing-" + Guid.NewGuid().ToString("N");

        Assert.Null(service.Get(path));
        Assert.Equal("not_found", Assert.Throws<ScheduledTaskException>(() => service.Run(path, [])).Code);
        Assert.Equal("not_found", Assert.Throws<ScheduledTaskException>(() => service.Stop(path)).Code);
        Assert.Equal("not_found", Assert.Throws<ScheduledTaskException>(() => service.SetEnabled(path, false)).Code);
        Assert.Equal("not_found", Assert.Throws<ScheduledTaskException>(() => service.Delete(path)).Code);
        Assert.Equal("not_found", Assert.Throws<ScheduledTaskException>(() => service.List(path, false, false)).Code);
    }

    [Fact]
    public void CachedDefinition_DoesNotCreateDefinitionForCleanup()
    {
        // No native references exist: the accessor must only read the pinned wrapper's cache.
        Scheduler.Task task = (Scheduler.Task)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Scheduler.Task));
        Assert.Null(ComScheduledTaskService.CachedDefinition(task));
    }

    [Fact]
    public async Task RealScheduler_TemporaryTasksRoundTripAndSupportChanges()
    {
        Assert.SkipUnless(Environment.UserInteractive, "Temporary task execution requires an interactive logon for this account.");
        using Scheduler.TaskService owner = new();
        using Scheduler.TaskFolder root = owner.RootFolder;
        string folderName = "Vestigo-test-" + Guid.NewGuid().ToString("N");
        Scheduler.TaskFolder? folder = null;
        try
        {
            try
            {
                folder = root.CreateFolder(folderName);
            }
            catch (UnauthorizedAccessException)
            {
                Assert.Skip("Task Scheduler does not permit this account to create a temporary test folder.");
            }

            using Scheduler.TaskFolder child = folder!.CreateFolder("Child");
            Register(owner, folder, "Visible", hidden: false, "/c exit $(Arg0)");
            Register(owner, folder, "Hidden", hidden: true, "/c exit 0");
            Register(owner, child, "Nested", hidden: false, "/c ping -n 30 127.0.0.1 >nul");
            ComScheduledTaskService service = new();
            string visible = folder.Path + "\\Visible";
            string nested = child.Path + "\\Nested";

            using (Scheduler.TaskCollection tasks = folder.GetTasks())
            using (Scheduler.Task task = tasks["Visible"])
            {
                Scheduler.TaskDefinition? definition = ComScheduledTaskService.CachedDefinition(task);
                Assert.NotNull(definition);
                Assert.Same(definition, task.Definition);
                Assert.Contains("Task", definition.XmlText);
                definition.Dispose();
                Assert.Throws<System.Runtime.InteropServices.InvalidComObjectException>(() => definition.XmlText);
            }

            using (Scheduler.TaskCollection tasks = folder.GetTasks())
            {
                Scheduler.Task task = tasks["Visible"];
                using Scheduler.TaskDefinition? definition = ComScheduledTaskService.CachedDefinition(task);
                Scheduler.RunningTask? running = null;
                try
                {
                    running = task.Run("0");
                    Assert.NotNull(running);
                    Assert.Null(ComScheduledTaskService.CachedDefinition(running));
                    Assert.True(Guid.TryParse(ComScheduledTaskService.ReadInstanceAndDispose(running), out _));
                    Assert.Throws<System.Runtime.InteropServices.InvalidComObjectException>(() => running.InstanceGuid);
                }
                finally
                {
                    // The adapter takes ownership once running is non-null.
                    if (running is null) task.Dispose();
                }
            }
            await WaitForStopped(service, visible);

            Assert.Equal([visible], service.List(folder.Path, false, false).Select(t => t.Path));
            Assert.Equal(2, service.List(folder.Path, false, true).Count);
            Assert.Equal(3, service.List(folder.Path, true, true).Count);
            Assert.Equal(2, service.List(folder.Path, true, false).Count);
            Assert.True(service.Get(folder.Path + "\\Hidden")!.Task.Hidden);
            Assert.Equal(NativeTimes(visible), (service.Get(visible)!.Task.LastRunTime, service.Get(visible)!.Task.NextRunTime));

            service.SetEnabled(visible, false);
            Assert.False(service.Get(visible)!.Task.Enabled);
            Assert.Equal("task_disabled", Assert.Throws<ScheduledTaskException>(() => service.Run(visible, [])).Code);
            service.SetEnabled(visible, true);
            Assert.True(service.Get(visible)!.Task.Enabled);

            Assert.True(Guid.TryParse(service.Run(visible, ["7"]), out _));
            await WaitForStopped(service, visible);
            Assert.Equal(7, service.Get(visible)!.Task.LastTaskResult);
            Assert.NotNull(service.Get(visible)!.Task.LastRunTime);
            Assert.True(Guid.TryParse(service.Run(nested, []), out _));
            service.Stop(nested);
            await WaitForStopped(service, nested);
            service.Delete(visible);
            Assert.Null(service.Get(visible));
        }
        finally
        {
            if (folder is not null)
            {
                using (folder)
                {
                    // Cleanup touches only the GUID-named folder created by this test.
                    foreach (string path in new[] { "Visible", "Hidden", "Child\\Nested" })
                    {
                        using Scheduler.Task? task = owner.GetTask(folder.Path + "\\" + path);
                        task?.Stop();
                        folder.DeleteTask(path, exceptionOnNotExists: false);
                    }
                    folder.DeleteFolder("Child", exceptionOnNotExists: false);
                }
                root.DeleteFolder(folderName);
            }
        }
    }

    private static void Register(Scheduler.TaskService service, Scheduler.TaskFolder folder, string name, bool hidden, string arguments)
    {
        using Scheduler.TaskDefinition definition = service.NewTask();
        definition.Settings.Hidden = hidden;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = TimeSpan.FromMinutes(1);
        definition.Actions.Add(new Scheduler.ExecAction(Path.Combine(Environment.SystemDirectory, "cmd.exe"), arguments));
        using System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        using Scheduler.Task task = folder.RegisterTaskDefinition(name, definition, Scheduler.TaskCreation.Create,
            identity.Name, null, Scheduler.TaskLogonType.InteractiveToken);
    }

    private static async Task WaitForStopped(ComScheduledTaskService service, string path)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (service.Get(path)!.Task.State is ScheduledTaskStates.Queued or ScheduledTaskStates.Running)
        {
            Assert.True(DateTime.UtcNow < deadline, "The temporary task did not stop in time.");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private static (DateTimeOffset? Last, DateTimeOffset? Next) NativeTimes(string path)
    {
        // Compare against the old COM contract: some Windows builds report 1999-11-30 for never-run
        // tasks, rather than the OLE zero date. Preserve those native values instead of inventing nulls.
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? folder = null;
        object? task = null;
        try
        {
            service.Connect();
            folder = service.GetFolder("\\");
            task = ((dynamic)folder).GetTask(path);
            DateTime last = ((dynamic)task).LastRunTime;
            DateTime next = ((dynamic)task).NextRunTime;
            return (last.Year > 1990 ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Local)) : null,
                next.Year > 1990 ? new DateTimeOffset(DateTime.SpecifyKind(next, DateTimeKind.Local)) : null);
        }
        finally
        {
            if (task is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(task);
            if (folder is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(folder);
            System.Runtime.InteropServices.Marshal.ReleaseComObject((object)service);
        }
    }

    private sealed class FakeScheduler : IScheduledTaskService
    {
        private readonly Dictionary<string, ScheduledTaskInfo> _tasks = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _polls = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Calls { get; } = [];

        public Dictionary<string, int> FinishAfterPolls { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ScheduledTaskException? Failure { get; set; }

        public static ScheduledTaskInfo Info(string path, string state) =>
            new(path, path[(path.LastIndexOf('\\') + 1)..], path[..Math.Max(1, path.LastIndexOf('\\'))], state,
                state != ScheduledTaskStates.Disabled, false, null, null, 0, 0, null, null);

        public void Add(string path, string state) => _tasks[path] = Info(path, state);

        public IReadOnlyList<ScheduledTaskInfo> List(string folder, bool recurse, bool includeHidden) => _tasks.Values.ToList();

        public ScheduledTaskDetails? Get(string path)
        {
            if (!_tasks.TryGetValue(path, out ScheduledTaskInfo? info))
            {
                return null;
            }

            if (info.State == ScheduledTaskStates.Running && FinishAfterPolls.TryGetValue(path, out int limit))
            {
                _polls[path] = _polls.GetValueOrDefault(path) + 1;
                if (_polls[path] > limit)
                {
                    info = _tasks[path] = info with { State = ScheduledTaskStates.Ready, LastTaskResult = 0x41301 };
                }
            }

            return new ScheduledTaskDetails(
                info,
                new ScheduledTaskPrincipal(null, null, null, null),
                [],
                [],
                new ScheduledTaskSettings(null, null, null, null, null, null, null, null),
                "<Task />");
        }

        public string? Run(string path, IReadOnlyList<string> arguments)
        {
            Calls.Add("run " + path);
            if (Failure is not null)
            {
                throw Failure;
            }

            _polls[path] = 0;
            return Guid.NewGuid().ToString();
        }

        public void Stop(string path) => Calls.Add("stop " + path);

        public void SetEnabled(string path, bool enabled)
        {
            Calls.Add($"enable {enabled} {path}");
            _tasks[path] = _tasks[path] with { Enabled = enabled, State = enabled ? ScheduledTaskStates.Ready : ScheduledTaskStates.Disabled };
        }

        public void Delete(string path)
        {
            Calls.Add("delete " + path);
            _tasks.Remove(path);
        }
    }
}
