using System.Diagnostics.Eventing.Reader;

using Vestigo.Abstractions;

namespace Vestigo.ScheduledTasks;

/// <summary>Reads task history from the Task Scheduler Operational event log channel.</summary>
public sealed class ScheduledTaskHistoryReader
{
    public const string Channel = "Microsoft-Windows-TaskScheduler/Operational";

    public OperationResult<ScheduledTaskHistory> Read(string path, int limit)
    {
        try
        {
            using EventLogConfiguration configuration = new(Channel);
            if (!configuration.IsEnabled)
            {
                return OperationResult<ScheduledTaskHistory>.Fail(
                    "history_disabled",
                    $"Task history is disabled. Enable the '{Channel}' channel (Task Scheduler > Enable All Tasks History, or 'wevtutil sl {Channel} /e:true').");
            }

            string query = $"*[EventData[Data[@Name='TaskName']='{EscapeXPath(path)}']]";
            EventLogQuery logQuery = new(Channel, PathType.LogName, query) { ReverseDirection = true };
            using EventLogReader reader = new(logQuery);
            List<ScheduledTaskHistoryEntry> entries = [];
            bool truncated = false;
            while (true)
            {
                using EventRecord? record = reader.ReadEvent();
                if (record is null)
                {
                    break;
                }

                if (entries.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                entries.Add(new ScheduledTaskHistoryEntry(
                    record.TimeCreated is { } time ? new DateTimeOffset(time.ToUniversalTime(), TimeSpan.Zero) : null,
                    record.Id,
                    record.LevelDisplayName ?? record.Level?.ToString() ?? "Unknown",
                    record.TaskDisplayName,
                    record.ActivityId?.ToString(),
                    Property(record, 1),
                    Render(record)));
            }

            return OperationResult<ScheduledTaskHistory>.Ok(new ScheduledTaskHistory(path, truncated, entries));
        }
        catch (EventLogNotFoundException)
        {
            return OperationResult<ScheduledTaskHistory>.Fail("history_disabled", $"The '{Channel}' channel does not exist on this machine.");
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult<ScheduledTaskHistory>.Fail("access_denied", "Windows denied access to the task history log.");
        }
        catch (EventLogException ex)
        {
            return OperationResult<ScheduledTaskHistory>.Fail("event_log_error", ex.Message);
        }
    }

    /// <summary>XPath 1.0 literals have no escape, so single quotes are rejected by path validation; strip them defensively.</summary>
    internal static string EscapeXPath(string value) => value.Replace("'", string.Empty, StringComparison.Ordinal);

    private static string? Property(EventRecord record, int index)
    {
        // Result-bearing events (102, 201, 203, ...) carry the result code as the second data item when present.
        return record.Properties.Count > index ? record.Properties[index].Value?.ToString() : null;
    }

    private static string? Render(EventRecord record)
    {
        try
        {
            return record.FormatDescription();
        }
        catch (Exception ex) when (ex is EventLogException or InvalidOperationException or ArgumentException or FormatException)
        {
            return null;
        }
    }
}
