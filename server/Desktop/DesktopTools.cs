using System.ComponentModel;
using System.Text.Json;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Files;
using Vestigo.Processes;
using Vestigo.Security;

namespace Vestigo.Desktop;

/// <summary>MCP tools that list Win32 windows and the displays they are rendered on, and capture screenshots.</summary>
[McpServerToolType]
public sealed class DesktopTools(
    DesktopInventory inventory,
    VestigoOptions options,
    RootPolicy roots,
    FileLinkIssuer? links = null,
    ScreenshotShareStore? shares = null,
    IHttpContextAccessor? httpContext = null)
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
        + "driven by each source, including cloned outputs. includeModes adds refresh, active mode, rotation, scaling, "
        + "the adapter LUID, and advanced-color state when Windows reports them. context 'interactive' (default) uses the console user's session.")]
    public Task<OperationResult<DisplayListResult>> ListDisplays(
        [Description("'interactive' (default) or 'service'.")] string context = "interactive",
        [Description("Include refresh, active mode, rotation, scaling, adapter LUID, and advanced color.")] bool includeModes = false,
        CancellationToken cancellationToken = default)
    {
        if (!ProcessTools.TryParseContext(context, out LaunchContext launchContext))
        {
            return Task.FromResult(OperationResult<DisplayListResult>.Fail("invalid_request", "context must be 'service' or 'interactive'."));
        }

        return inventory.ListDisplaysAsync(launchContext, elevated: false, cancellationToken, includeModes);
    }

    [McpServerTool(Name = "take_screenshot")]
    [Description(
        "Captures a PNG of the console user's desktop. target 'desktop' (default) is the virtual screen across all "
        + "monitors; 'display' is one GDI display (display='primary' or \\\\.\\DISPLAYn from list_displays); "
        + "'window' is one top-level window (windowHandle from list_windows, or pid/processName/titleRegex that "
        + "must match exactly one visible window); 'region' is x,y,width,height in the same physical-pixel "
        + "coordinates list_displays reports. Returns a downscaled preview as MCP image content for the model "
        + "(maxEdge, default 1920; 0 omits it) plus JSON metadata. That image block may not be shown to the user. "
        + "share:true also returns a short-lived, single-use URL and resource link for the same preview so the user "
        + "can open and copy it; present that URL and do not fetch it first. saveTo stores the full-resolution PNG "
        + "in a root with Write access (root:relative\\file.png) and adds a download link (one-time ticket when API "
        + "tokens are configured). Do not use fetch_file to export a screenshot. Requires Vestigo:Desktop:AllowScreenshots. "
        + "Interactive session only.")]
    public async Task<CallToolResult> TakeScreenshot(
        [Description("'desktop' (default), 'display', 'window' or 'region'.")] string target = ScreenCaptureTarget.Desktop,
        [Description("GDI device name such as \\\\.\\DISPLAY2, or 'primary'. Required for target 'display'.")] string? display = null,
        [Description("Hex window handle from list_windows (0x...).")] string? windowHandle = null,
        [Description("Process id of the window to capture.")] int? pid = null,
        [Description("Process name, with or without .exe.")] string? processName = null,
        [Description(".NET regular expression matched against window titles (case-insensitive).")] string? titleRegex = null,
        [Description("Region left, virtual-screen physical pixels.")] int? x = null,
        [Description("Region top, virtual-screen physical pixels.")] int? y = null,
        [Description("Region width in physical pixels.")] int? width = null,
        [Description("Region height in physical pixels.")] int? height = null,
        [Description("Draw the mouse cursor on the capture.")] bool includeCursor = false,
        [Description("Longest edge of the inline preview in pixels. 0 omits the preview. Default 1920.")] int maxEdge = ScreenCaptureTarget.DefaultMaxEdge,
        [Description("Optional destination as root:relative\\file.png. Needs Write access.")] string? saveTo = null,
        [Description("Replace an existing saveTo file.")] bool overwrite = false,
        [Description(
            "When true, also return a short-lived single-use URL for the downscaled preview so a person can open and copy it. "
            + "The MCP image block is for the model and may not appear in chat. Present the share URL and do not fetch it. Needs maxEdge above 0.")]
        bool share = false,
        [Description("Interactive only: use the elevated token when capturing as the console user.")] bool elevated = false,
        CancellationToken cancellationToken = default)
    {
        if (!options.Desktop.AllowScreenshots)
        {
            return ErrorResult(
                "screenshots_disabled",
                "Screenshots are disabled. Set Vestigo:Desktop:AllowScreenshots to true.");
        }

        if (!ScreenCaptureTarget.TryParse(target, out string parsedTarget, out string? error))
        {
            return ErrorResult("invalid_request", error!);
        }

        if (maxEdge is < 0 or > ScreenCaptureTarget.MaxMaxEdge)
        {
            return ErrorResult("invalid_request", $"maxEdge must be between 0 and {ScreenCaptureTarget.MaxMaxEdge}.");
        }

        if (share && maxEdge == 0)
        {
            return ErrorResult("share_unavailable", "share needs a preview. Set maxEdge above 0.");
        }

        if (share && shares is null)
        {
            return ErrorResult("share_unavailable", "Screenshot sharing is not available.");
        }

        bool hasWindowFilter = pid is not null || !string.IsNullOrWhiteSpace(processName) || !string.IsNullOrWhiteSpace(titleRegex);
        if (!string.IsNullOrWhiteSpace(windowHandle) && hasWindowFilter)
        {
            return ErrorResult("invalid_request", "Pass windowHandle or pid/processName/titleRegex, not both.");
        }

        DesktopRect? region = null;
        if (parsedTarget == ScreenCaptureTarget.Region)
        {
            if (x is null || y is null || width is null || height is null)
            {
                return ErrorResult("invalid_request", "region needs x, y, width and height.");
            }

            region = new DesktopRect(x.Value, y.Value, width.Value, height.Value);
        }

        string? savePath = null;
        if (!string.IsNullOrWhiteSpace(saveTo))
        {
            PathCheck resolved = roots.Resolve(saveTo, RootAccess.Write);
            if (!resolved.Success)
            {
                return ErrorResult(resolved.ErrorCode!, resolved.Error!);
            }

            PathCheck check = FileAccessGuard.Validate(resolved);
            if (!check.Success)
            {
                return ErrorResult(check.ErrorCode!, check.Error!);
            }

            savePath = check.FullPath;
        }

        ScreenCaptureRequest request = new(
            parsedTarget,
            string.IsNullOrWhiteSpace(display) ? null : display.Trim(),
            string.IsNullOrWhiteSpace(windowHandle) ? null : windowHandle.Trim(),
            pid is { } id ? [id] : [],
            string.IsNullOrWhiteSpace(processName) ? [] : [processName.Trim()],
            string.IsNullOrWhiteSpace(titleRegex) ? null : titleRegex,
            region,
            includeCursor,
            maxEdge,
            savePath is not null,
            options.Desktop.MaxScreenshotBytes);

        OperationResult<CapturedScreenshot> captured = await inventory
            .CaptureAsync(request, savePath, overwrite, elevated, cancellationToken)
            .ConfigureAwait(false);
        if (!captured.Success)
        {
            return ErrorResult(captured);
        }

        FileLink? download = captured.Value!.Info.SavedPath is { } saved ? links?.TryCreateDownload(saved) : null;
        ScreenshotShareLink? shareLink = null;
        OperationResult<ScreenshotShareLink>? shareFailure = null;
        if (share)
        {
            if (captured.Value.PreviewPng is not { Length: > 0 })
            {
                return ErrorResult("share_unavailable", "share needs a preview. Set maxEdge above 0.");
            }

            OperationResult<ScreenshotShareLink> issued = shares!.Issue(captured.Value.PreviewPng, BaseUrl());
            if (!issued.Success)
            {
                // The PNG is already stored, so a full share store must not hide that file.
                if (captured.Value.Info.SavedPath is not { Length: > 0 })
                {
                    return ErrorResult(issued);
                }

                shareFailure = issued;
            }
            else
            {
                shareLink = issued.Value;
            }
        }

        return SuccessResult(captured.Value, download, shareLink, shareFailure);
    }

    internal static CallToolResult SuccessResult(
        CapturedScreenshot capture,
        FileLink? download = null,
        ScreenshotShareLink? share = null,
        OperationResult<ScreenshotShareLink>? shareFailure = null)
    {
        List<ContentBlock> content = [];
        if (capture.PreviewPng is { Length: > 0 })
        {
            content.Add(ImageContentBlock.FromBytes(capture.PreviewPng, "image/png"));
        }

        if (share is not null)
        {
            content.Add(new ResourceLinkBlock
            {
                Uri = share.Url,
                Name = "vestigo-screenshot.png",
                Title = "Desktop screenshot",
                Description = share.Instruction,
                MimeType = "image/png",
                Size = capture.PreviewPng?.LongLength,
                Annotations = new Annotations { Audience = [Role.User] }
            });
        }

        string json = JsonSerializer.Serialize(capture.Info, SessionHelper.Json);
        System.Text.Json.Nodes.JsonObject node = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!;
        if (download is not null)
        {
            node["download"] = JsonSerializer.SerializeToNode(download, SessionHelper.Json);
        }

        if (share is not null)
        {
            node["share"] = JsonSerializer.SerializeToNode(share, SessionHelper.Json);
        }
        else if (shareFailure is not null)
        {
            node["shareError"] = JsonSerializer.SerializeToNode(
                new { errorCode = shareFailure.ErrorCode, error = shareFailure.Error },
                SessionHelper.Json);
        }

        content.Add(new TextContentBlock { Text = node.ToJsonString(SessionHelper.Json) });
        return new CallToolResult { Content = content };
    }

    private string BaseUrl()
    {
        HttpRequest? request = httpContext?.HttpContext?.Request;
        if (request is not null && request.Host.HasValue)
        {
            return $"{request.Scheme}://{request.Host.Value}";
        }

        System.Net.IPAddress address = System.Net.IPAddress.Parse(options.ListenAddress);
        string host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        return $"http://{host}:{options.ListenPort}";
    }

    internal static CallToolResult ErrorResult<T>(OperationResult<T> result) =>
        ErrorResult(result.ErrorCode!, result.Error!, result.Reason);

    internal static CallToolResult ErrorResult(string errorCode, string error, string? reason = null)
    {
        object payload = reason is null ? new { errorCode, error } : new { errorCode, error, reason };
        return new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, SessionHelper.Json) }]
        };
    }
}
