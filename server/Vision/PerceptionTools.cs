using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Desktop;
using Vestigo.Files;
using Vestigo.Security;

namespace Vestigo.Vision;

/// <summary>
///     MCP tools that let an agent see the screen: <c>inspect_screen</c> returns a numbered, annotated view plus a
///     text element list, and <c>find_on_screen</c> locates text or an image template.
/// </summary>
[McpServerToolType]
public sealed class PerceptionTools(PerceptionService perception, VestigoOptions options, RootPolicy roots)
{
    public const string ImageAnnotated = "annotated";

    public const string ImageRaw = "raw";

    public const string ImageNone = "none";

    private const long MaxTemplateBytes = 8 * 1024 * 1024;

    private const long MaxTemplatePixels = 16_000_000;

    [McpServerTool(Name = "inspect_screen", ReadOnly = true, Destructive = false)]
    [Description(
        "Looks at the console user's screen and describes it for an agent. Returns an annotated image with numbered boxes plus a JSON element list "
        + "(id, source uia|ocr|cv, role, name or text, box in image pixels) that works without seeing the image. Elements come from UI Automation, "
        + "Windows OCR, and OpenCV contour regions where the first two find nothing. The result has a captureId: pass it to send_input to click an "
        + "elementId, or to use x,y in the returned image's pixels. The id stays valid for about two minutes. diffAgainst compares with an earlier captureId "
        + "(same target and maxEdge) and lists the regions that changed. imageMode 'annotated' (default), 'raw' or 'none' controls the image. "
        + "grid:true draws a labelled coordinate grid instead of boxes. The list holds at most 150 elements, shared evenly by UI Automation and OCR, "
        + "with OpenCV regions filling what remains. uia and ocr report how collection went (ok, skipped, truncated, or a failure). "
        + "truncated, candidates and omitted report when collected elements were left out. Takes the same targets as take_screenshot. "
        + "Requires Vestigo:Desktop:AllowScreenshots. Interactive session only.")]
    public async Task<CallToolResult> InspectScreen(
        [Description("'desktop' (default), 'display', 'window' or 'region'.")] string target = ScreenCaptureTarget.Desktop,
        [Description("GDI device name such as \\\\.\\DISPLAY2, or 'primary'. Required for target 'display'.")] string? display = null,
        [Description("Hex window handle from list_windows (0x...).")] string? windowHandle = null,
        [Description("Process id of the window to inspect.")] int? pid = null,
        [Description("Process name, with or without .exe.")] string? processName = null,
        [Description(".NET regular expression matched against window titles (case-insensitive).")] string? titleRegex = null,
        [Description("Region left, virtual-screen physical pixels.")] int? x = null,
        [Description("Region top, virtual-screen physical pixels.")] int? y = null,
        [Description("Region width in physical pixels.")] int? width = null,
        [Description("Region height in physical pixels.")] int? height = null,
        [Description("'annotated' (default), 'raw' or 'none'.")] string imageMode = ImageAnnotated,
        [Description("Longest edge of the returned image in pixels, 320 to 3840. Default 1280.")] int maxEdge = ScreenAnalyzer.DefaultMaxEdge,
        [Description("Draw a coordinate grid instead of numbered boxes.")] bool grid = false,
        [Description("Read UI Automation elements (names and roles; edit values are never read). Default true.")] bool uia = true,
        [Description("Recognize text with Windows OCR. Default true.")] bool ocr = true,
        [Description("Draw the mouse cursor on the capture.")] bool includeCursor = false,
        [Description("captureId of an earlier inspection to diff against.")] string? diffAgainst = null,
        [Description("Use the elevated token when capturing as the console user.")] bool elevated = false,
        CancellationToken cancellationToken = default)
    {
        OperationResult<(ScreenCaptureRequest Request, string Mode)> prepared =
            Prepare(target, display, windowHandle, pid, processName, titleRegex, x, y, width, height, imageMode, maxEdge, includeCursor);
        if (!prepared.Success)
        {
            return DesktopTools.ErrorResult(prepared.ErrorCode!, prepared.Error!);
        }

        string mode = prepared.Value.Mode;
        OperationResult<PerceptionOutcome> outcome = await perception.InspectAsync(
            new PerceptionRequest(prepared.Value.Request, uia, ocr, maxEdge, grid, mode == ImageAnnotated, diffAgainst, elevated),
            null,
            cancellationToken).ConfigureAwait(false);
        return outcome.Success
            ? Describe(outcome.Value!, mode, null)
            : DesktopTools.ErrorResult(outcome);
    }

    [McpServerTool(Name = "find_on_screen", ReadOnly = true, Destructive = false)]
    [Description(
        "Finds things on the console user's screen. text matches UI Automation names and OCR text (case-insensitive substring) "
        + "across every collected candidate, before the 150-element cap, so a full UI Automation list cannot hide an OCR match. "
        + "matchesTruncated is true when the matches themselves exceed that cap. "
        + "template matches an image with multi-scale OpenCV template matching, from a PNG or JPEG at root:relative\\file.png (needs Read access) or "
        + "from a crop of an earlier capture (templateCaptureId with templateX, templateY, templateWidth, templateHeight in that capture's image pixels). "
        + "Give text, a template, or both. Returns matches with boxes in image pixels and an elementId on the new captureId, so send_input can click a match. "
        + "Template matches carry a confidence from 0 to 1. Requires Vestigo:Desktop:AllowScreenshots. Interactive session only.")]
    public async Task<CallToolResult> FindOnScreen(
        [Description("Text to find in element names or OCR text.")] string? text = null,
        [Description("Template image as root:relative\\file.png.")] string? template = null,
        [Description("Earlier captureId to crop the template from.")] string? templateCaptureId = null,
        [Description("Crop left in the earlier capture's image pixels.")] int? templateX = null,
        [Description("Crop top.")] int? templateY = null,
        [Description("Crop width.")] int? templateWidth = null,
        [Description("Crop height.")] int? templateHeight = null,
        [Description("Minimum template confidence 0.5 to 0.99. Default 0.8.")] double minConfidence = 0.8,
        [Description("'desktop' (default), 'display', 'window' or 'region'.")] string target = ScreenCaptureTarget.Desktop,
        [Description("GDI device name such as \\\\.\\DISPLAY2, or 'primary'. Required for target 'display'.")] string? display = null,
        [Description("Hex window handle from list_windows (0x...).")] string? windowHandle = null,
        [Description("Process id of the window to search.")] int? pid = null,
        [Description("Process name, with or without .exe.")] string? processName = null,
        [Description(".NET regular expression matched against window titles (case-insensitive).")] string? titleRegex = null,
        [Description("Region left, virtual-screen physical pixels.")] int? x = null,
        [Description("Region top, virtual-screen physical pixels.")] int? y = null,
        [Description("Region width in physical pixels.")] int? width = null,
        [Description("Region height in physical pixels.")] int? height = null,
        [Description("'annotated' (default), 'raw' or 'none'.")] string imageMode = ImageAnnotated,
        [Description("Longest edge of the returned image in pixels, 320 to 3840. Default 1280.")] int maxEdge = ScreenAnalyzer.DefaultMaxEdge,
        [Description("Use the elevated token when capturing as the console user.")] bool elevated = false,
        CancellationToken cancellationToken = default)
    {
        bool hasTemplate = !string.IsNullOrWhiteSpace(template) || !string.IsNullOrWhiteSpace(templateCaptureId);
        if (string.IsNullOrWhiteSpace(text) && !hasTemplate)
        {
            return DesktopTools.ErrorResult("invalid_request", "Give text, a template, or both.");
        }

        if (!string.IsNullOrWhiteSpace(template) && !string.IsNullOrWhiteSpace(templateCaptureId))
        {
            return DesktopTools.ErrorResult("invalid_request", "Pass template or templateCaptureId, not both.");
        }

        if (minConfidence is < 0.5 or > 0.99)
        {
            return DesktopTools.ErrorResult("invalid_request", "minConfidence must be between 0.5 and 0.99.");
        }

        OperationResult<(ScreenCaptureRequest Request, string Mode)> prepared =
            Prepare(target, display, windowHandle, pid, processName, titleRegex, x, y, width, height, imageMode, maxEdge, false);
        if (!prepared.Success)
        {
            return DesktopTools.ErrorResult(prepared.ErrorCode!, prepared.Error!);
        }

        byte[]? templatePng = null;
        if (hasTemplate)
        {
            OperationResult<byte[]> loaded = LoadTemplate(template, templateCaptureId, templateX, templateY, templateWidth, templateHeight);
            if (!loaded.Success)
            {
                return DesktopTools.ErrorResult(loaded.ErrorCode!, loaded.Error!);
            }

            templatePng = loaded.Value;
        }

        string mode = prepared.Value.Mode;
        IReadOnlyList<TemplateMatch> templateMatches = [];
        string? query = string.IsNullOrWhiteSpace(text) ? null : text;
        OperationResult<PerceptionOutcome> outcome = await perception.InspectAsync(
            new PerceptionRequest(
                prepared.Value.Request,
                true,
                query is not null,
                maxEdge,
                false,
                mode == ImageAnnotated,
                null,
                elevated,
                query is null ? null : element => MatchesText(element, query)),
            templatePng is null
                ? null
                : (analysis, bounds) =>
                {
                    templateMatches = ScreenAnalyzer.FindTemplate(analysis.WorkingPng, templatePng, minConfidence);
                    double sx = (double)bounds.Width / analysis.ImageSize.Width;
                    double sy = (double)bounds.Height / analysis.ImageSize.Height;
                    return templateMatches
                        .Select(match => new ScreenElement(
                            0,
                            ElementSource.Cv,
                            "template-match",
                            null,
                            null,
                            new DesktopRect(
                                bounds.X + (int)Math.Round(match.Box.X * sx),
                                bounds.Y + (int)Math.Round(match.Box.Y * sy),
                                Math.Max(1, (int)Math.Round(match.Box.Width * sx)),
                                Math.Max(1, (int)Math.Round(match.Box.Height * sy)))))
                        .ToList();
                },
            cancellationToken).ConfigureAwait(false);
        if (!outcome.Success)
        {
            return DesktopTools.ErrorResult(outcome);
        }

        PerceptionOutcome value = outcome.Value!;
        List<JsonObject> matches = [];
        if (!string.IsNullOrWhiteSpace(text))
        {
            foreach (ScreenElement element in value.Record.Elements.Where(item => item.Role != "template-match"))
            {
                if (MatchesText(element, text))
                {
                    matches.Add(MatchNode("text", element, value.Record, null));
                }
            }
        }

        if (templatePng is not null)
        {
            IEnumerable<ScreenElement> templateElements = value.Record.Elements.Where(item => item.Role == "template-match");
            int index = 0;
            foreach (ScreenElement element in templateElements)
            {
                matches.Add(MatchNode("template", element, value.Record, templateMatches[index++].Confidence));
            }
        }

        JsonObject extra = new() { ["matchCount"] = matches.Count, ["matches"] = new JsonArray(matches.Select(node => (JsonNode)node).ToArray()) };
        if (value.PriorityOmitted > 0)
        {
            extra["matchesTruncated"] = true;
        }

        return Describe(value, mode, extra, includeElements: false);
    }

    private OperationResult<(ScreenCaptureRequest Request, string Mode)> Prepare(
        string target,
        string? display,
        string? windowHandle,
        int? pid,
        string? processName,
        string? titleRegex,
        int? x,
        int? y,
        int? width,
        int? height,
        string imageMode,
        int maxEdge,
        bool includeCursor)
    {
        if (!options.Desktop.AllowScreenshots)
        {
            return OperationResult<(ScreenCaptureRequest, string)>.Fail(
                "screenshots_disabled",
                "Screenshots are disabled. Set Vestigo:Desktop:AllowScreenshots to true.");
        }

        if (!ScreenCaptureTarget.TryParse(target, out string parsedTarget, out string? error))
        {
            return Invalid(error!);
        }

        string mode = (imageMode ?? ImageAnnotated).Trim().ToLowerInvariant();
        if (mode is not (ImageAnnotated or ImageRaw or ImageNone))
        {
            return Invalid("imageMode must be 'annotated', 'raw' or 'none'.");
        }

        if (maxEdge is < ScreenAnalyzer.MinMaxEdge or > ScreenAnalyzer.MaxMaxEdge)
        {
            return Invalid($"maxEdge must be between {ScreenAnalyzer.MinMaxEdge} and {ScreenAnalyzer.MaxMaxEdge}.");
        }

        bool hasWindowFilter = pid is not null || !string.IsNullOrWhiteSpace(processName) || !string.IsNullOrWhiteSpace(titleRegex);
        if (!string.IsNullOrWhiteSpace(windowHandle) && hasWindowFilter)
        {
            return Invalid("Pass windowHandle or pid/processName/titleRegex, not both.");
        }

        DesktopRect? region = null;
        if (parsedTarget == ScreenCaptureTarget.Region)
        {
            if (x is null || y is null || width is null || height is null)
            {
                return Invalid("region needs x, y, width and height.");
            }

            region = new DesktopRect(x.Value, y.Value, width.Value, height.Value);
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
            0,
            true,
            options.Desktop.MaxScreenshotBytes);
        return OperationResult<(ScreenCaptureRequest, string)>.Ok((request, mode));
    }

    private OperationResult<byte[]> LoadTemplate(string? path, string? captureId, int? x, int? y, int? width, int? height)
    {
        if (!string.IsNullOrWhiteSpace(captureId))
        {
            if (!perception.Cache.TryGet(captureId.Trim(), out CaptureRecord? record))
            {
                return OperationResult<byte[]>.Fail("capture_expired", $"Capture '{captureId}' is unknown or expired.");
            }

            if (x is null || y is null || width is null || height is null || width <= 0 || height <= 0)
            {
                return OperationResult<byte[]>.Fail("invalid_request", "templateCaptureId needs templateX, templateY, templateWidth and templateHeight.");
            }

            byte[]? crop = ScreenAnalyzer.Crop(record.WorkingPng, new DesktopRect(x.Value, y.Value, width.Value, height.Value));
            return crop is null
                ? OperationResult<byte[]>.Fail("invalid_request", "The template rectangle lies outside the capture.")
                : OperationResult<byte[]>.Ok(crop);
        }

        PathCheck resolved = roots.Resolve(path!, RootAccess.Read);
        if (!resolved.Success)
        {
            return OperationResult<byte[]>.Fail(resolved.ErrorCode!, resolved.Error!);
        }

        PathCheck check = FileAccessGuard.Validate(resolved);
        if (!check.Success)
        {
            return OperationResult<byte[]>.Fail(check.ErrorCode!, check.Error!);
        }

        try
        {
            FileInfo file = new(check.FullPath!);
            if (!file.Exists)
            {
                return OperationResult<byte[]>.Fail("file_not_found", "The template file does not exist.");
            }

            if (file.Length is 0 or > MaxTemplateBytes)
            {
                return OperationResult<byte[]>.Fail("invalid_request", $"The template must be between 1 byte and {MaxTemplateBytes} bytes.");
            }

            byte[] bytes = File.ReadAllBytes(file.FullName);
            if (!ImageHeader.TryReadSize(bytes, out int pixelWidth, out int pixelHeight) ||
                (long)pixelWidth * pixelHeight > MaxTemplatePixels)
            {
                return OperationResult<byte[]>.Fail(
                    "invalid_request",
                    $"The template must be a PNG, JPEG or BMP image of at most {MaxTemplatePixels / 1_000_000} megapixels.");
            }

            return OperationResult<byte[]>.Ok(bytes);
        }
        catch (IOException ex)
        {
            return OperationResult<byte[]>.Fail("read_failed", ex.Message);
        }
    }

    /// <summary>True when <paramref name="text" /> occurs in the element's name or OCR text.</summary>
    private static bool MatchesText(ScreenElement element, string text) =>
        (element.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
        || (element.Text?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);

    private static JsonObject MatchNode(string kind, ScreenElement element, CaptureRecord record, double? confidence)
    {
        JsonObject node = new()
        {
            ["kind"] = kind,
            ["elementId"] = element.Id,
            ["box"] = JsonSerializer.SerializeToNode(record.ToImage(element.Box), VisionJson.Options)
        };
        if (element.Name is not null)
        {
            node["name"] = element.Name;
        }

        if (element.Text is not null)
        {
            node["text"] = element.Text;
        }

        if (confidence is { } value)
        {
            node["confidence"] = value;
        }

        return node;
    }

    /// <summary>Builds the JSON result for an inspection, including whether the element cap left anything out.</summary>
    internal static CallToolResult Describe(PerceptionOutcome outcome, string imageMode, JsonObject? extra, bool includeElements = true)
    {
        CaptureRecord record = outcome.Record;
        JsonObject node = new()
        {
            ["captureId"] = record.Id,
            ["target"] = outcome.Capture.Target,
            ["bounds"] = JsonSerializer.SerializeToNode(record.Bounds, VisionJson.Options),
            ["imageSize"] = JsonSerializer.SerializeToNode(record.ImageSize, VisionJson.Options),
            ["windowHandle"] = outcome.Capture.WindowHandle,
            ["windowTitle"] = outcome.Capture.WindowTitle,
            ["uia"] = outcome.UiaStatus,
            ["ocr"] = outcome.OcrStatus,
            ["truncated"] = outcome.Omitted.Total > 0,
            ["candidates"] = SourceCountsNode(outcome.Candidates),
            ["omitted"] = SourceCountsNode(outcome.Omitted),
            ["validForSeconds"] = (int)CaptureCache.DefaultTtl.TotalSeconds
        };
        if (includeElements)
        {
            node["elementCount"] = record.Elements.Count;
            node["elements"] = JsonSerializer.SerializeToNode(
                record.Elements.Select(element => element with { Box = record.ToImage(element.Box) }).ToList(),
                VisionJson.Options);
        }

        if (outcome.Diff is { } diff)
        {
            node["diff"] = JsonSerializer.SerializeToNode(diff, VisionJson.Options);
        }

        if (extra is not null)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in extra.ToList())
            {
                extra.Remove(pair.Key);
                node[pair.Key] = pair.Value;
            }
        }

        List<ContentBlock> content = [];
        byte[]? image = imageMode switch
        {
            ImageNone => null,
            ImageRaw => record.WorkingPng,
            _ => outcome.Image ?? record.WorkingPng
        };
        if (image is { Length: > 0 })
        {
            content.Add(ImageContentBlock.FromBytes(image, "image/png"));
        }

        content.Add(new TextContentBlock { Text = node.ToJsonString(VisionJson.Options) });
        return new CallToolResult { Content = content };
    }

    /// <summary>Per-source counts for the inspection JSON.</summary>
    private static JsonObject SourceCountsNode(SourceCounts counts) => new()
    {
        ["uia"] = counts.Uia,
        ["ocr"] = counts.Ocr,
        ["cv"] = counts.Cv
    };

    private static OperationResult<(ScreenCaptureRequest, string)> Invalid(string message) =>
        OperationResult<(ScreenCaptureRequest, string)>.Fail("invalid_request", message);
}
