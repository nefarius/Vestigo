using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Processes;

namespace Vestigo.Desktop;

/// <summary>Places a window by its client rectangle and restores a saved placement.</summary>
[McpServerToolType]
public sealed class WindowPlacementTools(
    WindowPlacer placer,
    WindowPlacementStore store,
    SessionHelperClient helper,
    IProcessLauncher launcher,
    VestigoOptions options)
{
    [McpServerTool(Name = "set_window_client_rect", Destructive = true)]
    [Description(
        "Moves and sizes a window so its client area matches x,y,width,height in physical virtual-screen pixels. "
        + "The call accounts for the window's current decorations and DPI, then measures the result. Achieved is true only when the measured client rectangle matches. "
        + "Windows or the application may alter the request; that is reported instead of being treated as success. "
        + "Activation and Z-order are preserved by SetWindowPos flags. savePlacement stores the previous placement for restore_window_placement. "
        + "The token does not survive a service restart. Requires Vestigo:Desktop:AllowWindowPlacement. "
        + "context service changes a window in the service session; interactive changes the console user's window and needs an elevated helper for an elevated window.")]
    public async Task<OperationResult<WindowPlacementResult>> SetWindowClientRect(
        [Description("Hex window handle from list_windows.")] string windowHandle,
        [Description("Client left in physical screen pixels.")] int x,
        [Description("Client top in physical screen pixels.")] int y,
        [Description("Client width in physical screen pixels.")] int width,
        [Description("Client height in physical screen pixels.")] int height,
        [Description("Must be physical-screen-pixels.")] string coordinateSpace = DesktopCoordinates.PhysicalScreenPixels,
        [Description("Owner pid. A different owner fails before the window is moved.")] int? expectedPid = null,
        [Description("Process start time. A different process fails before the window is moved.")] DateTimeOffset? expectedStartTime = null,
        [Description("Save the current placement and return a restore token.")] bool savePlacement = false,
        [Description("'interactive' (default) or 'service'.")] string context = "interactive",
        [Description("Interactive only: use the elevated token.")] bool elevated = false,
        CancellationToken cancellationToken = default)
    {
        if (!options.Desktop.AllowWindowPlacement)
        {
            return OperationResult<WindowPlacementResult>.Fail(
                "window_placement_disabled",
                "Window placement is disabled. Set Vestigo:Desktop:AllowWindowPlacement to true.");
        }

        if (!ProcessTools.TryParseContext(context, out LaunchContext launchContext))
        {
            return OperationResult<WindowPlacementResult>.Fail("invalid_request", "context must be 'service' or 'interactive'.");
        }

        WindowPlacementRequest request = new(windowHandle, expectedPid, expectedStartTime, x, y, width, height, coordinateSpace, savePlacement);
        if (launchContext == LaunchContext.Service)
        {
            if (!OperatingSystem.IsWindows())
            {
                return OperationResult<WindowPlacementResult>.Fail("windows_required", "Window placement requires Windows.");
            }

            return await Task.Run(() => placer.Place(request), cancellationToken).ConfigureAwait(false);
        }

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated);
        if (!available.Success)
        {
            return available.FailAs<WindowPlacementResult>();
        }

        OperationResult<WindowPlacementResult> placed = await helper.RunAsync<WindowPlacementResult>(
            SessionHelper.BuildPlaceWindowArguments(request),
            elevated,
            TimeSpan.FromSeconds(30),
            1024 * 1024,
            cancellationToken).ConfigureAwait(false);
        if (!placed.Success || placed.Value?.CapturedPlacement is null)
        {
            return placed.Success
                ? OperationResult<WindowPlacementResult>.Ok(placed.Value!)
                : placed;
        }

        OperationResult<string> token = store.Save(placed.Value.CapturedPlacement);
        if (!token.Success)
        {
            return token.FailAs<WindowPlacementResult>();
        }

        return OperationResult<WindowPlacementResult>.Ok(placed.Value with { SavedPlacementToken = token.Value, CapturedPlacement = null });
    }

    [McpServerTool(Name = "restore_window_placement", Destructive = true)]
    [Description(
        "Restores a placement saved by set_window_client_rect. The window and process identity are checked again. "
        + "SetWindowPlacement can let Windows activate the window; Vestigo does not call SetForegroundWindow. "
        + "Achieved is true only when the measured normal rectangle matches the saved one. Requires Vestigo:Desktop:AllowWindowPlacement.")]
    public async Task<OperationResult<WindowPlacementResult>> RestoreWindowPlacement(
        [Description("Token returned by set_window_client_rect.")] string token,
        [Description("'interactive' (default) or 'service'.")] string context = "interactive",
        [Description("Interactive only: use the elevated token.")] bool elevated = false,
        CancellationToken cancellationToken = default)
    {
        if (!options.Desktop.AllowWindowPlacement)
        {
            return OperationResult<WindowPlacementResult>.Fail(
                "window_placement_disabled",
                "Window placement is disabled. Set Vestigo:Desktop:AllowWindowPlacement to true.");
        }

        if (!ProcessTools.TryParseContext(context, out LaunchContext launchContext))
        {
            return OperationResult<WindowPlacementResult>.Fail("invalid_request", "context must be 'service' or 'interactive'.");
        }

        if (launchContext == LaunchContext.Service)
        {
            return await Task.Run(() => placer.Restore(token), cancellationToken).ConfigureAwait(false);
        }

        OperationResult<SavedWindowPlacement> saved = store.Peek(token);
        if (!saved.Success)
        {
            return saved.FailAs<WindowPlacementResult>();
        }

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated);
        if (!available.Success)
        {
            return available.FailAs<WindowPlacementResult>();
        }

        OperationResult<WindowPlacementResult> restored = await helper.RunAsync<WindowPlacementResult>(
            SessionHelper.BuildRestoreWindowArguments(saved.Value!),
            elevated,
            TimeSpan.FromSeconds(30),
            1024 * 1024,
            cancellationToken).ConfigureAwait(false);
        if (restored.Success)
        {
            store.Remove(token);
        }

        return restored;
    }
}
