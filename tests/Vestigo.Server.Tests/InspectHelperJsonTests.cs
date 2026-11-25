using System.Text.Json;

using Vestigo.Desktop;
using Vestigo.Processes;
using Vestigo.Vision;

namespace Vestigo.Server.Tests;

/// <summary>
///     The source-generated <c>inspect-screen</c> contract must stay byte-identical to
///     <see cref="SessionHelper.Json" /> so the service parser and existing fixtures keep working.
/// </summary>
public class InspectHelperJsonTests
{
    [Fact]
    public void GeneratedResult_MatchesReflectionJson_ForAFullWindowCapture()
    {
        InspectCaptureResult value = Result(
            capture: Capture(windowBounds: null, effectiveCrop: null, preview: null),
            elements:
            [
                new ScreenElement(1, ElementSource.Uia, "button", "Save", null, new DesktopRect(10, 20, 80, 30), true, false),
                new ScreenElement(2, ElementSource.Ocr, "text", null, "Status ready", new DesktopRect(12, 24, 90, 16))
            ],
            ocrStatus: "ok",
            uiaStatus: "ok");

        AssertRoundTrip(value);
    }

    [Fact]
    public void GeneratedResult_MatchesReflectionJson_ForACroppedCapture()
    {
        InspectCaptureResult value = Result(
            capture: Capture(
                windowBounds: new DesktopRect(100, 80, 1920, 1160),
                effectiveCrop: new DesktopRect(20, 16, 835, 450),
                preview: new DesktopSize(835, 450),
                title: "Vestigo Profile Fixture — café"),
            elements:
            [
                new ScreenElement(3, ElementSource.Uia, "text", "Status changed", "Status changed", new DesktopRect(40, 86, 120, 18), false, true),
                new ScreenElement(4, ElementSource.Ocr, null, null, null, new DesktopRect(40, 86, 60, 14), null, null)
            ],
            ocrStatus: "truncated",
            uiaStatus: "uia_unavailable: walker failed");

        AssertRoundTrip(value);
    }

    [Fact]
    public void GeneratedResult_MatchesReflectionJson_WhenSourcesAreSkipped()
    {
        InspectCaptureResult value = Result(
            capture: Capture(windowBounds: new DesktopRect(0, 0, 800, 600), effectiveCrop: new DesktopRect(0, 0, 800, 600), preview: null),
            elements: [],
            ocrStatus: "skipped",
            uiaStatus: "skipped");

        AssertRoundTrip(value);
    }

    [Fact]
    public void GeneratedError_MatchesTheReflectionEnvelope_AndTheServiceParser()
    {
        AssertError("Option '--uia' needs true or false.", "invalid_request", null);
        AssertError(
            "The crop rectangle does not intersect the window.",
            "empty_region",
            "crop_miss");
        AssertError("Invalid title pattern: too long", "invalid_request", null);
    }

    private static void AssertRoundTrip(InspectCaptureResult value)
    {
        string reflection = JsonSerializer.Serialize(value, SessionHelper.Json);
        string generated = JsonSerializer.Serialize(value, InspectHelperJsonContext.Default.InspectCaptureResult);
        Assert.Equal(reflection, generated);

        OperationResult<InspectCaptureResult> parsed = SessionHelperClient.Parse<InspectCaptureResult>("noise\n" + generated + "\n", 0);
        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(reflection, JsonSerializer.Serialize(parsed.Value, SessionHelper.Json));
        Assert.Equal(value.OcrStatus, parsed.Value!.OcrStatus);
        Assert.Equal(value.UiaStatus, parsed.Value.UiaStatus);
        Assert.Equal(value.Elements.Count, parsed.Value.Elements.Count);
        Assert.Equal(value.Capture.FullPngBase64, parsed.Value.Capture.FullPngBase64);
        Assert.Equal(value.Capture.WindowBounds, parsed.Value.Capture.WindowBounds);
        Assert.Equal(value.Capture.EffectiveCrop, parsed.Value.Capture.EffectiveCrop);
        Assert.Equal(value.Capture.WindowTitle, parsed.Value.Capture.WindowTitle);
    }

    private static void AssertError(string error, string code, string? reason)
    {
        object reflectionPayload = reason is null
            ? new { error, errorCode = code }
            : new { error, errorCode = code, reason };
        string reflection = JsonSerializer.Serialize(reflectionPayload, SessionHelper.Json);
        string generated = JsonSerializer.Serialize(
            new InspectHelperError(error, code, reason),
            InspectHelperJsonContext.Default.InspectHelperError);
        Assert.Equal(reflection, generated);

        OperationResult<InspectCaptureResult> parsed = SessionHelperClient.Parse<InspectCaptureResult>(generated, 2);
        Assert.False(parsed.Success);
        Assert.Equal(code, parsed.ErrorCode);
        Assert.Equal(error, parsed.Error);
        Assert.Equal(reason, parsed.Reason);
    }

    private static InspectCaptureResult Result(
        ScreenCaptureResult capture,
        IReadOnlyList<ScreenElement> elements,
        string ocrStatus,
        string uiaStatus) =>
        new(capture, elements, ocrStatus, uiaStatus);

    private static ScreenCaptureResult Capture(
        DesktopRect? windowBounds,
        DesktopRect? effectiveCrop,
        DesktopSize? preview,
        string? title = "Fixture") =>
        new(
            ScreenCaptureTarget.Window,
            effectiveCrop is null ? new DesktopRect(100, 80, 1920, 1160) : new DesktopRect(120, 96, effectiveCrop.Width, effectiveCrop.Height),
            new DesktopSize(effectiveCrop?.Width ?? 1920, effectiveCrop?.Height ?? 1160),
            preview,
            96,
            ["\\\\.\\DISPLAY1"],
            "0x20",
            title,
            null,
            Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A]),
            1,
            effectiveCrop is null ? "input-desktop" : "best-effort",
            windowBounds,
            effectiveCrop);
}
