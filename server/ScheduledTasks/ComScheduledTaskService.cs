using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace Vestigo.ScheduledTasks;

/// <summary>
///     Task Scheduler 2.0 through late-bound COM (<c>Schedule.Service</c>). The interfaces are dual IDispatch
///     with VARIANT arguments, so late binding avoids declaring them without adding a package. Actions,
///     triggers, and the principal are read from the task definition XML.
/// </summary>
public sealed class ComScheduledTaskService : IScheduledTaskService
{
    private const int EnumHidden = 1;
    private const int HResultNotFound = unchecked((int)0x80070002);
    private const int HResultPathNotFound = unchecked((int)0x80070003);
    private const int HResultAccessDenied = unchecked((int)0x80070005);
    private const int HResultInvalidName = unchecked((int)0x8007007B);
    private const int HResultAlreadyRunning = unchecked((int)0x8004131F);
    private const int HResultNotRunning = unchecked((int)0x8004130B);
    private const int HResultTaskDisabled = unchecked((int)0x80041326);

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public IReadOnlyList<ScheduledTaskInfo> List(string folder, bool recurse, bool includeHidden) =>
        Invoke(() =>
        {
            List<ScheduledTaskInfo> tasks = [];
            dynamic root = Connect().GetFolder(folder);
            Walk(root, recurse, includeHidden, tasks);
            return (IReadOnlyList<ScheduledTaskInfo>)tasks;
        });

    public ScheduledTaskDetails? Get(string path)
    {
        try
        {
            return Invoke(() =>
            {
                dynamic task = GetTask(path);
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
        Invoke(() =>
        {
            dynamic task = GetTask(path);
            object? parameters = arguments.Count == 0 ? null : arguments.Cast<object>().ToArray();
            dynamic running = task.Run(parameters);
            try
            {
                return (string?)running.InstanceGuid;
            }
            catch (COMException)
            {
                return null;
            }
        });

    public void Stop(string path) => Invoke<object?>(() =>
    {
        GetTask(path).Stop(0);
        return null;
    });

    public void SetEnabled(string path, bool enabled) => Invoke<object?>(() =>
    {
        GetTask(path).Enabled = enabled;
        return null;
    });

    public void Delete(string path) => Invoke<object?>(() =>
    {
        (string folder, string name) = Split(path);
        Connect().GetFolder(folder).DeleteTask(name, 0);
        return null;
    });

    private static dynamic Connect()
    {
        Type type = Type.GetTypeFromProgID("Schedule.Service")
                    ?? throw new ScheduledTaskException("scheduler_unavailable", "The Task Scheduler COM class is not registered.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static dynamic GetTask(string path)
    {
        (string folder, string name) = Split(path);
        return Connect().GetFolder(folder).GetTask(name);
    }

    private static (string Folder, string Name) Split(string path)
    {
        int index = path.LastIndexOf('\\');
        string folder = index <= 0 ? "\\" : path[..index];
        return (folder, path[(index + 1)..]);
    }

    private static void Walk(dynamic folder, bool recurse, bool includeHidden, List<ScheduledTaskInfo> output)
    {
        foreach (dynamic task in folder.GetTasks(includeHidden ? EnumHidden : 0))
        {
            output.Add(ToInfo(task));
        }

        if (!recurse)
        {
            return;
        }

        foreach (dynamic child in folder.GetFolders(0))
        {
            Walk(child, true, includeHidden, output);
        }
    }

    private static ScheduledTaskInfo ToInfo(dynamic task)
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
            (bool)task.Enabled,
            string.Equals((string?)settings?.Element(Ns + "Hidden"), "true", StringComparison.OrdinalIgnoreCase),
            ToTime(task.LastRunTime),
            ToTime(task.NextRunTime),
            (int)task.LastTaskResult,
            (int)task.NumberOfMissedRuns,
            (string?)registration?.Element(Ns + "Author"),
            (string?)registration?.Element(Ns + "Description"));
    }

    private static DateTimeOffset? ToTime(object value)
    {
        // Never-run and unscheduled tasks report the OLE zero date (1899-12-30).
        if (value is DateTime time && time.Year > 1990)
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

    private static T Invoke<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (ScheduledTaskException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw new ScheduledTaskException("access_denied", "Windows denied access to the Task Scheduler.");
        }
        catch (Exception ex) when (ex is COMException or IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // The runtime binder surfaces some HRESULTs as typed exceptions (for example FileNotFoundException).
            throw Map(ex);
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex)
        {
            throw new ScheduledTaskException("task_error", ex.Message);
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
        _ => new ScheduledTaskException("task_error", $"Task Scheduler error 0x{ex.HResult:X8}: {ex.Message}")
    };
}
