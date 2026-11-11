using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Processes;

namespace Vestigo.Desktop;

/// <summary>Bounded, observational window-event capture.</summary>
[McpServerToolType]
public sealed class WindowEventTools(WindowEventCaptureManager captures, VestigoOptions options, IManagedDesktopDirectory? desktops = null)
{
    [McpServerTool(Name = "start_window_event_capture")]
    [Description(
        "Captures window creation, destruction, show, hide, foreground, and location changes, plus exit of the selected process. "
        + "A process filter includes windows that process creates later and does not include child processes. A window handle without a process filter "
        + "follows only that window. Events are observed; geometry and DPI are sampled when an event arrives. DPI changes are comparisons of samples, not WM_DPICHANGED. "
        + "Process exit has no inferred cause. WinEvent does not report how many events it dropped before delivery; dropped counts only the helper queue. "
        + "The capture keeps running after this call returns. Read it with read_window_event_capture and stop it with stop_window_event_capture. "
        + "A stateless client disconnect does not stop it. Service shutdown does. desktopSessionId follows windows on that managed desktop without activating it. Interactive session only.")]
    public Task<OperationResult<WindowEventCaptureStatus>> StartWindowEventCapture(
        [Description("Hex window handle. Omit to follow a process.")] string? windowHandle = null,
        [Description("Process id. Later windows from this process are included; child processes are not.")] int? pid = null,
        [Description("Start time from list_processes. Required to prove the pid was not reused when it can be read.")] DateTimeOffset? expectedStartTime = null,
        [Description("Seconds to capture, from 1 to Vestigo:Desktop:MaxWindowEventDuration.")] int durationSeconds = 30,
        [Description("Use the elevated token for the helper.")] bool elevated = false,
        [Description("Managed desktop session id. Events are observed on that desktop without activating it.")] string? desktopSessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds <= 0)
        {
            return Task.FromResult(OperationResult<WindowEventCaptureStatus>.Fail("invalid_request", "durationSeconds must be positive."));
        }

        TimeSpan duration = TimeSpan.FromSeconds(durationSeconds);
        if (duration > options.Desktop.MaxWindowEventDuration)
        {
            duration = options.Desktop.MaxWindowEventDuration;
        }

        return StartTargeted(windowHandle, pid, expectedStartTime, duration, elevated, desktopSessionId, cancellationToken);
    }

    private async Task<OperationResult<WindowEventCaptureStatus>> StartTargeted(
        string? windowHandle,
        int? pid,
        DateTimeOffset? expectedStartTime,
        TimeSpan duration,
        bool elevated,
        string? desktopSessionId,
        CancellationToken cancellationToken)
    {
        OperationResult<DesktopHelperTarget?> targeted = await DesktopHelperTargeting.ResolveAsync(desktops, desktopSessionId, requireInput: false, cancellationToken).ConfigureAwait(false);
        if (!targeted.Success)
        {
            return targeted.FailAs<WindowEventCaptureStatus>();
        }

        WindowEventStart start = new(
            string.IsNullOrWhiteSpace(windowHandle) ? null : windowHandle.Trim(),
            pid,
            expectedStartTime,
            duration,
            options.Desktop.MaxWindowEvents,
            elevated,
            targeted.Value?.DesktopPath);
        return await captures.StartAsync(start, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "read_window_event_capture", ReadOnly = true)]
    [Description(
        "Reads window-event records after afterSequence. waitSeconds long-polls for new records. "
        + "Completed is true when no further records will arrive. Dropped is the helper queue count. "
        + "Native WinEvent loss is not reported because Windows does not provide that count.")]
    public Task<OperationResult<WindowEventPage>> ReadWindowEventCapture(
        [Description("Capture id from start_window_event_capture.")] string id,
        [Description("Return records with a sequence greater than this value.")] long afterSequence = 0,
        [Description("Maximum records, clamped to Vestigo:MaxReadBatch.")] int? limit = null,
        [Description("Seconds to wait for new records, up to Vestigo:MaxReadWait.")] double waitSeconds = 0,
        CancellationToken cancellationToken = default) =>
        captures.ReadAsync(id, afterSequence, limit, TimeSpan.FromSeconds(Math.Max(0, waitSeconds)), cancellationToken);

    [McpServerTool(Name = "get_window_event_capture", ReadOnly = true)]
    [Description("Returns the state, counts, dropped events, truncation, and whether new windows or child processes are included.")]
    public OperationResult<WindowEventCaptureStatus> GetWindowEventCapture(
        [Description("Capture id.")] string id) => captures.Get(id);

    [McpServerTool(Name = "list_window_event_captures", ReadOnly = true)]
    [Description("Lists window-event captures started by this service process.")]
    public IReadOnlyList<WindowEventCaptureStatus> ListWindowEventCaptures() => captures.List();

    [McpServerTool(Name = "stop_window_event_capture")]
    [Description("Stops a window-event capture and unhooks its helper. Already-stopped captures return their final state.")]
    public Task<OperationResult<WindowEventCaptureStatus>> StopWindowEventCapture(
        [Description("Capture id.")] string id,
        CancellationToken cancellationToken = default) => captures.StopAsync(id);
}
