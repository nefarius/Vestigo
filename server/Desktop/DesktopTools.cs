using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Processes;

namespace Vestigo.Desktop;

/// <summary>MCP tools that list Win32 windows and the displays they are rendered on.</summary>
[McpServerToolType]
public sealed class DesktopTools(DesktopInventory inventory)
{
    [McpServerTool(Name = "list_windows")]
    [Description(
        "Lists Win32 windows on the chosen desktop with owning process, parent and owner, physical-pixel bounds, "
        + "and the GDI display they are rendered on. Filters combine: pid or processName, narrowed by titleRegex and "
        + "className. context 'interactive' (default) enumerates the console user's desktop through the session helper.")]
    public Task<OperationResult<WindowListResult>> ListWindows(
        [Description("Process id.")] int? pid = null,
        [Description("Process name, with or without .exe.")] string? processName = null,
        [Description(".NET regular expression matched against window titles (case-insensitive).")] string? titleRegex = null,
        [Description("Window class name (case-insensitive).")] string? className = null,
        [Description("Include hidden windows. Cloaked (virtual-desktop) windows are still listed when visible.")] bool includeHidden = false,
        [Description("Also enumerate child windows of each top-level window. Child titles are omitted.")] bool includeChildren = false,
        [Description("Maximum windows to return, 1 to 5000.")] int limit = WindowSelector.DefaultLimit,
        [Description("'interactive' (default) or 'service'.")] string context = "interactive",
        [Description("Interactive only: use the elevated token when enumerating as the console user.")] bool elevated = false,
        CancellationToken cancellationToken = default)
    {
        if (!ProcessTools.TryParseContext(context, out LaunchContext launchContext))
        {
            return Task.FromResult(OperationResult<WindowListResult>.Fail("invalid_request", "context must be 'service' or 'interactive'."));
        }

        WindowSelector selector = new(
            pid is { } id ? [id] : [],
            string.IsNullOrWhiteSpace(processName) ? [] : [processName.Trim()],
            string.IsNullOrWhiteSpace(titleRegex) ? null : titleRegex,
            string.IsNullOrWhiteSpace(className) ? null : className.Trim(),
            includeHidden,
            includeChildren,
            Math.Clamp(limit, 1, WindowSelector.MaxLimit));
        return inventory.ListWindowsAsync(selector, launchContext, elevated, cancellationToken);
    }

    [McpServerTool(Name = "list_displays")]
    [Description(
        "Lists GDI displays (\\\\.\\DISPLAYn) with bounds, work area, DPI, and the physical monitors currently "
        + "driven by each source, including cloned outputs. context 'interactive' (default) uses the console user's session.")]
    public Task<OperationResult<DisplayListResult>> ListDisplays(
        [Description("'interactive' (default) or 'service'.")] string context = "interactive",
        CancellationToken cancellationToken = default)
    {
        if (!ProcessTools.TryParseContext(context, out LaunchContext launchContext))
        {
            return Task.FromResult(OperationResult<DisplayListResult>.Fail("invalid_request", "context must be 'service' or 'interactive'."));
        }

        return inventory.ListDisplaysAsync(launchContext, elevated: false, cancellationToken);
    }
}
