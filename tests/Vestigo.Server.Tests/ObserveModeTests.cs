using System.Text;
using System.Text.Json;

using ModelContextProtocol.Protocol;

using Vestigo.Configuration;
using Vestigo.Desktop;
using Vestigo.Input;
using Vestigo.Processes;
using Vestigo.Vision;

namespace Vestigo.Server.Tests;

/// <summary>
///     Contract tests for <c>send_input</c> <c>observeMode</c> and the observation crop. Each case runs with the
///     default reused inspection helper and with <c>VESTIGO_OBSERVE_REUSE_HELPER=0</c>. Helper output and image
///     analysis are scripted, so these exercise the service logic on any OS; the OpenCV path has its own tests.
/// </summary>
public class ObserveModeTests
{
    private const string Window = "0x20";

    private static readonly DesktopRect FullWindow = new(100, 100, 800, 600);

    [Theory]
    [InlineData(null, true)]
    [InlineData(null, false)]
    [InlineData("full", true)]
    [InlineData("full", false)]
    [InlineData(" FULL ", true)]
    [InlineData(" FULL ", false)]
    public async Task Full_KeepsUiaOcrElementsAndTheDefaultWait(string? mode, bool reuse)
    {
        Rig rig = new(reuse);
        rig.Frames.AddRange([Frame(1, element: "ready"), Frame(1, element: "changed")]);

        JsonElement result = await rig.Send(mode is null ? null : mode);

        rig.AssertCaptures(2, (_, uia, ocr) => Assert.True(uia && ocr));
        Assert.Equal([TimeSpan.FromMilliseconds(300)], rig.Clock.Delays);
        // Same pixels, different elements: the full classification still sees the element change.
        Assert.Equal("observed", Response(result).GetProperty("status").GetString());
        Assert.Equal("window", Response(result).GetProperty("compared").GetString());
        Assert.Equal([false, false], rig.Annotated);
    }

    [Theory]
    [InlineData("none", false, true)]
    [InlineData("none", false, false)]
    [InlineData("raw", false, true)]
    [InlineData("raw", false, false)]
    [InlineData("annotated", true, true)]
    [InlineData("annotated", true, false)]
    public async Task Pixels_SkipsUiaAndOcrInBothPhasesWhateverTheImageMode(string imageMode, bool annotatesAfter, bool reuse)
    {
        Rig rig = new(reuse);
        rig.Frames.AddRange([Frame(1), Frame(2)]);

        JsonElement result = await rig.Send("pixels", imageMode: imageMode, delay: 0);

        rig.AssertCaptures(2, (_, uia, ocr) => Assert.False(uia || ocr));
        Assert.Equal([false, annotatesAfter], rig.Annotated);
        Assert.Equal("observed", Response(result).GetProperty("status").GetString());
        Assert.Equal("window-pixels", Response(result).GetProperty("compared").GetString());
        Assert.Empty(rig.Clock.Delays);
        using JsonDocument described = Described(result);
        Assert.Equal("skipped", described.RootElement.GetProperty("uia").GetString());
        Assert.Equal("skipped", described.RootElement.GetProperty("ocr").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pixels_ReportsNoResponseForAnUnchangedFrameEvenWhenElementsDiffer(bool reuse)
    {
        Rig rig = new(reuse);
        rig.Frames.AddRange([Frame(1), Frame(1)]);

        JsonElement result = await rig.Send("pixels", delay: 0);

        Assert.Equal("no-response", Response(result).GetProperty("status").GetString());
        Assert.Equal(InputInjectionStatus.Submitted, Injection(result).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pixels_DelayedUpdateNeedsTheExplicitWait(bool reuse)
    {
        Rig immediate = new(reuse);
        immediate.MarkerFor = (index, rig) => index == 0 ? (byte)1 : rig.Clock.Delays.Count > 0 ? (byte)2 : (byte)1;
        immediate.Frames.AddRange([Frame(0), Frame(0)]);
        JsonElement missed = await immediate.Send("pixels", delay: 0);
        Assert.Equal("no-response", Response(missed).GetProperty("status").GetString());

        Rig waited = new(reuse);
        waited.MarkerFor = immediate.MarkerFor;
        waited.Frames.AddRange([Frame(0), Frame(0)]);
        JsonElement seen = await waited.Send("pixels", delay: 400);
        Assert.Equal("observed", Response(seen).GetProperty("status").GetString());
        Assert.Equal([TimeSpan.FromMilliseconds(400)], waited.Clock.Delays);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pixels_IncompatibleCropIsInconclusiveWithoutRetryAndKeepsTheInjection(bool reuse)
    {
        DesktopRect first = new(120, 116, 100, 50);
        DesktopRect second = new(100, 100, 100, 50);
        Rig rig = new(reuse);
        rig.Frames.AddRange([Frame(1, bounds: first, crop: new DesktopRect(20, 16, 100, 50)), Frame(2, bounds: second, crop: new DesktopRect(0, 0, 100, 50))]);

        JsonElement result = await rig.Send("pixels", delay: 0, crop: (20, 16, 100, 50));

        rig.AssertCaptures(2);
        Assert.Equal("inconclusive", Response(result).GetProperty("status").GetString());
        Assert.Equal("capture_mismatch", Root(result).GetProperty("observeError").GetProperty("errorCode").GetString());
        JsonElement injection = Injection(result);
        Assert.Equal(InputInjectionStatus.Submitted, injection.GetProperty("status").GetString());
        Assert.Equal(2, injection.GetProperty("submittedEvents").GetInt32());
        Assert.Equal("9007199254740997", injection.GetProperty("timing").GetProperty("firstSubmitted").GetProperty("qpcTicks").GetString());
    }

    [Theory]
    [InlineData("bogus", null, null, null, null, Window)]
    [InlineData("pixels", 1, null, null, null, Window)]
    [InlineData("pixels", 1, 1, 0, 10, Window)]
    [InlineData("pixels", 1, 1, 10, -1, Window)]
    [InlineData("pixels", 1, 1, 10, 10, null)]
    [InlineData("full", 1, 1, 10, 10, "  ")]
    public async Task InvalidObserveArguments_AreRefusedBeforeAnyHelperRuns(string mode, int? x, int? y, int? width, int? height, string? handle)
    {
        foreach (bool reuse in new[] { true, false })
        {
            await Refuse(reuse);
        }

        async Task Refuse(bool reuse)
        {
            Rig rig = new(reuse);

            CallToolResult result = await rig.Tools.SendInput(
                [new InputActionSpec { Type = "key", Keys = "a" }],
                observe: true,
                observeMode: mode,
                observeWindowHandle: handle,
                observeCropX: x,
                observeCropY: y,
                observeCropWidth: width,
                observeCropHeight: height,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("invalid_request", InputTests.ErrorCode(result));
            Assert.Empty(rig.Launcher.Commands);
            Assert.Null(rig.Session);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Crop_AppliesToBothInspectionsAndTheCaptureMapsToTheEffectiveCrop(bool reuse)
    {
        // The window moved between the inspections; the same crop of the same window still compares.
        DesktopRect movedWindow = new(300, 200, 800, 600);
        DesktopRect crop = new(20, 16, 100, 50);
        Rig rig = new(reuse);
        rig.Frames.AddRange([
            Frame(1, bounds: new DesktopRect(120, 116, 100, 50), crop: crop, window: FullWindow),
            Frame(1, bounds: new DesktopRect(320, 216, 100, 50), crop: crop, window: movedWindow)]);

        JsonElement result = await rig.Send("pixels", delay: 0, crop: (20, 16, 100, 50));

        Assert.Equal("no-response", Response(result).GetProperty("status").GetString());
        Assert.Equal("window-crop-pixels", Response(result).GetProperty("compared").GetString());
        rig.AssertCaptures(2, (request, uia, ocr) =>
        {
            Assert.Equal(new DesktopRect(20, 16, 100, 50), request.WindowCrop);
            Assert.Equal(Window, request.WindowHandle);
            Assert.False(uia || ocr);
        });

        using JsonDocument described = Described(result);
        Assert.Equal(20, described.RootElement.GetProperty("effectiveCrop").GetProperty("x").GetInt32());
        Assert.Equal(300, described.RootElement.GetProperty("windowBounds").GetProperty("x").GetInt32());
        string captureId = described.RootElement.GetProperty("captureId").GetString()!;
        OperationResult<InputBatch> plan = InputPlanner.Plan(
            [new InputActionSpec { Type = "click", X = 50, Y = 25 }],
            captureId,
            null,
            rig.Cache,
            new InputOptions());
        Assert.True(plan.Success, plan.Error);
        // Image pixel (50,25) of the crop is screen pixel (320+50, 216+25), not an offset from the window origin.
        Assert.Equal((370, 241), (plan.Value!.Steps[0].X, plan.Value.Steps[0].Y));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Crop_PartialOverlapIsClippedAndReported(bool reuse)
    {
        DesktopRect clipped = new(700, 500, 100, 100);
        Rig rig = new(reuse);
        rig.Frames.AddRange([
            Frame(1, bounds: new DesktopRect(800, 600, 100, 100), crop: clipped, window: FullWindow),
            Frame(2, bounds: new DesktopRect(800, 600, 100, 100), crop: clipped, window: FullWindow)]);

        JsonElement result = await rig.Send("pixels", delay: 0, crop: (700, 500, 400, 400));

        Assert.Equal("observed", Response(result).GetProperty("status").GetString());
        using JsonDocument described = Described(result);
        JsonElement effective = described.RootElement.GetProperty("effectiveCrop");
        Assert.Equal((700, 500, 100, 100), (effective.GetProperty("x").GetInt32(), effective.GetProperty("y").GetInt32(), effective.GetProperty("width").GetInt32(), effective.GetProperty("height").GetInt32()));
        // The request carries the unclipped crop; clipping is the capture helper's job and is reported back.
        rig.AssertCaptures(2, (request, _, _) => Assert.Equal(new DesktopRect(700, 500, 400, 400), request.WindowCrop));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Crop_DoesNotChangeInputTargeting(bool reuse)
    {
        Rig rig = new(reuse);
        rig.Frames.AddRange([Frame(1), Frame(1)]);

        await rig.Send("pixels", delay: 0, crop: (20, 16, 100, 50), actions: [new InputActionSpec { Type = "click", X = 400, Y = 300 }]);

        string batch = Encoding.UTF8.GetString(Convert.FromBase64String(rig.Launcher.Commands.Single(command => command.Contains("send-input", StringComparison.Ordinal)).Split(' ').Last()));
        using JsonDocument document = JsonDocument.Parse(batch);
        JsonElement step = document.RootElement.GetProperty("steps")[0];
        Assert.Equal(400, step.GetProperty("x").GetInt32());
        Assert.Equal(300, step.GetProperty("y").GetInt32());
        Assert.True(document.RootElement.GetProperty("requireFocus").GetBoolean());
    }

    [Theory]
    [InlineData("full", false, true)]
    [InlineData("full", false, false)]
    [InlineData("pixels", true, true)]
    [InlineData("pixels", true, false)]
    public async Task StaleWindowRejection_SubmitsNothingAndSkipsTheAfterInspection(string mode, bool crop, bool reuse)
    {
        Rig rig = new(reuse) { SendResult = Rejected() };
        rig.Frames.Add(Frame(1));

        JsonElement result = await rig.Send(mode, delay: 2000, crop: crop ? (20, 16, 100, 50) : null);

        Assert.Equal(InputInjectionStatus.Rejected, Injection(result).GetProperty("status").GetString());
        Assert.Equal(0, Injection(result).GetProperty("submittedEvents").GetInt32());
        Assert.Equal("skipped", Response(result).GetProperty("status").GetString());
        Assert.Equal("input_rejected", Response(result).GetProperty("reason").GetString());
        rig.AssertCaptures(1);
        Assert.Empty(rig.Clock.Delays);
        Assert.Equal(1, rig.SendCount);
        Assert.Contains("\"requireFocus\":true", Encoding.UTF8.GetString(Convert.FromBase64String(rig.SendCommand.Split(' ').Last())), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("full", true)]
    [InlineData("full", false)]
    [InlineData("pixels", true)]
    [InlineData("pixels", false)]
    public async Task ObservationFailureAfterAcceptedInput_PreservesTheInjection(string mode, bool reuse)
    {
        Rig rig = new(reuse);
        rig.Frames.AddRange([Frame(1), Frame(2, error: "empty_region")]);

        JsonElement result = await rig.Send(mode, delay: 0, crop: (20, 16, 100, 50));

        Assert.Equal("inconclusive", Response(result).GetProperty("status").GetString());
        Assert.Equal("empty_region", Root(result).GetProperty("observeError").GetProperty("errorCode").GetString());
        JsonElement injection = Injection(result);
        Assert.Equal(InputInjectionStatus.Submitted, injection.GetProperty("status").GetString());
        Assert.Equal(2, injection.GetProperty("submittedEvents").GetInt32());
        Assert.True(injection.TryGetProperty("timing", out _));
        Assert.Equal(1, result.GetProperty("content").GetArrayLength());
        rig.AssertCaptures(2);
        Assert.Equal(1, rig.SendCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Full_WindowMoveRetriesOnceWithoutSendingInputAgain(bool reuse)
    {
        Rig rig = new(reuse);
        DesktopRect moved = new(300, 200, 800, 600);
        rig.Frames.AddRange([
            Frame(1, bounds: FullWindow),
            Frame(1, bounds: moved),
            Frame(1, bounds: moved)]);

        JsonElement result = await rig.Send("full", delay: 0);

        Assert.Equal("observed", Response(result).GetProperty("status").GetString());
        Assert.Equal("window", Response(result).GetProperty("compared").GetString());
        rig.AssertCaptures(3, (capture, uia, ocr) =>
        {
            Assert.Null(capture.WindowCrop);
            Assert.True(uia && ocr);
        });
        Assert.Equal(1, rig.SendCount);
        JsonElement injection = Injection(result);
        Assert.Equal(InputInjectionStatus.Submitted, injection.GetProperty("status").GetString());
        Assert.Equal("9007199254740997", injection.GetProperty("timing").GetProperty("firstSubmitted").GetProperty("qpcTicks").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WindowClose_PreservesTheInjectionAndDoesNotReplay(bool reuse)
    {
        Rig rig = new(reuse);
        rig.Frames.AddRange([Frame(1), Frame(2, error: "window_not_found")]);

        JsonElement result = await rig.Send("pixels", delay: 0, crop: (20, 16, 100, 50));

        Assert.Equal("inconclusive", Response(result).GetProperty("status").GetString());
        Assert.Equal("window_not_found", Root(result).GetProperty("observeError").GetProperty("errorCode").GetString());
        JsonElement injection = Injection(result);
        Assert.Equal(InputInjectionStatus.Submitted, injection.GetProperty("status").GetString());
        Assert.Equal(2, injection.GetProperty("submittedEvents").GetInt32());
        Assert.True(injection.TryGetProperty("timing", out _));
        Assert.Equal(1, result.GetProperty("content").GetArrayLength());
        rig.AssertCaptures(2);
        Assert.Equal(1, rig.SendCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartialSubmission_KeepsCleanupWhenTheLaterCaptureChanges(bool reuse)
    {
        Rig rig = new(reuse) { SendResult = Partial() };
        rig.Frames.AddRange([Frame(1), Frame(2)]);

        JsonElement result = await rig.Send("pixels", delay: 0, crop: (20, 16, 100, 50));

        Assert.Equal("observed", Response(result).GetProperty("status").GetString());
        JsonElement injection = Injection(result);
        Assert.Equal(InputInjectionStatus.Partial, injection.GetProperty("status").GetString());
        Assert.Equal(InputStopCodes.FocusLost, injection.GetProperty("stopCode").GetString());
        Assert.Equal(1, injection.GetProperty("cleanupReleasedEvents").GetInt32());
        Assert.True(injection.GetProperty("timing").GetProperty("includesCleanupReleases").GetBoolean());
        rig.AssertCaptures(2);
        Assert.Equal(1, rig.SendCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartialSubmission_KeepsCleanupWhenObservationFails(bool reuse)
    {
        Rig rig = new(reuse) { SendResult = Partial() };
        rig.Frames.AddRange([Frame(1), Frame(2, error: "helper_failed")]);

        JsonElement result = await rig.Send("full", delay: 0);

        Assert.Equal("inconclusive", Response(result).GetProperty("status").GetString());
        Assert.Equal("helper_failed", Root(result).GetProperty("observeError").GetProperty("errorCode").GetString());
        JsonElement injection = Injection(result);
        Assert.Equal(InputInjectionStatus.Partial, injection.GetProperty("status").GetString());
        Assert.Equal(1, injection.GetProperty("cleanupReleasedEvents").GetInt32());
        Assert.True(injection.GetProperty("timing").GetProperty("includesCleanupReleases").GetBoolean());
        Assert.Equal(1, result.GetProperty("content").GetArrayLength());
        rig.AssertCaptures(2);
        Assert.Equal(1, rig.SendCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ObserveOptions_AreIgnoredWithoutObserve(bool reuse)
    {
        Rig rig = new(reuse);

        CallToolResult result = await rig.Tools.SendInput(
            [new InputActionSpec { Type = "key", Keys = "a" }],
            observeMode: "nonsense",
            observeCropX: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError is not true);
        Assert.Equal(1, rig.SendCount);
        Assert.Equal(0, rig.Launcher.InspectScreenCount);
        Assert.Equal(0, rig.Launcher.InspectSessionCount);
        Assert.Null(rig.Session);
    }

    private static JsonElement Root(JsonElement result)
    {
        using JsonDocument document = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        return document.RootElement.Clone();
    }

    private static JsonElement Response(JsonElement result) => Root(result).GetProperty("response");

    private static JsonElement Injection(JsonElement result) => Root(result).GetProperty("injection");

    private static JsonDocument Described(JsonElement result)
    {
        JsonElement content = result.GetProperty("content");
        return JsonDocument.Parse(content[content.GetArrayLength() - 1].GetProperty("text").GetString()!);
    }

    private static string[] Arguments(string joined)
    {
        string[] all = joined.Split(' ');
        return all[Array.IndexOf(all, SessionHelper.Flag)..];
    }

    private static Scripted Frame(
        byte marker,
        DesktopRect? bounds = null,
        DesktopRect? crop = null,
        DesktopRect? window = null,
        string element = "label",
        string? error = null) =>
        new(marker, bounds ?? FullWindow, crop, crop is null ? null : window ?? FullWindow, element, error);

    private static InputResult Rejected() => new(
        0,
        7,
        null,
        0,
        "0x20",
        "0xB2",
        false,
        Status: InputInjectionStatus.Rejected,
        StopCode: InputStopCodes.FocusNotGranted,
        StopReason: "The requested window was not the foreground window immediately before input submission.",
        StoppedAtActionIndex: 0,
        StoppedAtActionKind: InputKinds.Key);

    private static InputResult Partial() => new(
        0,
        7,
        null,
        2,
        "0x20",
        "0xB2",
        false,
        Status: InputInjectionStatus.Partial,
        StopCode: InputStopCodes.FocusLost,
        StopReason: "The requested window was no longer the foreground window. Later actions were not submitted.",
        StoppedAtActionIndex: 0,
        StoppedAtActionKind: InputKinds.Key,
        CleanupReleasedEvents: 1,
        Timing: new InputTiming(
            InputTiming.QueryPerformanceCounter,
            "10000000",
            true,
            new InputTimestamp(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), "9007199254740997"),
            new InputTimestamp(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(40), "9007199254740999")));

    private static InputResult Submitted() => new(
        1,
        7,
        null,
        2,
        null,
        "0x20",
        true,
        3,
        4,
        Timing: new InputTiming(
            InputTiming.QueryPerformanceCounter,
            "10000000",
            false,
            new InputTimestamp(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), "9007199254740997"),
            new InputTimestamp(new DateTimeOffset(2026, 3, 1, 0, 0, 1, TimeSpan.Zero), "9007199254740999")));

    private sealed record Scripted(byte Marker, DesktopRect Bounds, DesktopRect? Crop, DesktopRect? Window, string Element, string? Error);

    private sealed class Rig
    {
        public Rig(bool reuse)
        {
            Reuse = reuse;
            VestigoOptions options = new() { Input = { AllowInput = true }, Desktop = { AllowScreenshots = true } };
            Launcher = new ScriptedLauncher(this);
            SessionHelperClient helper = new(Launcher);
            Cache = new CaptureCache();
            PerceptionService perception = new(Launcher, helper, Cache, options)
            {
                Analyzer = (png, bounds, uia, ocr, _, _, annotate, _) =>
                {
                    Annotated.Add(annotate);
                    return new AnalysisResult(
                        [.. uia.Select((item, index) => item with { Id = index + 1 }), .. ocr],
                        new DesktopSize(bounds.Width, bounds.Height),
                        png,
                        annotate ? [9] : null,
                        new SourceCounts(uia.Count, ocr.Count, 0),
                        new SourceCounts(0, 0, 0),
                        0);
                },
                Differ = (before, after) => before.SequenceEqual(after)
                    ? new DiffResult([], 0)
                    : new DiffResult([new ChangedRegion(new DesktopRect(0, 0, 10, 10))], 4.5)
            };
            Tools = new InputTools(helper, Launcher, perception, options, clock: Clock)
            {
                ActiveConsoleSessionOverride = () => 7
            };
            if (reuse)
            {
                Tools.ReuseHelperVariableOverride = () => null;
                Tools.InspectSessionFactory = _ => Task.FromResult(OpenSession());
            }
            else
            {
                Tools.ReuseHelperVariableOverride = () => "0";
            }
        }

        public bool Reuse { get; }

        public ScriptedInspection? Session { get; private set; }

        public List<bool> Annotated { get; } = [];

        public List<Scripted> Frames { get; } = [];

        public CaptureCache Cache { get; }

        public RecordingClock Clock { get; } = new();

        public ScriptedLauncher Launcher { get; }

        public InputTools Tools { get; }

        public InputResult SendResult { get; set; } = Submitted();

        public Func<int, Rig, byte>? MarkerFor { get; set; }

        public int Served { get; set; }

        public int SendCount => Launcher.Commands.Count(command => command.Contains("send-input", StringComparison.Ordinal));

        public string SendCommand => Launcher.Commands.Single(command => command.Contains("send-input", StringComparison.Ordinal));

        public void AssertCaptures(int count, Action<ScreenCaptureRequest, bool, bool>? assert = null)
        {
            if (Reuse)
            {
                Assert.NotNull(Session);
                Assert.Equal(count, Session.Calls);
                Assert.Equal(0, Launcher.InspectScreenCount);
                Assert.Equal(0, Launcher.InspectSessionCount);
                Assert.True(Session.Disposed);
                if (assert is not null)
                {
                    foreach ((ScreenCaptureRequest capture, bool uia, bool ocr) in Session.Requests)
                    {
                        assert(capture, uia, ocr);
                    }
                }
            }
            else
            {
                Assert.Null(Session);
                Assert.Equal(count, Launcher.InspectScreenCount);
                if (assert is not null)
                {
                    foreach (string inspect in Launcher.Commands.Where(command => command.Contains("inspect-screen", StringComparison.Ordinal)))
                    {
                        Assert.True(SessionHelper.TryParseInspect(Arguments(inspect), out ScreenCaptureRequest request, out bool uia, out bool ocr, out string? error), error);
                        assert(request, uia, ocr);
                    }
                }
            }
        }

        public OperationResult<IInspectCaptureSession> OpenSession()
        {
            Session = new ScriptedInspection(this);
            return OperationResult<IInspectCaptureSession>.Ok(Session);
        }

        public InspectCaptureResult ResultFor(int index, bool uia)
        {
            Scripted frame = Frames[index];
            byte marker = MarkerFor?.Invoke(index, this) ?? frame.Marker;
            return new InspectCaptureResult(
                new ScreenCaptureResult(
                    ScreenCaptureTarget.Window,
                    frame.Bounds,
                    new DesktopSize(frame.Bounds.Width, frame.Bounds.Height),
                    null,
                    96,
                    ["a"],
                    Window,
                    "Fixture",
                    null,
                    Convert.ToBase64String([marker]),
                    7,
                    WindowBounds: frame.Window,
                    EffectiveCrop: frame.Crop),
                uia ? [new ScreenElement(0, ElementSource.Uia, "text", frame.Element, null, new DesktopRect(frame.Bounds.X, frame.Bounds.Y, 10, 10))] : [],
                uia ? "ok" : "skipped",
                uia ? "ok" : "skipped");
        }

        public async Task<JsonElement> Send(
            string? mode,
            string imageMode = "none",
            int delay = 300,
            (int X, int Y, int Width, int Height)? crop = null,
            List<InputActionSpec>? actions = null)
        {
            CallToolResult result = await Tools.SendInput(
                actions ?? [new InputActionSpec { Type = "key", Keys = "f8" }],
                focusWindow: Window,
                requireFocus: true,
                observe: true,
                observeDelayMs: delay,
                observeWindowHandle: Window,
                imageMode: imageMode,
                observeMode: mode!,
                observeCropX: crop?.X,
                observeCropY: crop?.Y,
                observeCropWidth: crop?.Width,
                observeCropHeight: crop?.Height,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.IsError is not true, JsonSerializer.Serialize(result.Content.Select(block => (block as TextContentBlock)?.Text)));
            return JsonSerializer.SerializeToElement(new
            {
                content = result.Content.Select(block => new { type = block is TextContentBlock ? "text" : "image", text = (block as TextContentBlock)?.Text })
            });
        }
    }

    private sealed class ScriptedInspection(Rig rig) : IInspectCaptureSession
    {
        public int Calls { get; private set; }

        public bool Disposed { get; private set; }

        public List<(ScreenCaptureRequest Capture, bool Uia, bool Ocr)> Requests { get; } = [];

        public Task<OperationResult<InspectCaptureResult>> InspectAsync(
            ScreenCaptureRequest capture,
            bool uia,
            bool ocr,
            TimeSpan timeout,
            long maxOutputBytes,
            CancellationToken cancellationToken)
        {
            int index = Calls++;
            Requests.Add((capture, uia, ocr));
            cancellationToken.ThrowIfCancellationRequested();
            Scripted frame = rig.Frames[index];
            return Task.FromResult(frame.Error is null
                ? OperationResult<InspectCaptureResult>.Ok(rig.ResultFor(index, uia))
                : OperationResult<InspectCaptureResult>.Fail(frame.Error, "scripted"));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedLauncher(Rig rig) : IProcessLauncher
    {
        public List<string> Commands { get; } = [];

        public int InspectScreenCount => Commands.Count(command => command.Contains("inspect-screen", StringComparison.Ordinal));

        public int InspectSessionCount => Commands.Count(command => command.Contains("inspect-session", StringComparison.Ordinal));

        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) => OperationResult<bool>.Ok(true);

        public Task<int> RunAsync(LaunchRequest request, Vestigo.Jobs.JobContext output, CancellationToken cancellationToken)
        {
            string command = string.Join(' ', request.Arguments);
            Commands.Add(command);
            if (command.Contains("send-input", StringComparison.Ordinal))
            {
                File.WriteAllText(output.OutputPath, JsonSerializer.Serialize(rig.SendResult, SessionHelper.Json));
                return Task.FromResult(0);
            }

            int index = rig.Served++;
            Scripted frame = rig.Frames[index];
            if (frame.Error is not null)
            {
                File.WriteAllText(output.OutputPath, JsonSerializer.Serialize(new { error = "scripted", errorCode = frame.Error }));
                return Task.FromResult(2);
            }

            bool uia = command.Contains("--uia true", StringComparison.Ordinal);
            File.WriteAllText(output.OutputPath, JsonSerializer.Serialize(rig.ResultFor(index, uia), SessionHelper.Json));
            return Task.FromResult(0);
        }
    }

    private sealed class RecordingClock : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime);
            callback(state);
            return new NoopTimer();
        }

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
