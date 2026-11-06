using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.EventLogs;
using Vestigo.LogFiles;
using Vestigo.Security;

namespace Vestigo.WindowsServicing;

public sealed record ServicingLog(string Source, OperationResult<TextSlice> Excerpt);
public sealed record UpdateEventEvidence(string Channel, OperationResult<EventQueryResult> Result);
public sealed record UpdateFailureDetails(OperationResult<BoundedList<UpdateHistoryEntry>> History,
    IReadOnlyList<UpdateEventEvidence> Events, IReadOnlyList<ServicingLog> Logs,
    IReadOnlyList<string> Observations, IReadOnlyList<string> Hypotheses, IReadOnlyList<string> Recommendations);
public sealed record UpdateStorageStatus(long? TotalBytes, long? AvailableBytes,
    IReadOnlyList<ServicingFile> Files, string Limitation);

[McpServerToolType]
public sealed class UpdateDiagnosticsTools(VestigoOptions options, RootPolicy roots,
    IWindowsUpdateAgent agent, NativeDiagnosticWorker worker)
{
    private static readonly string[] Channels = ["Microsoft-Windows-WindowsUpdateClient/Operational",
        "Microsoft-Windows-Servicing/Operational", "Microsoft-Windows-Bits-Client/Operational",
        "Microsoft-Windows-DeliveryOptimization/Operational"];

    [McpServerTool(Name = "get_servicing_logs", ReadOnly = true, Destructive = false)]
    [Description("Returns bounded tails of the fixed CBS.log and DISM dism.log, using existing Read roots and text-tail decoding. Each source has its own error/truncation. Does not decompress rotated CBS archives or convert Windows Update ETLs. Raw logs may contain sensitive machine/user data. Optional literal filtering applies only within the tail window, not the whole file.")]
    public OperationResult<IReadOnlyList<ServicingLog>> GetServicingLogs(
        [Description("Literal case-insensitive text, KB, update id or HRESULT; at most 128 characters.")] string? contains = null)
    {
        if (contains?.Length > 128) return OperationResult<IReadOnlyList<ServicingLog>>.Fail("invalid_request", "contains is limited to 128 characters.");
        return OperationResult<IReadOnlyList<ServicingLog>>.Ok(ReadLogs(contains));
    }

    private IReadOnlyList<ServicingLog> ReadLogs(string? contains)
    {
        LogTools logs = new(options, roots);
        List<ServicingLog> result = [];
        foreach ((string source, string relative) in new[] { ("CBS", @"Logs\CBS\CBS.log"), ("DISM", @"Logs\DISM\dism.log") })
        {
            OperationResult<TextSlice> excerpt = logs.TailTextFile(Path.Combine(WindowsDirectory, relative),
                options.WindowsServicing.MaxLogLines, options.WindowsServicing.MaxLogBytes);
            if (excerpt.Success && !string.IsNullOrEmpty(contains))
                excerpt = OperationResult<TextSlice>.Ok(excerpt.Value! with
                { Lines = excerpt.Value.Lines.Where(line => line.Contains(contains, StringComparison.OrdinalIgnoreCase)).ToArray() });
            result.Add(new(source, excerpt));
        }
        return result;
    }

    [McpServerTool(Name = "get_update_failure_details", ReadOnly = true, Destructive = false)]
    [Description("Collects evidence from a bounded newest WUA history window, four update/servicing/download event channels, and root-confined CBS/DISM log tails. since defaults to 24 hours ago; event/history timestamps are filtered, log tails are not time-parsed. contains is a literal KB/id/HRESULT/text filter. Source failures remain explicit. Empty results do not prove absence; no causal diagnosis is inferred from matching text.")]
    public async Task<OperationResult<UpdateFailureDetails>> GetUpdateFailureDetails(
        [Description("Literal KB, update id, HRESULT or text, at most 128 characters.")] string? contains = null,
        [Description("Earliest event/history time; defaults to 24 hours ago. Log lines retain raw timestamps.")] DateTimeOffset? since = null,
        [Description("Maximum entries examined/returned per evidence source, clamped to WindowsServicing:MaxResults.")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (contains?.Length > 128) return OperationResult<UpdateFailureDetails>.Fail("invalid_request", "contains is limited to 128 characters.");
        int bounded = Math.Clamp(limit, 1, options.WindowsServicing.MaxResults);
        DateTimeOffset cutoff = since ?? DateTimeOffset.UtcNow.AddHours(-24);
        OperationResult<BoundedList<UpdateHistoryEntry>> history = await worker.Run(() => agent.History(0, bounded, true),
            options.WindowsServicing.NativeCallTimeout, cancellationToken);
        if (history.Success)
        {
            BoundedList<UpdateHistoryEntry> original = history.Value!;
            history = OperationResult<BoundedList<UpdateHistoryEntry>>.Ok(original with
            { Items = original.Items.Where(row => new DateTimeOffset(DateTime.SpecifyKind(row.Date, DateTimeKind.Utc)) >= cutoff &&
                (string.IsNullOrEmpty(contains) || row.Title.Contains(contains, StringComparison.OrdinalIgnoreCase) ||
                 row.Id.Contains(contains, StringComparison.OrdinalIgnoreCase) || row.HResult.Contains(contains, StringComparison.OrdinalIgnoreCase))).ToArray() });
        }
        EventLogService events = new(options, roots);
        List<UpdateEventEvidence> evidence = [];
        foreach (string channel in Channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OperationResult<EventQueryResult> query = events.Query(channel, new EventLogFilter(Levels: ["critical", "error", "warning"], Since: cutoff),
                null, contains, bounded, true, false, cancellationToken);
            if (query.Success)
                query = OperationResult<EventQueryResult>.Ok(query.Value! with
                { Events = query.Value.Events.Select(row => row with { Message = row.Message is { Length: > 4000 } text ? text[..4000] + " [truncated]" : row.Message }).ToArray() });
            evidence.Add(new(channel, query));
        }
        return OperationResult<UpdateFailureDetails>.Ok(new(history, evidence, ReadLogs(contains),
            ["History is limited to the newest examined window; events have independent scan/result caps; logs are bounded tails.",
             "Matching entries are evidence, not proof of causation. Unavailable sources and truncation must be checked."], [],
            ["Compare timestamps, update identities, raw HRESULTs and event record IDs before choosing a repair action."]));
    }

    [McpServerTool(Name = "get_update_storage_status", ReadOnly = true, Destructive = false)]
    [Description("Reports available space on the Windows volume and metadata for fixed update database/log/pending files. Does not recurse through caches, calculate WinSxS directory size, determine reserved storage, or perform cleanup. Free space is an observation, not a prediction of update space requirements.")]
    public OperationResult<UpdateStorageStatus> GetUpdateStorageStatus()
    {
        long? total = null, free = null;
        try { DriveInfo drive = new(Path.GetPathRoot(WindowsDirectory)!); total = drive.TotalSize; free = drive.AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Unknown volume fields. */ }
        string[] relativeFiles = [@"SoftwareDistribution\DataStore\DataStore.edb", @"Logs\CBS\CBS.log", @"Logs\DISM\dism.log", @"WinSxS\pending.xml"];
        return OperationResult<UpdateStorageStatus>.Ok(new(total, free,
            relativeFiles.Select(path => WindowsServicingTools.Metadata(path, Path.Combine(WindowsDirectory, path))).ToArray(),
            "File lengths are logical lengths, not physical allocation. Cache totals, WinSxS allocation and reserved storage are not measured."));
    }

    private static string WindowsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
}
