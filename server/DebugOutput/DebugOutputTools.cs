using System.ComponentModel;

using ModelContextProtocol.Server;

namespace Vestigo.DebugOutput;

/// <summary>MCP tools for capturing and reading Win32 OutputDebugString messages.</summary>
[McpServerToolType]
public sealed class DebugOutputTools(DebugOutputManager captures)
{
    [McpServerTool(Name = "start_debug_capture")]
    [Description("Starts the single DebugViewConsole capture of OutputDebugString messages. Events are spooled locally; use read_debug_output (with waitSeconds to follow live) to read them. A second start fails with capture_busy until the running capture is stopped.")]
    public Task<OperationResult<DebugCaptureStatus>> StartDebugCapture(
        [Description("Substring include filters passed to DebugViewConsole -i. Case-insensitive. Omit to capture every message.")]
        string[]? includeMessages = null,
        [Description("Substring exclude filters passed to DebugViewConsole -e. Case-insensitive.")]
        string[]? excludeMessages = null,
        [Description("Process-name include filters passed to DebugViewConsole --include-process.")]
        string[]? includeProcesses = null,
        [Description("Process-name exclude filters passed to DebugViewConsole --exclude-process.")]
        string[]? excludeProcesses = null,
        [Description("Maximum capture duration, for example 00:05:00. Capped by Vestigo:DebugOutput:MaxDuration.")]
        string? duration = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan? parsedDuration = null;
        if (!string.IsNullOrWhiteSpace(duration))
        {
            if (!TimeSpan.TryParse(duration, out TimeSpan value))
            {
                return Task.FromResult(OperationResult<DebugCaptureStatus>.Fail("invalid_request", "Duration is not a valid timespan."));
            }

            parsedDuration = value;
        }

        return captures.StartCaptureAsync(
            new StartDebugCaptureRequest(
                includeMessages ?? [],
                excludeMessages ?? [],
                includeProcesses ?? [],
                excludeProcesses ?? [],
                parsedDuration),
            cancellationToken);
    }

    [McpServerTool(Name = "list_debug_captures")]
    [Description("Lists debug-output captures owned by this Vestigo process.")]
    public IReadOnlyList<DebugCaptureStatus> ListDebugCaptures() => captures.List();

    [McpServerTool(Name = "get_debug_capture")]
    [Description("Returns status, counters, and the last error for one debug-output capture.")]
    public OperationResult<DebugCaptureStatus> GetDebugCapture(
        [Description("Capture identifier.")] string captureId) =>
        captures.Get(captureId);

    [McpServerTool(Name = "read_debug_output")]
    [Description("Reads a page of NDJSON OutputDebugString events from a capture spool. Each line is {receivedAt, time, pid, processName, message}. To follow a live capture, call repeatedly with afterSequence set to the previous nextSequence and waitSeconds > 0 (long-poll); stop when completed is true. Read-time filters skip non-matching lines and still advance nextSequence past every scanned line.")]
    public Task<OperationResult<DebugCapturePage>> ReadDebugOutput(
        [Description("Capture identifier.")] string captureId,
        [Description("Return events after this sequence. Use 0 to start at the beginning.")]
        long afterSequence = 0,
        [Description("Maximum matching events to return. The server clamps this to its configured batch size.")]
        int? limit = null,
        [Description("When no unread events are available yet, wait up to this many seconds for new ones. Clamped to the server's MaxReadWait.")]
        double waitSeconds = 0,
        [Description("Optional .NET regex (NonBacktracking, ignore-case) that the message must match.")]
        string? messageRegex = null,
        [Description("Optional .NET regex (NonBacktracking, ignore-case) that excludes a matching message.")]
        string? excludeRegex = null,
        [Description("When set, only messages from these process IDs are returned.")]
        int[]? processIds = null,
        [Description("When set, only messages whose process name matches one of these simple globs (* and ?) are returned.")]
        string[]? processNames = null,
        CancellationToken cancellationToken = default) =>
        captures.ReadAsync(
            captureId,
            afterSequence,
            limit,
            TimeSpan.FromSeconds(double.IsFinite(waitSeconds) ? Math.Clamp(waitSeconds, 0, 3600) : 0),
            new DebugCaptureReadFilter(messageRegex, excludeRegex, processIds, processNames),
            cancellationToken);

    [McpServerTool(Name = "stop_debug_capture")]
    [Description("Stops the DebugViewConsole capture. Sends Ctrl+C, then kills the process tree if it does not exit.")]
    public Task<OperationResult<DebugCaptureStatus>> StopDebugCapture(
        [Description("Capture identifier.")] string captureId) =>
        captures.StopAsync(captureId);
}
