using System.Diagnostics.Eventing.Reader;
using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.EventLogs;

public sealed record EventLogSummary(string Name, long? RecordCount, bool Enabled, string? LogType);

public sealed record EventEntry(
    int Id,
    string? Provider,
    string Level,
    DateTimeOffset? Time,
    long? RecordId,
    string? Machine,
    int? ProcessId,
    string? Message,
    string? Xml);

public sealed record EventQueryResult(
    string Query,
    IReadOnlyList<EventEntry> Events,
    bool Truncated,
    bool ScanLimitReached);

/// <summary>Enumerates Windows event logs and runs bounded queries against them.</summary>
public sealed class EventLogService(VestigoOptions options, RootPolicy roots)
{
    public OperationResult<IReadOnlyList<EventLogSummary>> List(string? nameContains, bool includeCounts)
    {
        try
        {
            using EventLogSession session = new();
            List<EventLogSummary> logs = [];
            foreach (string name in session.GetLogNames().Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(nameContains) && !name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool enabled = false;
                string? type = null;
                long? count = null;
                try
                {
                    using EventLogConfiguration configuration = new(name, session);
                    enabled = configuration.IsEnabled;
                    type = configuration.LogType.ToString();
                    if (includeCounts)
                    {
                        count = session.GetLogInformation(name, PathType.LogName).RecordCount;
                    }
                }
                catch (Exception exception) when (exception is EventLogException or UnauthorizedAccessException)
                {
                    // Some channels deny configuration or statistics; list them without details.
                }

                logs.Add(new EventLogSummary(name, count, enabled, type));
            }

            return OperationResult<IReadOnlyList<EventLogSummary>>.Ok(logs);
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult<IReadOnlyList<EventLogSummary>>.Fail("access_denied", "Windows denied access to the event log service.");
        }
        catch (EventLogException exception)
        {
            return OperationResult<IReadOnlyList<EventLogSummary>>.Fail("event_log_error", exception.Message);
        }
    }

    public OperationResult<EventQueryResult> Query(
        string channel,
        EventLogFilter filter,
        string? xpath,
        string? messageContains,
        int? maxEvents,
        bool newestFirst,
        bool includeXml,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            return OperationResult<EventQueryResult>.Fail("invalid_channel", "Channel or .evtx path is required.");
        }

        bool hasFilters = filter.Providers is { Count: > 0 } || filter.Levels is { Count: > 0 } || filter.EventIds is { Count: > 0 }
                          || filter.Since is not null || filter.Until is not null;
        if (!string.IsNullOrWhiteSpace(xpath) && hasFilters)
        {
            return OperationResult<EventQueryResult>.Fail("invalid_query", "Use either xpath or the structured filters, not both.");
        }

        string query;
        try
        {
            query = string.IsNullOrWhiteSpace(xpath) ? EventLogQueryBuilder.Build(filter) : xpath.Trim();
        }
        catch (ArgumentException exception)
        {
            return OperationResult<EventQueryResult>.Fail("invalid_query", exception.Message);
        }

        string target = channel.Trim();
        PathType pathType = PathType.LogName;
        if (target.EndsWith(".evtx", StringComparison.OrdinalIgnoreCase))
        {
            PathCheck check = roots.ResolveExisting(target, RootAccess.Read);
            if (!check.Success)
            {
                return OperationResult<EventQueryResult>.Fail(check.ErrorCode!, check.Error!);
            }

            target = check.FullPath!;
            pathType = PathType.FilePath;
        }

        int limit = Math.Clamp(maxEvents ?? options.EventLog.MaxEvents, 1, options.EventLog.MaxEvents);
        bool textFilter = !string.IsNullOrEmpty(messageContains);
        List<EventEntry> events = [];
        int scanned = 0;
        bool truncated = false;
        bool scanLimit = false;

        try
        {
            EventLogQuery logQuery = new(target, pathType, query) { ReverseDirection = newestFirst };
            using EventLogReader reader = new(logQuery);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using EventRecord? record = reader.ReadEvent();
                if (record is null)
                {
                    break;
                }

                if (++scanned > options.EventLog.MaxScannedEvents)
                {
                    scanLimit = true;
                    break;
                }

                string? message = Render(record);
                if (textFilter && (message is null || !message.Contains(messageContains!, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (events.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                events.Add(new EventEntry(
                    record.Id,
                    record.ProviderName,
                    EventLogQueryBuilder.LevelName(record.Level),
                    record.TimeCreated is { } time ? new DateTimeOffset(time.ToUniversalTime(), TimeSpan.Zero) : null,
                    record.RecordId,
                    record.MachineName,
                    record.ProcessId,
                    message,
                    includeXml ? SafeXml(record) : null));
            }
        }
        catch (EventLogNotFoundException)
        {
            return OperationResult<EventQueryResult>.Fail("not_found", "Event log channel or file was not found.");
        }
        catch (EventLogInvalidDataException exception)
        {
            return OperationResult<EventQueryResult>.Fail("invalid_query", exception.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult<EventQueryResult>.Fail("access_denied", "Windows denied access to the event log.");
        }
        catch (EventLogException exception)
        {
            return OperationResult<EventQueryResult>.Fail("event_log_error", exception.Message);
        }

        return OperationResult<EventQueryResult>.Ok(new EventQueryResult(query, events, truncated, scanLimit));
    }

    /// <summary>Renders the message text; publishers that are not installed yield null instead of failing the query.</summary>
    private static string? Render(EventRecord record)
    {
        try
        {
            return record.FormatDescription();
        }
        catch (Exception exception) when (exception is EventLogException or InvalidOperationException or ArgumentException
                                              or FormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? SafeXml(EventRecord record)
    {
        try
        {
            return record.ToXml();
        }
        catch (EventLogException)
        {
            return null;
        }
    }
}
