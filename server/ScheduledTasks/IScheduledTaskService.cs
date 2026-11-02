namespace Vestigo.ScheduledTasks;

/// <summary>
///     Thin seam over the Task Scheduler 2.0 service. Implementations throw <see cref="ScheduledTaskException" />;
///     gating, protection, and validation live in <c>ScheduledTaskTools</c>. Paths are normalized with a leading
///     backslash before they reach the seam.
/// </summary>
public interface IScheduledTaskService
{
    IReadOnlyList<ScheduledTaskInfo> List(string folder, bool recurse, bool includeHidden);

    /// <summary>Returns full details, or null when no such task exists.</summary>
    ScheduledTaskDetails? Get(string path);

    /// <summary>Starts the task now and returns the instance id when known.</summary>
    string? Run(string path, IReadOnlyList<string> arguments);

    void Stop(string path);

    void SetEnabled(string path, bool enabled);

    void Delete(string path);
}
