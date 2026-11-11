using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Processes;

namespace Vestigo.Desktop;

/// <summary>Detailed read of one Win32 window. Listing stays on <c>list_windows</c>.</summary>
[McpServerToolType]
public sealed class WindowInspectionTools(DesktopInventory inventory, IManagedDesktopDirectory? desktops = null)
{
    [McpServerTool(Name = "inspect_window", ReadOnly = true)]
    [Description(
        "Reads one window in physical virtual-screen pixels. Returns the raw GetWindowRect, the DWM visible frame, "
        + "and the client rectangle in screen coordinates, plus DPI, frame metrics, styles, visibility, owner relationships, "
        + "display intersections, and process integrity, elevation, and LocalSystem state when those reads succeed. "
        + "Each field has its own success or error. A failed read is not reported as a zero rectangle. Geometry is omitted "
        + "when per-monitor DPI awareness v2 cannot be entered. expectedPid and expectedStartTime reject a reused handle or pid. "
        + "context 'interactive' (default) reads the console user's desktop. desktopSessionId reads that managed desktop without activating it. "
        + "Inspection of an inactive desktop is best-effort. This does not change the window.")]
    public Task<OperationResult<WindowInspectionReport>> InspectWindow(
        [Description("Hex window handle from list_windows (0x...).")] string windowHandle,
        [Description("Process id from list_windows. A different owner fails with window_identity_mismatch.")] int? expectedPid = null,
        [Description("Process start time from list_processes. A different process fails with process_identity_mismatch.")] DateTimeOffset? expectedStartTime = null,
        [Description("'interactive' (default) or 'service'.")] string context = "interactive",
        [Description("Interactive only: use the elevated token.")] bool elevated = false,
        [Description("Managed desktop session id. Omit to use winsta0\\default. Does not activate the desktop.")] string? desktopSessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (!ProcessTools.TryParseContext(context, out LaunchContext launchContext))
        {
            return Task.FromResult(OperationResult<WindowInspectionReport>.Fail("invalid_request", "context must be 'service' or 'interactive'."));
        }

        if (!string.IsNullOrWhiteSpace(desktopSessionId) && launchContext != LaunchContext.Interactive)
        {
            return Task.FromResult(OperationResult<WindowInspectionReport>.Fail("invalid_request", "desktopSessionId requires context 'interactive'."));
        }

        return InspectTargeted(windowHandle, expectedPid, expectedStartTime, launchContext, elevated, desktopSessionId, cancellationToken);
    }

    private async Task<OperationResult<WindowInspectionReport>> InspectTargeted(
        string windowHandle,
        int? expectedPid,
        DateTimeOffset? expectedStartTime,
        LaunchContext launchContext,
        bool elevated,
        string? desktopSessionId,
        CancellationToken cancellationToken)
    {
        OperationResult<DesktopHelperTarget?> targeted = await DesktopHelperTargeting.ResolveAsync(desktops, desktopSessionId, requireInput: false, cancellationToken).ConfigureAwait(false);
        return targeted.Success
            ? await inventory.InspectWindowAsync(windowHandle, expectedPid, expectedStartTime, launchContext, elevated, cancellationToken, targeted.Value).ConfigureAwait(false)
            : targeted.FailAs<WindowInspectionReport>();
    }
}
