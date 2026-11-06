using System.Text.Json;
using System.Text.Json.Serialization;

using Vestigo.Desktop;

namespace Vestigo.Vision;

/// <summary>Where a <see cref="ScreenElement" /> came from.</summary>
public static class ElementSource
{
    public const string Uia = "uia";

    public const string Ocr = "ocr";

    public const string Cv = "cv";
}

/// <summary>
///     One thing on screen. <paramref name="Box" /> is in virtual-screen physical pixels when it travels between the
///     helper, the merger and the cache, and in image pixels of the returned preview when it is shown to the client.
/// </summary>
public sealed record ScreenElement(
    int Id,
    string Source,
    string? Role,
    string? Name,
    string? Text,
    DesktopRect Box,
    bool? Enabled = null,
    bool? Focused = null);

/// <summary>Helper output of <c>inspect-screen</c>: the full capture plus UI Automation and OCR elements.</summary>
/// <param name="Capture">Capture with <c>FullPngBase64</c> set. Element boxes are physical pixels.</param>
/// <param name="OcrStatus"><c>ok</c>, <c>skipped</c>, or an <c>ocr_unavailable</c> reason.</param>
/// <param name="UiaStatus"><c>ok</c>, <c>skipped</c>, <c>truncated</c>, or a failure reason.</param>
public sealed record InspectCaptureResult(
    ScreenCaptureResult Capture,
    IReadOnlyList<ScreenElement> Elements,
    string OcrStatus,
    string UiaStatus);

/// <summary>A region where two captures differ, in image pixels.</summary>
public sealed record ChangedRegion(DesktopRect Box);

public sealed record DiffResult(IReadOnlyList<ChangedRegion> Regions, double ChangedPercent);

/// <summary>A template-matching candidate in image pixels.</summary>
public sealed record TemplateMatch(DesktopRect Box, double Confidence);

public static class VisionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
