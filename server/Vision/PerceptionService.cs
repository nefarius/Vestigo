using System.Diagnostics;

using Vestigo.Configuration;
using Vestigo.Desktop;
using Vestigo.Processes;

namespace Vestigo.Vision;

/// <summary>What the client asked <c>inspect_screen</c> to do.</summary>
public sealed record PerceptionRequest(
    ScreenCaptureRequest Capture,
    bool Uia,
    bool Ocr,
    int MaxEdge,
    bool Grid,
    bool Annotate,
    string? DiffAgainst,
    bool Elevated);

/// <summary>A finished inspection: the cached record, the annotated or raw image, and how each source fared.</summary>
public sealed record PerceptionOutcome(
    CaptureRecord Record,
    ScreenCaptureResult Capture,
    byte[]? Image,
    string UiaStatus,
    string OcrStatus,
    DiffResult? Diff);

/// <summary>Runs the helper's capture, UI Automation and OCR, then the OpenCV pipeline, and caches the result.</summary>
public sealed class PerceptionService(
    IProcessLauncher launcher,
    SessionHelperClient helper,
    CaptureCache cache,
    VestigoOptions options,
    ILogger<PerceptionService>? logger = null)
{
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(45);

    public CaptureCache Cache { get; } = cache;

    /// <param name="extraElements">
    ///     Optional extra elements (for example template matches) computed from the analyzed image. They are added
    ///     after merging, so they get ids that follow the merged list. Boxes are physical pixels.
    /// </param>
    public async Task<OperationResult<PerceptionOutcome>> InspectAsync(
        PerceptionRequest request,
        Func<AnalysisResult, DesktopRect, IReadOnlyList<ScreenElement>>? extraElements,
        CancellationToken cancellationToken)
    {
        CaptureRecord? previous = null;
        if (!string.IsNullOrWhiteSpace(request.DiffAgainst) && !Cache.TryGet(request.DiffAgainst.Trim(), out previous))
        {
            return OperationResult<PerceptionOutcome>.Fail("capture_expired", $"Capture '{request.DiffAgainst}' is unknown or expired.");
        }

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, request.Elevated);
        if (!available.Success)
        {
            return OperationResult<PerceptionOutcome>.Fail(available.ErrorCode!, available.Error!);
        }

        long maxBytes = options.Desktop.MaxScreenshotBytes;
        ScreenCaptureRequest capture = request.Capture with { MaxBytes = maxBytes, IncludeFullImage = true, MaxEdge = 0 };
        Stopwatch clock = Stopwatch.StartNew();
        OperationResult<InspectCaptureResult> helperResult = await helper.RunAsync<InspectCaptureResult>(
            SessionHelper.BuildInspectArguments(capture, request.Uia, request.Ocr),
            request.Elevated,
            HelperTimeout,
            maxBytes,
            cancellationToken).ConfigureAwait(false);
        if (!helperResult.Success)
        {
            return OperationResult<PerceptionOutcome>.Fail(helperResult.ErrorCode!, helperResult.Error!);
        }

        InspectCaptureResult value = helperResult.Value!;
        byte[] fullPng;
        try
        {
            fullPng = Convert.FromBase64String(value.Capture.FullPngBase64 ?? string.Empty);
        }
        catch (FormatException)
        {
            return OperationResult<PerceptionOutcome>.Fail("helper_failed", "The session helper returned invalid image data.");
        }

        if (fullPng.Length == 0)
        {
            return OperationResult<PerceptionOutcome>.Fail("helper_failed", "The session helper did not return an image.");
        }

        AnalysisResult analysis;
        DiffResult? diff = null;
        try
        {
            analysis = await Task.Run(
                () => ScreenAnalyzer.Analyze(
                    fullPng,
                    value.Capture.Bounds,
                    value.Elements.Where(item => item.Source == ElementSource.Uia).ToList(),
                    value.Elements.Where(item => item.Source == ElementSource.Ocr).ToList(),
                    request.MaxEdge,
                    request.Grid,
                    request.Annotate),
                cancellationToken).ConfigureAwait(false);
            if (previous is not null)
            {
                if (previous.ImageSize != analysis.ImageSize || previous.Bounds != value.Capture.Bounds)
                {
                    return OperationResult<PerceptionOutcome>.Fail(
                        "capture_mismatch",
                        "diffAgainst needs the same target, area and maxEdge as the earlier capture.");
                }

                diff = await Task.Run(() => ScreenAnalyzer.Diff(previous.WorkingPng, analysis.WorkingPng), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or OpenCvSharp.OpenCVException or DllNotFoundException or BadImageFormatException)
        {
            string hint = ex is DllNotFoundException or BadImageFormatException
                ? " The OpenCV runtime could not be loaded. On Windows N and KN editions install the Media Feature Pack, then restart the service."
                : string.Empty;
            return OperationResult<PerceptionOutcome>.Fail("vision_failed", "Image analysis failed: " + ex.Message + hint);
        }

        IReadOnlyList<ScreenElement> elements = analysis.Elements;
        if (extraElements is not null)
        {
            List<ScreenElement> combined = [.. elements];
            int next = elements.Count + 1;
            foreach (ScreenElement extra in extraElements(analysis, value.Capture.Bounds))
            {
                combined.Add(extra with { Id = next++ });
            }

            elements = combined;
        }

        CaptureRecord record = Cache.Add(value.Capture.Bounds, analysis.ImageSize, elements, analysis.WorkingPng);
        logger?.LogInformation(
            "Inspect target={Target} image={Width}x{Height} elements={Elements} uia={Uia} ocr={Ocr} durationMs={Ms}",
            value.Capture.Target,
            analysis.ImageSize.Width,
            analysis.ImageSize.Height,
            elements.Count,
            value.UiaStatus,
            value.OcrStatus,
            clock.ElapsedMilliseconds);

        byte[]? image = request.Annotate || request.Grid ? analysis.AnnotatedPng : null;
        return OperationResult<PerceptionOutcome>.Ok(new PerceptionOutcome(
            record,
            value.Capture with { PreviewPngBase64 = null, FullPngBase64 = null },
            image,
            value.UiaStatus,
            value.OcrStatus,
            diff));
    }
}
