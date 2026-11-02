namespace Vestigo.ScheduledTasks;

/// <summary>Stable lowercase state names used in tool results and filters.</summary>
public static class ScheduledTaskStates
{
    public const string Unknown = "unknown";
    public const string Disabled = "disabled";
    public const string Queued = "queued";
    public const string Ready = "ready";
    public const string Running = "running";

    public static readonly IReadOnlyList<string> All = [Unknown, Disabled, Queued, Ready, Running];

    /// <summary>Maps the <c>TASK_STATE</c> value to its name.</summary>
    public static string FromCode(int code) => code switch
    {
        1 => Disabled,
        2 => Queued,
        3 => Ready,
        4 => Running,
        _ => Unknown
    };
}

public sealed record ScheduledTaskInfo(
    string Path,
    string Name,
    string Folder,
    string State,
    bool Enabled,
    bool Hidden,
    DateTimeOffset? LastRunTime,
    DateTimeOffset? NextRunTime,
    int LastTaskResult,
    int NumberOfMissedRuns,
    string? Author,
    string? Description);

public sealed record ScheduledTaskPrincipal(string? UserId, string? GroupId, string? LogonType, string? RunLevel);

public sealed record ScheduledTaskAction(string Type, string? Command, string? Arguments, string? WorkingDirectory, string? ClassId);

public sealed record ScheduledTaskTrigger(string Type, bool Enabled, string? StartBoundary, string? EndBoundary, string? Repetition);

public sealed record ScheduledTaskSettings(
    bool? AllowStartOnDemand,
    bool? StartWhenAvailable,
    bool? RunOnlyIfNetworkAvailable,
    bool? DisallowStartIfOnBatteries,
    bool? StopIfGoingOnBatteries,
    bool? WakeToRun,
    string? ExecutionTimeLimit,
    string? MultipleInstancesPolicy);

public sealed record ScheduledTaskDetails(
    ScheduledTaskInfo Task,
    ScheduledTaskPrincipal Principal,
    IReadOnlyList<ScheduledTaskAction> Actions,
    IReadOnlyList<ScheduledTaskTrigger> Triggers,
    ScheduledTaskSettings Settings,
    string Xml);

public sealed record ScheduledTaskList(int Total, int Count, bool Truncated, IReadOnlyList<ScheduledTaskInfo> Tasks);

public sealed record ScheduledTaskHistoryEntry(
    DateTimeOffset? Time,
    int EventId,
    string Level,
    string? Task,
    string? InstanceId,
    string? ResultCode,
    string? Message);

public sealed record ScheduledTaskHistory(string Path, bool Truncated, IReadOnlyList<ScheduledTaskHistoryEntry> Entries);

public sealed record ScheduledTaskRunResult(
    string Path,
    string? InstanceId,
    string State,
    bool Waited,
    bool StillRunning,
    int? LastTaskResult);

/// <summary>Failure of the Task Scheduler service carrying a stable error code.</summary>
public sealed class ScheduledTaskException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
