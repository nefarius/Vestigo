using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

using Microsoft.Win32.TaskScheduler;

using Windows.Win32.Foundation;

using RegisteredTask = Microsoft.Win32.TaskScheduler.Task;

namespace Vestigo.ScheduledTasks;

/// <summary>
///     Task Scheduler through the maintained TaskScheduler wrapper. XML projection preserves absent optional
///     values and duration strings instead of substituting the wrapper's defaults or enum names.
/// </summary>
public sealed class ComScheduledTaskService : IScheduledTaskService
{
    private const int HResultNotFound = unchecked((int)0x80070002);
    private const int HResultPathNotFound = unchecked((int)0x80070003);
    private const int HResultAccessDenied = unchecked((int)0x80070005);
    private const int HResultInvalidName = unchecked((int)0x8007007B);
    private const int HResultAlreadyRunning = unchecked((int)0x8004131F);
    private const int HResultNotRunning = unchecked((int)0x8004130B);
    private const int HResultTaskDisabled = unchecked((int)0x80041326);

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public IReadOnlyList<ScheduledTaskInfo> List(string folder, bool recurse, bool includeHidden) =>
        Invoke(service =>
        {
            List<ScheduledTaskInfo> tasks = [];
            using TaskFolder root = GetFolder(service, folder);
            Walk(root, recurse, includeHidden, tasks);
            return (IReadOnlyList<ScheduledTaskInfo>)tasks;
        });

    public ScheduledTaskDetails? Get(string path)
    {
        try
        {
            return Invoke(service =>
            {
                using RegisteredTask task = GetTask(service, path);
                using TaskDefinition? definition = CachedDefinition(task);
                string xml = task.Xml;
                return Parse(ToInfo(task), xml);
            });
        }
        catch (ScheduledTaskException ex) when (ex.Code == "not_found")
        {
            return null;
        }
    }

    public string? Run(string path, IReadOnlyList<string> arguments) =>
        Invoke(service =>
        {
            RegisteredTask? task = GetTask(service, path);
            using TaskDefinition? definition = CachedDefinition(task);
            try
            {
                RunningTask? running = task.Run(arguments.ToArray());
                // In TaskScheduler 2.12.2, RunningTask shares its parent's registered-task RCW and
                // disposes both references. Transfer ownership so it is not released a second time.
                // https://github.com/dahall/TaskScheduler/blob/v2.12.2/TaskService/Task.cs
                if (running is null) return null;
                task = null;
                return ReadInstanceAndDispose(running);
            }
            finally
            {
                task?.Dispose();
            }
        });

    internal static string? ReadInstanceAndDispose(RunningTask running)
    {
        try
        {
            return running.InstanceGuid.ToString("B");
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            // 2.12.2 hides Task.Dispose instead of reimplementing IDisposable. A using
            // declaration calls the base interface slot and leaks the running-task RCW.
            running.Dispose();
        }
    }

    public void Stop(string path) => Invoke<object?>(service =>
    {
        using RegisteredTask task = GetTask(service, path);
        using TaskDefinition? definition = CachedDefinition(task);
        task.Stop();
        return null;
    });

    public void SetEnabled(string path, bool enabled) => Invoke<object?>(service =>
    {
        using RegisteredTask task = GetTask(service, path);
        using TaskDefinition? definition = CachedDefinition(task);
        task.Enabled = enabled;
        return null;
    });

    public void Delete(string path) => Invoke<object?>(service =>
    {
        (string folder, string name) = Split(path);
        using TaskFolder parent = GetFolder(service, folder);
        parent.DeleteTask(name, exceptionOnNotExists: true);
        return null;
    });

    private static TaskFolder GetFolder(TaskService service, string path) =>
        service.GetFolder(path) ?? throw new ScheduledTaskException("not_found", "No such scheduled task or folder.");

    private static RegisteredTask GetTask(TaskService service, string path)
    {
        (string folder, string name) = Split(path);
        using TaskFolder parent = GetFolder(service, folder);
        using TaskCollection tasks = parent.GetTasks();
        // TaskService.GetTask catches every native error and returns null. The collection indexer
        // preserves HRESULTs, so access_denied must not become not_found.
        return tasks[name];
    }

    private static (string Folder, string Name) Split(string path)
    {
        int index = path.LastIndexOf('\\');
        string folder = index <= 0 ? "\\" : path[..index];
        return (folder, path[(index + 1)..]);
    }

    // TaskScheduler 2.12.2 caches a definition when CreateTask materializes collection
    // entries, but Task.Dispose releases only the registered-task RCW. The public
    // Definition getter would create a new definition when none is cached. Read the
    // existing cache without acquiring another COM object; recheck on package upgrades.
    // RunningTask shares the registered-task RCW, but not this cached definition.
    // https://github.com/dahall/TaskScheduler/blob/v2.12.2/TaskService/Task.cs
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "myTD")]
    internal static extern ref TaskDefinition? CachedDefinition(RegisteredTask task);

    private static void Walk(TaskFolder folder, bool recurse, bool includeHidden, List<ScheduledTaskInfo> output)
    {
        using TaskCollection tasks = folder.GetTasks();
        foreach (RegisteredTask task in tasks)
        {
            using (task)
            {
                using TaskDefinition? definition = CachedDefinition(task);
                ScheduledTaskInfo info = ToInfo(task);
                // The wrapper's task collections always include hidden tasks.
                if (includeHidden || !info.Hidden) output.Add(info);
            }
        }

        if (!recurse)
        {
            return;
        }

        using TaskFolderCollection children = folder.SubFolders;
        foreach (TaskFolder child in children)
        {
            using (child) Walk(child, true, includeHidden, output);
        }
    }

    private static ScheduledTaskInfo ToInfo(RegisteredTask task)
    {
        string path = task.Path;
        int index = path.LastIndexOf('\\');
        string xml = task.Xml;
        XElement? definition = TryParse(xml);
        XElement? registration = definition?.Element(Ns + "RegistrationInfo");
        XElement? settings = definition?.Element(Ns + "Settings");
        return new ScheduledTaskInfo(
            path,
            task.Name,
            index <= 0 ? "\\" : path[..index],
            ScheduledTaskStates.FromCode((int)task.State),
            task.Enabled,
            string.Equals((string?)settings?.Element(Ns + "Hidden"), "true", StringComparison.OrdinalIgnoreCase),
            ToTime(task.LastRunTime),
            ToTime(task.NextRunTime),
            task.LastTaskResult,
            task.NumberOfMissedRuns,
            (string?)registration?.Element(Ns + "Author"),
            (string?)registration?.Element(Ns + "Description"));
    }

    internal static DateTimeOffset? ToTime(DateTime time)
    {
        // Preserve the existing OLE zero-date handling; the wrapper may normalize it to DateTime.MinValue.
        if (time.Year > 1990)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Local));
        }

        return null;
    }

    private static XElement? TryParse(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Root;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    internal static ScheduledTaskDetails Parse(ScheduledTaskInfo info, string xml)
    {
        XElement? root = TryParse(xml);
        XElement? principal = root?.Element(Ns + "Principals")?.Elements(Ns + "Principal").FirstOrDefault();
        ScheduledTaskPrincipal who = new(
            (string?)principal?.Element(Ns + "UserId"),
            (string?)principal?.Element(Ns + "GroupId"),
            (string?)principal?.Element(Ns + "LogonType"),
            (string?)principal?.Element(Ns + "RunLevel"));

        List<ScheduledTaskAction> actions = [];
        foreach (XElement action in root?.Element(Ns + "Actions")?.Elements() ?? [])
        {
            actions.Add(new ScheduledTaskAction(
                action.Name.LocalName,
                (string?)action.Element(Ns + "Command"),
                (string?)action.Element(Ns + "Arguments"),
                (string?)action.Element(Ns + "WorkingDirectory"),
                (string?)action.Element(Ns + "ClassId")));
        }

        List<ScheduledTaskTrigger> triggers = [];
        foreach (XElement trigger in root?.Element(Ns + "Triggers")?.Elements() ?? [])
        {
            XElement? repetition = trigger.Element(Ns + "Repetition");
            string? repeat = repetition is null
                ? null
                : $"every {(string?)repetition.Element(Ns + "Interval")} for {(string?)repetition.Element(Ns + "Duration") ?? "indefinitely"}";
            triggers.Add(new ScheduledTaskTrigger(
                trigger.Name.LocalName,
                !string.Equals((string?)trigger.Element(Ns + "Enabled"), "false", StringComparison.OrdinalIgnoreCase),
                (string?)trigger.Element(Ns + "StartBoundary"),
                (string?)trigger.Element(Ns + "EndBoundary"),
                repeat));
        }

        XElement? s = root?.Element(Ns + "Settings");
        ScheduledTaskSettings settings = new(
            Flag(s, "AllowStartOnDemand"),
            Flag(s, "StartWhenAvailable"),
            Flag(s, "RunOnlyIfNetworkAvailable"),
            Flag(s, "DisallowStartIfOnBatteries"),
            Flag(s, "StopIfGoingOnBatteries"),
            Flag(s, "WakeToRun"),
            (string?)s?.Element(Ns + "ExecutionTimeLimit"),
            (string?)s?.Element(Ns + "MultipleInstancesPolicy"));

        return new ScheduledTaskDetails(info, who, actions, triggers, settings, xml);
    }

    private static bool? Flag(XElement? settings, string name) =>
        settings?.Element(Ns + name) is { } element ? string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase) : null;

    private static T Invoke<T>(Func<TaskService, T> action)
    {
        try
        {
            using TaskService service = new();
            return action(service);
        }
        catch (UnauthorizedAccessException)
        {
            throw new ScheduledTaskException("access_denied", "Windows denied access to the Task Scheduler.");
        }
        catch (Exception ex) when (ex is COMException or IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // COM interop surfaces some HRESULTs as typed exceptions (for example FileNotFoundException).
            throw Map(ex);
        }
    }

    internal static ScheduledTaskException Map(Exception ex) => ex.HResult switch
    {
        HResultNotFound or HResultPathNotFound => new ScheduledTaskException("not_found", "No such scheduled task or folder."),
        HResultAccessDenied => new ScheduledTaskException("access_denied", "Windows denied access to the Task Scheduler."),
        HResultInvalidName => new ScheduledTaskException("invalid_path", "The task path is not valid."),
        HResultAlreadyRunning => new ScheduledTaskException("already_running", "The task is already running."),
        HResultNotRunning => new ScheduledTaskException("not_running", "The task is not running."),
        HResultTaskDisabled => new ScheduledTaskException("task_disabled", "The task is disabled; enable it first."),
        var code when code == HRESULT.REGDB_E_CLASSNOTREG.Value => new ScheduledTaskException("scheduler_unavailable", "The Task Scheduler COM class is not registered."),
        _ => new ScheduledTaskException("task_error", $"Task Scheduler error 0x{ex.HResult:X8}: {ex.Message}")
    };
}
