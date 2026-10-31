using System.ComponentModel;
using System.Globalization;

using ModelContextProtocol.Server;

namespace Vestigo.EventLogs;

/// <summary>MCP tools for listing and querying Windows event logs.</summary>
[McpServerToolType]
public sealed class EventLogTools(EventLogService service)
{
    [McpServerTool(Name = "list_event_logs")]
    [Description("Lists Windows event log channels with record count and enabled state. Channels that deny access are listed without details.")]
    public OperationResult<IReadOnlyList<EventLogSummary>> ListEventLogs(
        [Description("Only channels whose name contains this text (case-insensitive).")] string? nameContains = null,
        [Description("Include record counts. Disable for a faster listing.")] bool includeCounts = true) =>
        service.List(nameContains, includeCounts);

    [McpServerTool(Name = "query_event_log")]
    [Description("Queries a Windows event log channel or an .evtx file under a root with Read access. Use the structured filters or a raw XPath, not both. Results are capped by Vestigo:EventLog:MaxEvents. Messages come from the installed publisher and are null when it is missing.")]
    public OperationResult<EventQueryResult> QueryEventLog(
        [Description("Channel name such as Application or System, or a path ending in .evtx.")] string channel,
        [Description("Provider names to match.")] string[]? providers = null,
        [Description("Levels to match: critical, error, warning, information, verbose.")] string[]? levels = null,
        [Description("Event IDs to match.")] int[]? eventIds = null,
        [Description("Only events at or after this ISO 8601 timestamp.")] string? since = null,
        [Description("Only events at or before this ISO 8601 timestamp.")] string? until = null,
        [Description("Only events whose rendered message contains this text (case-insensitive).")] string? messageContains = null,
        [Description("Maximum events to return. Clamped to MaxEvents.")] int? maxEvents = null,
        [Description("Return the newest events first.")] bool newestFirst = true,
        [Description("Raw Event Log XPath, for example *[System[EventID=1000]]. Cannot be combined with the structured filters.")] string? xpath = null,
        [Description("Include the event XML.")] bool includeXml = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryParse(since, out DateTimeOffset? from) || !TryParse(until, out DateTimeOffset? to))
        {
            return OperationResult<EventQueryResult>.Fail("invalid_time", "since and until must be ISO 8601 timestamps.");
        }

        return service.Query(
            channel,
            new EventLogFilter(providers, levels, eventIds, from, to),
            xpath,
            messageContains,
            maxEvents,
            newestFirst,
            includeXml,
            cancellationToken);
    }

    private static bool TryParse(string? text, out DateTimeOffset? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }
}
