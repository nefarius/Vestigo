using Vestigo.Abstractions;
using Vestigo.Configuration;
using Vestigo.Modules;
using Vestigo.ScheduledTasks;

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
