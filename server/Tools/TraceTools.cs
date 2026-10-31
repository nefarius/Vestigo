using System.ComponentModel;
using System.Globalization;

using ModelContextProtocol.Server;

using Vestigo.Etws;

namespace Vestigo.Tools;

/// <summary>MCP tools for offline WPP conversion and managed realtime capture.</summary>
[McpServerToolType]
public sealed class TraceTools(SymbolCatalogService symbols, OfflineConversionService offline, CaptureManager captures, IRealtimeTraceSessionFactory sessions)
{
    [McpServerTool(Name = "inspect_wpp_pdb")]
    [Description("Reads WPP control GUIDs, names, and flag names from a PDB inside an allowed input root.")]
    public OperationResult<PdbInspection> InspectWppPdb(
        [Description("Absolute or rooted path to a .pdb file.")] string pdbPath) =>
        symbols.InspectPdb(pdbPath);

    [McpServerTool(Name = "validate_decoding_sources")]
    [Description("Loads PDB and TMF files and reports whether they can decode WPP messages.")]
    public OperationResult<SymbolLoadDescription> ValidateDecodingSources(
        [Description("PDB and TMF paths inside an allowed input root.")] string[] symbolPaths) =>
        symbols.ValidateSources(symbolPaths ?? []);

    [McpServerTool(Name = "scan_etl_metadata")]
    [Description("Lists distinct PDB identities referenced by one or more ETL files.")]
    public OperationResult<IReadOnlyList<PdbReferenceDescription>> ScanEtlMetadata(
        [Description("ETL paths inside an allowed input root.")] string[] etlPaths) =>
        symbols.ScanEtl(etlPaths ?? []);

    [McpServerTool(Name = "convert_etl")]
    [Description("Decodes ETL files to a JSON or NDJSON file inside an allowed output root and returns a summary.")]
    public Task<OperationResult<ConversionSummary>> ConvertEtl(
        [Description("ETL paths inside an allowed input root.")] string[] etlPaths,
        [Description("Output .json or .ndjson path inside an allowed output root.")] string outputPath,
        [Description("json or ndjson.")] string format = "ndjson",
        [Description("Optional explicit PDB and TMF paths.")] string[]? symbolPaths = null,
        [Description("Resolve PDB references through the configured symbol cache and server.")] bool resolveSymbols = false,
        [Description("Replace an existing output file.")] bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        offline.ConvertAsync(etlPaths ?? [], outputPath, format, symbolPaths ?? [], resolveSymbols, overwrite, cancellationToken);

    [McpServerTool(Name = "list_etw_sessions")]
    [Description("Lists ETW session names currently running on this machine.")]
    public OperationResult<IReadOnlyList<string>> ListEtwSessions()
    {
        try
        {
            return OperationResult<IReadOnlyList<string>>.Ok(sessions.ListSessions());
        }
        catch (PlatformNotSupportedException ex)
        {
            return OperationResult<IReadOnlyList<string>>.Fail("windows_required", ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OperationResult<IReadOnlyList<string>>.Fail("session_query_failed", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [McpServerTool(Name = "start_capture")]
    [Description("Starts a managed realtime WPP capture. Events are spooled locally; use read_capture (with waitSeconds to follow live) to read them.")]
    public Task<OperationResult<CaptureStatus>> StartCapture(
        [Description("PDB paths whose WPP control GUIDs should be enabled.")] string[]? pdbPaths = null,
        [Description("TMF paths used only for message decoding.")] string[]? tmfPaths = null,
        [Description("Additional ETW provider GUIDs.")] string[]? providerIds = null,
        [Description("Trace level: None, Critical, Error, Warning, Information, or Verbose.")] string level = "Verbose",
        [Description("Match-any keyword bitmask. Omit for all keywords.")] string? matchAnyKeyword = null,
        [Description("Match-all keyword bitmask. Omit for no match-all filter.")] string? matchAllKeyword = null,
        [Description("Maximum capture duration, for example 00:05:00. Capped by server configuration.")] string? duration = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseGuids(providerIds, out List<Guid> providers, out string? guidError))
        {
            return Task.FromResult(OperationResult<CaptureStatus>.Fail("invalid_request", guidError!));
        }

        if (!TryParseKeyword(matchAnyKeyword, ulong.MaxValue, out ulong anyKeyword, out string? anyError))
        {
            return Task.FromResult(OperationResult<CaptureStatus>.Fail("invalid_request", anyError!));
        }

        if (!TryParseKeyword(matchAllKeyword, 0, out ulong allKeyword, out string? allError))
        {
            return Task.FromResult(OperationResult<CaptureStatus>.Fail("invalid_request", allError!));
        }

        TimeSpan? parsedDuration = null;
        if (!string.IsNullOrWhiteSpace(duration))
        {
            if (!TimeSpan.TryParse(duration, out TimeSpan value))
            {
                return Task.FromResult(OperationResult<CaptureStatus>.Fail("invalid_request", "Duration is not a valid timespan."));
            }

            parsedDuration = value;
        }

        return captures.StartAsync(
            new StartCaptureRequest(pdbPaths ?? [], tmfPaths ?? [], providers, level, anyKeyword, allKeyword, parsedDuration),
            cancellationToken);
    }

    [McpServerTool(Name = "list_captures")]
    [Description("Lists captures owned by this Vestigo process.")]
    public IReadOnlyList<CaptureStatus> ListCaptures() => captures.List();

    [McpServerTool(Name = "get_capture")]
    [Description("Returns status, counters, and the last error for one capture.")]
    public OperationResult<CaptureStatus> GetCapture([Description("Capture identifier.")] string captureId) =>
        captures.Get(captureId);

    [McpServerTool(Name = "read_capture")]
    [Description("Reads a page of decoded NDJSON events from a capture spool. To follow a live capture, call repeatedly with afterSequence set to the previous nextSequence and waitSeconds > 0 (long-poll); stop when completed is true.")]
    public Task<OperationResult<CapturePage>> ReadCapture(
        [Description("Capture identifier.")] string captureId,
        [Description("Return events after this sequence. Use 0 to start at the beginning.")] long afterSequence = 0,
        [Description("Maximum events to return. The server clamps this to its configured batch size.")] int? limit = null,
        [Description("When no events are available yet, wait up to this many seconds for new ones. Clamped to the server's MaxReadWait.")] double waitSeconds = 0,
        CancellationToken cancellationToken = default) =>
        captures.ReadAsync(
            captureId,
            afterSequence,
            limit,
            TimeSpan.FromSeconds(double.IsFinite(waitSeconds) ? Math.Clamp(waitSeconds, 0, 3600) : 0),
            cancellationToken);

    [McpServerTool(Name = "attach_etw_session")]
    [Description("Attaches read-only to an existing realtime ETW session (not Vestigo-owned) allowed by Vestigo:AttachableSessions and decodes its events live. Read with read_capture; stop_capture only detaches and never stops the session.")]
    public Task<OperationResult<CaptureStatus>> AttachEtwSession(
        [Description("Exact name of a running ETW session, as listed by list_etw_sessions.")] string sessionName,
        [Description("Optional PDB and TMF paths inside an allowed input root used to decode WPP messages.")] string[]? symbolPaths = null,
        [Description("Maximum attach duration, for example 00:05:00. Capped by server configuration.")] string? duration = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan? parsed = null;
        if (!string.IsNullOrWhiteSpace(duration))
        {
            if (!TimeSpan.TryParse(duration, out TimeSpan value))
            {
                return Task.FromResult(OperationResult<CaptureStatus>.Fail("invalid_request", "Duration is not a valid timespan."));
            }

            parsed = value;
        }

        return captures.AttachAsync(new AttachSessionRequest(sessionName, symbolPaths ?? [], parsed), cancellationToken);
    }

    [McpServerTool(Name = "stop_capture")]
    [Description("Stops a capture. For managed captures this also stops the ETW session; for attached captures it only detaches.")]
    public Task<OperationResult<CaptureStatus>> StopCapture([Description("Capture identifier.")] string captureId) =>
        captures.StopAsync(captureId);

    private static bool TryParseGuids(string[]? values, out List<Guid> guids, out string? error)
    {
        guids = [];
        error = null;
        if (values is null)
        {
            return true;
        }

        foreach (string value in values)
        {
            if (!Guid.TryParse(value, out Guid guid))
            {
                error = $"'{value}' is not a GUID.";
                return false;
            }

            guids.Add(guid);
        }

        return true;
    }

    private static bool TryParseKeyword(string? value, ulong fallback, out ulong keyword, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            keyword = fallback;
            return true;
        }

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ulong.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out keyword))
            {
                return true;
            }
        }
        else if (ulong.TryParse(value, out keyword))
        {
            return true;
        }

        error = $"'{value}' is not an unsigned keyword bitmask.";
        keyword = fallback;
        return false;
    }
}
