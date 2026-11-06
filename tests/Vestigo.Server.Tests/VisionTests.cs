using System.Text.Json;

using ModelContextProtocol.Protocol;

using OpenCvSharp;

using Vestigo.Configuration;
using Vestigo.Desktop;
using Vestigo.Processes;
using Vestigo.Security;
using Vestigo.Vision;

namespace Vestigo.Server.Tests;

public class VisionTests
{
    [Fact]
    public void CaptureCache_ExpiresAndEvictsOldest()
    {
        ManualClock clock = new();
        CaptureCache cache = new(clock, capacity: 2, ttl: TimeSpan.FromMinutes(2));
        DesktopRect bounds = new(0, 0, 10, 10);
        DesktopSize size = new(10, 10);

        CaptureRecord first = cache.Add(bounds, size, [], [1]);
        CaptureRecord second = cache.Add(bounds, size, [], [1]);
        CaptureRecord third = cache.Add(bounds, size, [], [1]);

        Assert.False(cache.TryGet(first.Id, out _));
        Assert.True(cache.TryGet(second.Id, out _));
        Assert.True(cache.TryGet(third.Id.ToUpperInvariant(), out _));
        Assert.Equal(2, cache.Count);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(cache.TryGet(second.Id, out _));
        clock.Advance(TimeSpan.FromMinutes(1.5));
        Assert.False(cache.TryGet(second.Id, out _));
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(null, out _));
        Assert.StartsWith("cap-", first.Id);
    }

    [Fact]
    public void CaptureRecord_MapsBetweenImageAndScreen()
    {
        CaptureRecord record = new("cap-1", new DesktopRect(100, 50, 3840, 2160), new DesktopSize(1280, 720), [], [1], DateTimeOffset.UnixEpoch);

        Assert.Equal((100 + 300, 50 + 150), record.ToScreen(100, 50));
        Assert.Equal((100, 50), record.ToScreen(-5, -5));
        Assert.Equal((100 + 3839, 50 + 2159), record.ToScreen(5000, 5000));
        Assert.Equal(new DesktopRect(400, 200, 400, 200), record.ToImage(new DesktopRect(1300, 650, 1200, 600)));
        Assert.Equal(1, record.ToImage(new DesktopRect(100, 50, 1, 1)).Width);
    }

    [Fact]
    public void ElementMerger_DropsCoveredOcrAndCvAndNumbersInReadingOrder()
    {
        ScreenElement button = Uia("button", "Save", new DesktopRect(200, 10, 80, 30));
        ScreenElement duplicate = Uia("button", "Save", new DesktopRect(200, 10, 80, 30));
        ScreenElement label = Uia("text", "Name", new DesktopRect(10, 12, 60, 20));
        ScreenElement document = Uia("document", "Artifact Browser", new DesktopRect(0, 60, 500, 80));
        ScreenElement coveredText = Ocr("Save", new DesktopRect(210, 15, 50, 18));
        ScreenElement pageText = Ocr("Download", new DesktopRect(20, 90, 80, 14));
        ScreenElement freeText = Ocr("Footer text", new DesktopRect(10, 400, 100, 16));
        ScreenElement coveredRegion = Cv(new DesktopRect(195, 5, 95, 40));
        ScreenElement freeRegion = Cv(new DesktopRect(300, 200, 50, 50));

        MergeResult merged = ElementMerger.Merge(
            [button, duplicate, label, document],
            [coveredText, pageText, freeText],
            [coveredRegion, freeRegion]);
        IReadOnlyList<ScreenElement> elements = merged.Elements;

        Assert.Equal(6, elements.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6], elements.Select(item => item.Id).ToArray());
        Assert.Equal(["Save", "Name", "Artifact Browser"], elements.Where(item => item.Source == ElementSource.Uia).Select(item => item.Name).ToArray());
        Assert.DoesNotContain(elements, item => item.Text == "Save");
        Assert.Contains(elements, item => item.Text == "Download");
        Assert.Contains(elements, item => item.Text == "Footer text");
        Assert.Single(elements, item => item.Source == ElementSource.Cv);
        Assert.Equal(ElementSource.Ocr, elements[^1].Source);
        Assert.Equal(0, merged.Omitted.Total);
    }

    [Fact]
    public void ElementMerger_KeepsPriorityOcrHiddenByAShorterName()
    {
        ScreenElement button = Uia("button", "Download", new DesktopRect(0, 0, 200, 40));
        ScreenElement line = Ocr("Download the nightly build", new DesktopRect(10, 8, 160, 16));

        MergeResult plain = ElementMerger.Merge([button], [line], [], maxElements: 10);
        Assert.DoesNotContain(plain.Elements, item => item.Text == "Download the nightly build");

        MergeResult searched = ElementMerger.Merge(
            [button],
            [line],
            [],
            maxElements: 10,
            priority: item => Matches(item, "nightly"));
        Assert.Contains(searched.Elements, item => item.Text == "Download the nightly build");
        Assert.Contains(searched.Elements, item => item.Name == "Download");
        Assert.Equal(0, searched.Omitted.Total);

        MergeResult both = ElementMerger.Merge([button], [line], [], maxElements: 10, priority: MatchesDownload);
        Assert.Contains(both.Elements, item => item.Name == "Download");
        Assert.DoesNotContain(both.Elements, item => item.Source == ElementSource.Ocr);
    }

    [Fact]
    public void ElementMerger_KeepsRecognizedTextWhenUiAutomationFillsTheCap()
    {
        List<ScreenElement> controls = Enumerable.Range(0, 200)
            .Select(index => Uia("listitem", "Item " + index, new DesktopRect(0, index * 24, 40, 20)))
            .ToList();
        ScreenElement download = Ocr("Download", new DesktopRect(10, 200 * 24, 80, 14));

        MergeResult merged = ElementMerger.Merge(controls, [download], []);

        Assert.Equal(ElementMerger.MaxElements, merged.Elements.Count);
        Assert.Contains(merged.Elements, item => item.Text == "Download");
        Assert.Equal(200, merged.Candidates.Uia);
        Assert.Equal(1, merged.Candidates.Ocr);
        Assert.Equal(51, merged.Omitted.Uia);
        Assert.Equal(0, merged.Omitted.Ocr);

        List<ScreenElement> lines = Enumerable.Range(0, 200)
            .Select(index => Ocr("Line " + index, new DesktopRect(100, index * 24, 40, 14)))
            .Append(download)
            .ToList();
        MergeResult crowded = ElementMerger.Merge(controls, lines, [], maxElements: 10);
        Assert.DoesNotContain(crowded.Elements, item => item.Text == "Download");

        MergeResult prioritized = ElementMerger.Merge(controls, lines, [], maxElements: 10, priority: MatchesDownload);
        Assert.Contains(prioritized.Elements, item => item.Text == "Download");
        Assert.Equal(0, prioritized.PriorityOmitted);
    }

    [Fact]
    public void ElementMerger_SplitsTheCapBetweenSources()
    {
        List<ScreenElement> uia = Enumerable.Range(0, 200)
            .Select(index => Uia("button", "B" + index, new DesktopRect(0, index * 20, 10, 10)))
            .ToList();
        List<ScreenElement> ocr = Enumerable.Range(0, 200)
            .Select(index => Ocr("T" + index, new DesktopRect(40, index * 20, 10, 10)))
            .ToList();

        MergeResult even = ElementMerger.Merge(Enumerable.Reverse(uia), Enumerable.Reverse(ocr), [], maxElements: 10);

        Assert.Equal(["B0", "B1", "B2", "B3", "B4"], even.Elements.Where(item => item.Source == ElementSource.Uia).Select(item => item.Name).ToArray());
        Assert.Equal(["T0", "T1", "T2", "T3", "T4"], even.Elements.Where(item => item.Source == ElementSource.Ocr).Select(item => item.Text).ToArray());
        Assert.Equal(195, even.Omitted.Uia);
        Assert.Equal(195, even.Omitted.Ocr);

        MergeResult passed = ElementMerger.Merge(uia.Take(2), ocr, [], maxElements: 10);
        Assert.Equal(2, passed.Elements.Count(item => item.Source == ElementSource.Uia));
        Assert.Equal(8, passed.Elements.Count(item => item.Source == ElementSource.Ocr));

        List<ScreenElement> regions = Enumerable.Range(0, 6).Select(index => Cv(new DesktopRect(80, index * 20, 10, 10))).ToList();
        MergeResult withRegions = ElementMerger.Merge(uia.Take(1), ocr.Take(1), regions, maxElements: 6);
        Assert.Equal(1, withRegions.Elements.Count(item => item.Source == ElementSource.Uia));
        Assert.Equal(1, withRegions.Elements.Count(item => item.Source == ElementSource.Ocr));
        Assert.Equal(4, withRegions.Elements.Count(item => item.Source == ElementSource.Cv));
        Assert.Equal(2, withRegions.Omitted.Cv);
    }

    [Fact]
    public void ElementMerger_TruncatesPriorityMatchesInReadingOrder()
    {
        List<ScreenElement> lines = Enumerable.Range(0, 8)
            .Select(index => Ocr("Hit " + index, new DesktopRect(0, (7 - index) * 20, 30, 12)))
            .ToList();

        MergeResult merged = ElementMerger.Merge([], lines, [], maxElements: 3, priority: item => item.Text!.StartsWith("Hit", StringComparison.Ordinal));

        Assert.Equal(["Hit 7", "Hit 6", "Hit 5"], merged.Elements.Select(item => item.Text).ToArray());
        Assert.Equal(5, merged.PriorityOmitted);
        Assert.Equal(5, merged.Omitted.Ocr);
        Assert.Equal(8, merged.Candidates.Ocr);
    }

    [Fact]
    public void ElementMerger_CapsTheList()
    {
        List<ScreenElement> many = Enumerable.Range(0, 20).Select(index => Uia("button", "B" + index, new DesktopRect(index * 30, 0, 20, 20))).ToList();

        MergeResult merged = ElementMerger.Merge(Enumerable.Reverse(many), [], [], maxElements: 5);

        Assert.Equal(["B0", "B1", "B2", "B3", "B4"], merged.Elements.Select(item => item.Name).ToArray());
        Assert.Equal(20, merged.Candidates.Uia);
        Assert.Equal(15, merged.Omitted.Uia);
        Assert.Equal(0, merged.Omitted.Ocr);
        Assert.Equal(0, merged.Omitted.Cv);
    }

    [Fact]
    public void ElementMerger_ComputesCoverageAndOverlap()
    {
        DesktopRect box = new(0, 0, 10, 10);
        Assert.Equal(1, ElementMerger.Coverage(box, new DesktopRect(-5, -5, 30, 30)));
        Assert.Equal(0.5, ElementMerger.Coverage(box, new DesktopRect(5, 0, 10, 10)));
        Assert.Equal(0, ElementMerger.Coverage(box, new DesktopRect(20, 20, 5, 5)));
        Assert.Equal(1, ElementMerger.IntersectionOverUnion(box, box));
        Assert.Equal(0, ElementMerger.IntersectionOverUnion(box, new DesktopRect(10, 0, 5, 5)));
    }

    [Fact]
    public void SessionHelper_RoundTripsInspectArguments()
    {
        ScreenCaptureRequest request = ScreenCaptureRequest.DesktopPreview with
        {
            Target = ScreenCaptureTarget.Region,
            Region = new DesktopRect(-10, 20, 640, 480),
            TitlePattern = "Notepad.*",
            MaxBytes = 1234
        };

        string[] args = SessionHelper.BuildInspectArguments(request, uia: false, ocr: true).ToArray();

        Assert.True(SessionHelper.IsInvocation(args));
        Assert.Equal(SessionHelper.InspectScreenCommand, args[1]);
        Assert.True(SessionHelper.TryParseInspect(args, out ScreenCaptureRequest parsed, out bool uia, out bool ocr, out string? error));
        Assert.Null(error);
        Assert.False(uia);
        Assert.True(ocr);
        Assert.Equal(new DesktopRect(-10, 20, 640, 480), parsed.Region);
        Assert.Equal("Notepad.*", parsed.TitlePattern);
        Assert.True(parsed.IncludeFullImage);
        Assert.Equal(0, parsed.MaxEdge);

        Assert.False(SessionHelper.TryParseInspect([SessionHelper.Flag, SessionHelper.InspectScreenCommand, "--uia", "maybe"], out _, out _, out _, out _));
        Assert.False(SessionHelper.TryParseInspect([SessionHelper.Flag, SessionHelper.InspectScreenCommand, "--bogus", "1"], out _, out _, out _, out _));
    }

    [Fact]
    public async Task PerceptionTools_RefuseWhenDisabledOrInvalid()
    {
        PerceptionTools disabled = CreateTools(new VestigoOptions());
        Assert.Equal("screenshots_disabled", InputTests.ErrorCode(await disabled.InspectScreen()));
        Assert.Equal("screenshots_disabled", InputTests.ErrorCode(await disabled.FindOnScreen(text: "OK")));

        VestigoOptions enabled = new() { Desktop = { AllowScreenshots = true } };
        PerceptionTools tools = CreateTools(enabled);
        Assert.Equal("invalid_request", InputTests.ErrorCode(await tools.InspectScreen(target: "camera")));
        Assert.Equal("invalid_request", InputTests.ErrorCode(await tools.InspectScreen(imageMode: "gif")));
        Assert.Equal("invalid_request", InputTests.ErrorCode(await tools.InspectScreen(maxEdge: 10)));
        Assert.Equal("invalid_request", InputTests.ErrorCode(await tools.InspectScreen(target: "region", x: 0, y: 0)));
        Assert.Equal("invalid_request", InputTests.ErrorCode(await tools.InspectScreen(windowHandle: "0x1", pid: 4)));
        Assert.Equal("capture_expired", InputTests.ErrorCode(await tools.InspectScreen(diffAgainst: "cap-none")));
        Assert.Equal("no_interactive_session", InputTests.ErrorCode(await tools.InspectScreen()));

        Assert.Equal("invalid_request", InputTests.ErrorCode(await tools.FindOnScreen()));
        Assert.Equal("invalid_request", InputTests.ErrorCode(await tools.FindOnScreen(text: "x", template: "a:b.png", templateCaptureId: "cap-1")));
        Assert.Equal("invalid_request", InputTests.ErrorCode(await tools.FindOnScreen(text: "x", minConfidence: 0.1)));
        Assert.Equal("capture_expired", InputTests.ErrorCode(await tools.FindOnScreen(templateCaptureId: "cap-none", templateX: 0, templateY: 0, templateWidth: 5, templateHeight: 5)));
        Assert.Equal("no_interactive_session", InputTests.ErrorCode(await tools.FindOnScreen(text: "OK")));
    }

    [Fact]
    public void Describe_ReturnsImageAndTextOnlyElementList()
    {
        CaptureCache cache = new();
        CaptureRecord record = cache.Add(
            new DesktopRect(0, 0, 200, 100),
            new DesktopSize(100, 50),
            [new ScreenElement(1, ElementSource.Uia, "button", "OK", null, new DesktopRect(20, 20, 40, 20), true)],
            [1, 2, 3]);
        PerceptionOutcome outcome = new(
            record,
            new ScreenCaptureResult(ScreenCaptureTarget.Desktop, record.Bounds, new DesktopSize(200, 100), null, 96, ["a"], null, null, null, null, 1),
            [9, 9],
            "ok",
            "ok",
            null,
            new SourceCounts(1, 0, 0),
            new SourceCounts(0, 0, 0),
            0);

        CallToolResult annotated = PerceptionTools.Describe(outcome, PerceptionTools.ImageAnnotated, null);
        Assert.Equal(2, annotated.Content.Count);
        Assert.Equal([9, 9], Assert.IsType<ImageContentBlock>(annotated.Content[0]).DecodedData.ToArray());
        using JsonDocument document = JsonDocument.Parse(Assert.IsType<TextContentBlock>(annotated.Content[1]).Text);
        JsonElement element = document.RootElement.GetProperty("elements")[0];
        Assert.Equal(record.Id, document.RootElement.GetProperty("captureId").GetString());
        Assert.False(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, document.RootElement.GetProperty("candidates").GetProperty("uia").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("omitted").GetProperty("ocr").GetInt32());
        Assert.Equal("OK", element.GetProperty("name").GetString());
        // The client sees the image, so boxes are in image pixels (half of the physical ones).
        Assert.Equal(10, element.GetProperty("box").GetProperty("x").GetInt32());
        Assert.Equal(20, element.GetProperty("box").GetProperty("width").GetInt32());
        Assert.False(element.TryGetProperty("text", out _));

        CallToolResult raw = PerceptionTools.Describe(outcome, PerceptionTools.ImageRaw, null);
        Assert.Equal([1, 2, 3], Assert.IsType<ImageContentBlock>(raw.Content[0]).DecodedData.ToArray());
        CallToolResult none = PerceptionTools.Describe(outcome, PerceptionTools.ImageNone, null);
        Assert.IsType<TextContentBlock>(Assert.Single(none.Content));
    }

    [Fact]
    public void Describe_ReportsOmittedElements()
    {
        CaptureCache cache = new();
        CaptureRecord record = cache.Add(new DesktopRect(0, 0, 10, 10), new DesktopSize(10, 10), [], [1]);
        PerceptionOutcome outcome = new(
            record,
            new ScreenCaptureResult(ScreenCaptureTarget.Desktop, record.Bounds, new DesktopSize(10, 10), null, 96, ["a"], null, null, null, null, 1),
            null,
            "truncated",
            "ok",
            null,
            new SourceCounts(180, 40, 3),
            new SourceCounts(30, 0, 3),
            0);

        CallToolResult result = PerceptionTools.Describe(outcome, PerceptionTools.ImageNone, null);
        using JsonDocument document = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(180, document.RootElement.GetProperty("candidates").GetProperty("uia").GetInt32());
        Assert.Equal(40, document.RootElement.GetProperty("candidates").GetProperty("ocr").GetInt32());
        Assert.Equal(3, document.RootElement.GetProperty("candidates").GetProperty("cv").GetInt32());
        Assert.Equal(30, document.RootElement.GetProperty("omitted").GetProperty("uia").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("omitted").GetProperty("ocr").GetInt32());
        Assert.Equal(3, document.RootElement.GetProperty("omitted").GetProperty("cv").GetInt32());
    }

    [Fact]
    public void ScreenAnalyzer_AnnotatesAndFindsRegions()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The OpenCvSharp native runtime in this repository is Windows-only.");

        byte[] png = Scene(rectX: 60, rectY: 40);
        DesktopRect bounds = new(1000, 500, 640, 480);
        ScreenElement uia = Uia("button", "Go", new DesktopRect(1000 + 300, 500 + 300, 80, 30));

        AnalysisResult result = ScreenAnalyzer.Analyze(png, bounds, [uia], [], 320, grid: false, annotate: true, priority: null);

        Assert.Equal(new DesktopSize(320, 240), result.ImageSize);
        Assert.NotNull(result.AnnotatedPng);
        Assert.NotEqual(result.WorkingPng, result.AnnotatedPng);
        Assert.Contains(result.Elements, item => item.Source == ElementSource.Uia && item.Name == "Go");
        ScreenElement region = Assert.Single(result.Elements, item => item.Source == ElementSource.Cv);
        // The contour of the drawn rectangle, mapped back to physical pixels.
        Assert.InRange(region.Box.X, 1000 + 50, 1000 + 70);
        Assert.InRange(region.Box.Width, 100, 150);

        AnalysisResult grid = ScreenAnalyzer.Analyze(png, bounds, [], [], 640, grid: true, annotate: true, priority: null);
        Assert.NotNull(grid.AnnotatedPng);
        AnalysisResult plain = ScreenAnalyzer.Analyze(png, bounds, [], [], 640, grid: false, annotate: false, priority: null);
        Assert.Null(plain.AnnotatedPng);
    }

    [Fact]
    public void ScreenAnalyzer_DiffsAndMatchesTemplates()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The OpenCvSharp native runtime in this repository is Windows-only.");

        byte[] before = Scene(rectX: 60, rectY: 40);
        byte[] after = Scene(rectX: 300, rectY: 300);

        DiffResult same = ScreenAnalyzer.Diff(before, before);
        Assert.Empty(same.Regions);
        Assert.Equal(0, same.ChangedPercent);

        DiffResult changed = ScreenAnalyzer.Diff(before, after);
        Assert.NotEmpty(changed.Regions);
        Assert.InRange(changed.ChangedPercent, 0.5, 50);
        Assert.Throws<ArgumentException>(() => ScreenAnalyzer.Diff(before, Scene(rectX: 1, rectY: 1, width: 100, height: 100)));

        byte[]? template = ScreenAnalyzer.Crop(before, new DesktopRect(50, 30, 120, 90));
        Assert.NotNull(template);
        IReadOnlyList<TemplateMatch> matches = ScreenAnalyzer.FindTemplate(before, template, 0.9);
        TemplateMatch best = matches[0];
        Assert.InRange(best.Box.X, 45, 55);
        Assert.InRange(best.Box.Y, 25, 35);
        Assert.True(best.Confidence >= 0.9);
        Assert.Empty(ScreenAnalyzer.FindTemplate(after, template, 0.99));
        Assert.Null(ScreenAnalyzer.Crop(before, new DesktopRect(5000, 5000, 10, 10)));
    }

    private static byte[] Scene(int rectX, int rectY, int width = 640, int height = 480)
    {
        using Mat image = new(height, width, MatType.CV_8UC3, new Scalar(255, 255, 255));
        // A filled block with a distinctive inner mark, so contour and template matching have something to find.
        Cv2.Rectangle(image, new Rect(rectX, rectY, 120, 90), new Scalar(40, 40, 40), -1);
        Cv2.Rectangle(image, new Rect(rectX + 20, rectY + 20, 40, 20), new Scalar(0, 200, 255), -1);
        Cv2.Circle(image, new Point(rectX + 90, rectY + 60), 15, new Scalar(255, 80, 0), -1);
        Cv2.ImEncode(".png", image, out byte[] png);
        return png;
    }

    private static PerceptionTools CreateTools(VestigoOptions options)
    {
        InputTests.UnavailableLauncher launcher = new();
        PerceptionService service = new(launcher, new SessionHelperClient(launcher), new CaptureCache(), options);
        return new PerceptionTools(service, options, new RootPolicy(options));
    }

    private static bool MatchesDownload(ScreenElement element) => Matches(element, "download");

    private static bool Matches(ScreenElement element, string text) =>
        (element.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
        || (element.Text?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);

    private static ScreenElement Uia(string role, string name, DesktopRect box) => new(0, ElementSource.Uia, role, name, null, box);

    private static ScreenElement Ocr(string text, DesktopRect box) => new(0, ElementSource.Ocr, "text", null, text, box);

    private static ScreenElement Cv(DesktopRect box) => new(0, ElementSource.Cv, "region", null, null, box);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
