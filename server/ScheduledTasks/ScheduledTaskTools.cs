using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Abstractions;
using Vestigo.Configuration;

namespace Vestigo.ScheduledTasks;

/// <summary>
///     MCP tools for listing, inspecting, and controlling Windows scheduled tasks on this machine. Changing
///     tools need <c>Vestigo:ScheduledTasks:AllowChanges</c>; delete and disable also refuse
///     <c>Vestigo:ScheduledTasks:ProtectedPaths</c>.
/// </summary>
[McpServerToolType]
public sealed class ScheduledTaskTools(
    IScheduledTaskService scheduler,
    ScheduledTaskHistoryReader history,
    VestigoOptions options)
{
    private const int MaxWaitSeconds = 300;
    private const int MaxListLimit = 1000;
    private const int MaxHistoryLimit = 1000;

    /// <summary>Interval between state polls while waiting. Tests shorten it.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    [McpServerTool(Name = "list_scheduled_tasks")]
    [Description("Lists scheduled tasks with state, enabled flag, last and next run time, last result, author, and description, sorted by path.")]
    public OperationResult<ScheduledTaskList> ListScheduledTasks(
        [Description("Folder to list, such as \\ or \\Microsoft\\Windows.")] string folder = "\\",
        [Description("Include tasks in subfolders.")] bool recurse = true,
        [Description("Include hidden tasks.")] bool includeHidden = false,
        [Description("Case-insensitive substring of the task path.")] string? nameContains = null,
        [Description("Only tasks in this state: unknown, disabled, queued, ready or running.")] string? state = null,
        [Description("Maximum entries to return, 1 to 1000.")] int limit = 200)
    {
        OperationResult<string> normalized = NormalizePath(folder, allowRoot: true);
        if (!normalized.Success)
        {
            return OperationResult<ScheduledTaskList>.Fail(normalized.ErrorCode!, normalized.Error!);
        }

        string? stateFilter = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            stateFilter = state.Trim().ToLowerInvariant();
            if (!ScheduledTaskStates.All.Contains(stateFilter))
            {
                return OperationResult<ScheduledTaskList>.Fail("invalid_state", $"state must be one of {string.Join(", ", ScheduledTaskStates.All)}.");
            }
        }

        IReadOnlyList<ScheduledTaskInfo> all;
        try
        {
            all = scheduler.List(normalized.Value!, recurse, includeHidden);
        }
        catch (ScheduledTaskException ex)
        {
            return OperationResult<ScheduledTaskList>.Fail(ex.Code, ex.Message);
        }

        List<ScheduledTaskInfo> matches = all
            .Where(task => stateFilter is null || task.State == stateFilter)
            .Where(task => string.IsNullOrWhiteSpace(nameContains)
                || task.Path.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            .OrderBy(task => task.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<ScheduledTaskInfo> page = matches.Take(Math.Clamp(limit, 1, MaxListLimit)).ToList();
        return OperationResult<ScheduledTaskList>.Ok(new ScheduledTaskList(matches.Count, page.Count, matches.Count > page.Count, page));
    }

    [McpServerTool(Name = "get_scheduled_task")]
    [Description("Returns one task with its principal, actions, triggers, key settings, and the full definition XML. Arguments and XML can contain secrets.")]
    public OperationResult<ScheduledTaskDetails> GetScheduledTask([Description("Task path such as \\Vendor\\Nightly.")] string path)
    {
        OperationResult<string> normalized = NormalizePath(path);
        if (!normalized.Success)
        {
            return OperationResult<ScheduledTaskDetails>.Fail(normalized.ErrorCode!, normalized.Error!);
        }

        return Run(() => scheduler.Get(normalized.Value!) is { } details
            ? OperationResult<ScheduledTaskDetails>.Ok(details)
            : NotFound<ScheduledTaskDetails>(normalized.Value!));
    }

    [McpServerTool(Name = "get_scheduled_task_history")]
    [Description("Returns recent events for one task from the Microsoft-Windows-TaskScheduler/Operational log, newest first. Fails with history_disabled when that channel is not enabled.")]
    public OperationResult<ScheduledTaskHistory> GetScheduledTaskHistory(
        [Description("Task path such as \\Vendor\\Nightly.")] string path,
        [Description("Maximum events to return, 1 to 1000.")] int limit = 50)
    {
        OperationResult<string> normalized = NormalizePath(path);
        if (!normalized.Success)
        {
            return OperationResult<ScheduledTaskHistory>.Fail(normalized.ErrorCode!, normalized.Error!);
        }

        // Event data matches the task name case-sensitively; prefer the canonical path when the task still exists.
        // Deleted tasks and scheduler failures fall back to the normalized input so history stays readable.
        string historyPath = normalized.Value!;
        try
        {
            if (scheduler.Get(historyPath) is { } details)
            {
                historyPath = details.Task.Path;
            }
        }
        catch (ScheduledTaskException)
        {
        }

        return history.Read(historyPath, Math.Clamp(limit, 1, MaxHistoryLimit));
    }

    [McpServerTool(Name = "run_scheduled_task")]
    [Description("Starts a task now. It runs as the task's own principal (often SYSTEM), not as the Vestigo service account. Requires Vestigo:ScheduledTasks:AllowChanges. wait > 0 waits that many seconds for it to finish and returns the last result.")]
    public async Task<OperationResult<ScheduledTaskRunResult>> RunScheduledTask(
        [Description("Task path such as \\Vendor\\Nightly.")] string path,
        [Description("Optional parameters passed to the task's actions (replace $(Arg0), $(Arg1), ...).")] string[]? arguments = null,
        [Description("Seconds to wait (0 to 300) before returning. 0 returns immediately.")] int wait = 0,
        CancellationToken cancellationToken = default)
    {
        OperationResult<string> gate = Gate(path);
        if (!gate.Success)
        {
            return OperationResult<ScheduledTaskRunResult>.Fail(gate.ErrorCode!, gate.Error!);
        }

        string normalized = gate.Value!;
        string? instance;
        try
        {
            instance = scheduler.Run(normalized, arguments ?? []);
        }
        catch (ScheduledTaskException ex)
        {
            return OperationResult<ScheduledTaskRunResult>.Fail(ex.Code, ex.Message);
        }

        int seconds = Math.Clamp(wait, 0, MaxWaitSeconds);
        try
        {
            ScheduledTaskDetails? current = scheduler.Get(normalized);
            if (seconds > 0)
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
                while (current is { Task.State: ScheduledTaskStates.Running or ScheduledTaskStates.Queued } && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                    current = scheduler.Get(normalized);
                }
            }

            bool running = current?.Task.State is ScheduledTaskStates.Running or ScheduledTaskStates.Queued;
            return OperationResult<ScheduledTaskRunResult>.Ok(new ScheduledTaskRunResult(
                normalized,
                instance,
                current?.Task.State ?? ScheduledTaskStates.Unknown,
                seconds > 0,
                running,
                seconds > 0 && !running ? current?.Task.LastTaskResult : null));
        }
        catch (ScheduledTaskException ex)
        {
            return OperationResult<ScheduledTaskRunResult>.Fail(ex.Code, ex.Message);
        }
    }

    [McpServerTool(Name = "stop_scheduled_task")]
    [Description("Stops all running instances of a task. Requires Vestigo:ScheduledTasks:AllowChanges.")]
    public OperationResult<ScheduledTaskInfo> StopScheduledTask([Description("Task path such as \\Vendor\\Nightly.")] string path) =>
        Change(path, protectedCheck: false, scheduler.Stop);

    [McpServerTool(Name = "enable_scheduled_task")]
    [Description("Enables a task. Requires Vestigo:ScheduledTasks:AllowChanges.")]
    public OperationResult<ScheduledTaskInfo> EnableScheduledTask([Description("Task path such as \\Vendor\\Nightly.")] string path) =>
        Change(path, protectedCheck: false, task => scheduler.SetEnabled(task, true));

    [McpServerTool(Name = "disable_scheduled_task")]
    [Description("Disables a task so its triggers no longer fire. Requires Vestigo:ScheduledTasks:AllowChanges; tasks under Vestigo:ScheduledTasks:ProtectedPaths are refused.")]
    public OperationResult<ScheduledTaskInfo> DisableScheduledTask([Description("Task path such as \\Vendor\\Nightly.")] string path) =>
        Change(path, protectedCheck: true, task => scheduler.SetEnabled(task, false));

    [McpServerTool(Name = "delete_scheduled_task")]
    [Description("Permanently deletes a task definition. Requires Vestigo:ScheduledTasks:AllowChanges; tasks under Vestigo:ScheduledTasks:ProtectedPaths are refused. Returns the deleted task's last known state.")]
    public OperationResult<ScheduledTaskInfo> DeleteScheduledTask([Description("Task path such as \\Vendor\\Nightly.")] string path)
    {
        OperationResult<string> gate = Gate(path, protectedCheck: true);
        if (!gate.Success)
        {
            return OperationResult<ScheduledTaskInfo>.Fail(gate.ErrorCode!, gate.Error!);
        }

        try
        {
            ScheduledTaskDetails? existing = scheduler.Get(gate.Value!);
            if (existing is null)
            {
                return NotFound<ScheduledTaskInfo>(gate.Value!);
            }

            scheduler.Delete(gate.Value!);
            return OperationResult<ScheduledTaskInfo>.Ok(existing.Task);
        }
        catch (ScheduledTaskException ex)
        {
            return OperationResult<ScheduledTaskInfo>.Fail(ex.Code, ex.Message);
        }
    }

    private OperationResult<ScheduledTaskInfo> Change(string path, bool protectedCheck, Action<string> action)
    {
        OperationResult<string> gate = Gate(path, protectedCheck);
        if (!gate.Success)
        {
            return OperationResult<ScheduledTaskInfo>.Fail(gate.ErrorCode!, gate.Error!);
        }

        try
        {
            if (scheduler.Get(gate.Value!) is null)
            {
                return NotFound<ScheduledTaskInfo>(gate.Value!);
            }

            action(gate.Value!);
            return scheduler.Get(gate.Value!) is { } after
                ? OperationResult<ScheduledTaskInfo>.Ok(after.Task)
                : NotFound<ScheduledTaskInfo>(gate.Value!);
        }
        catch (ScheduledTaskException ex)
        {
            return OperationResult<ScheduledTaskInfo>.Fail(ex.Code, ex.Message);
        }
    }

    /// <summary>Checks the change gate and path validity, and optionally protection; returns the normalized path.</summary>
    private OperationResult<string> Gate(string path, bool protectedCheck = false)
    {
        if (!options.ScheduledTasks.AllowChanges)
        {
            return OperationResult<string>.Fail("changes_disabled", "Changing scheduled tasks is disabled. Set Vestigo:ScheduledTasks:AllowChanges to true.");
        }

        OperationResult<string> normalized = NormalizePath(path);
        if (normalized.Success && protectedCheck && options.ScheduledTasks.IsProtected(normalized.Value!))
        {
            return OperationResult<string>.Fail("protected_task", $"'{normalized.Value}' is protected by Vestigo:ScheduledTasks:ProtectedPaths.");
        }

        return normalized;
    }

    internal static OperationResult<string> NormalizePath(string? path, bool allowRoot = false)
    {
        string value = (path ?? string.Empty).Trim().Replace('/', '\\');
        if (value.Length == 0)
        {
            return OperationResult<string>.Fail("invalid_path", "A task path is required.");
        }

        if (!value.StartsWith('\\'))
        {
            value = "\\" + value;
        }

        if (value.Length > 1)
        {
            value = value.TrimEnd('\\');
        }

        if (value.Contains("..", StringComparison.Ordinal) || value.Contains("\\\\", StringComparison.Ordinal)
            || value.IndexOfAny(['\'', '"', '*', '?', '<', '>', '|', ':', '\0']) >= 0)
        {
            return OperationResult<string>.Fail("invalid_path", "The task path contains invalid characters or segments.");
        }

        if (value == "\\" && !allowRoot)
        {
            return OperationResult<string>.Fail("invalid_path", "A task path is required, not the root folder.");
        }

        return OperationResult<string>.Ok(value);
    }

    private static OperationResult<T> Run<T>(Func<OperationResult<T>> action)
    {
        try
        {
            return action();
        }
        catch (ScheduledTaskException ex)
        {
            return OperationResult<T>.Fail(ex.Code, ex.Message);
        }
    }

    private static OperationResult<T> NotFound<T>(string path) =>
        OperationResult<T>.Fail("not_found", $"No scheduled task '{path}'.");
}
