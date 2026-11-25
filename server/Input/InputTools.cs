using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Desktop;
using Vestigo.Processes;
using Vestigo.Vision;

namespace Vestigo.Input;

/// <summary>MCP tool that injects mouse and keyboard input into the console user's session.</summary>
[McpServerToolType]
public sealed class InputTools(
    SessionHelperClient helper,
    IProcessLauncher launcher,
    PerceptionService perception,
    VestigoOptions options,
    ILogger<InputTools>? logger = null,
    IManagedDesktopDirectory? desktops = null,
    InputSubmissionGate? submissions = null,
    TimeProvider? clock = null)
{
    private readonly InputSubmissionGate _submissions = submissions ?? new();

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private const long HelperMaxOutputBytes = 64 * 1024;

    /// <summary>
    ///     Experimental process switch. When it is <c>1</c>, one <c>observe: true</c> call keeps a single inspection
    ///     helper for both captures. This is not a <c>VestigoOptions</c> setting.
    /// </summary>
    internal const string ReuseHelperVariable = "VESTIGO_OBSERVE_REUSE_HELPER";

    /// <summary>Test hook that supplies the inspection helper instead of reading <see cref="ReuseHelperVariable"/>.</summary>
    internal Func<CancellationToken, Task<OperationResult<IInspectCaptureSession>>>? InspectSessionFactory { get; set; }

    private bool ReuseInspection =>
        InspectSessionFactory is not null
        || string.Equals(Environment.GetEnvironmentVariable(ReuseHelperVariable), "1", StringComparison.Ordinal);

    public const string ObserveFull = "full";

    public const string ObservePixels = "pixels";

    [McpServerTool(Name = "send_input", Destructive = true, OpenWorld = false)]
    [Description(
        "Injects mouse and keyboard input as the console user. actions run in order: move, click (button, count), down, up, scroll (deltaY notches down, deltaX right), "
        + "drag (x,y to toX,toY), type (text as Unicode characters by default; inputMode \"keys\" types virtual keys from the keyboard layout of the window that receives them; an unsupported character returns its position and sends nothing for that action; a different receiving-window layout during the action is layout_changed, and a Caps Lock change is caps_lock_changed; either sends no further characters), "
        + "key (a key or chord such as enter, ctrl+c, alt+f4, win+r) and wait (milliseconds). "
        + "A point is elementId from inspect_screen or find_on_screen, or x and y: in the pixels of the returned image when captureId is set, otherwise absolute virtual-screen physical pixels. "
        + "focusWindow (hex handle from list_windows) is brought to the foreground first. requireFocus defaults to false. When true, focusWindow is required and must be the foreground window in that session immediately before the first input submission and before every later submission, including after waits. "
        + "A mismatch or a closed window before any event is submitted returns injection.status rejected, the requested and actual HWNDs, and submittedEvents 0. "
        + "If focus is lost after events were submitted, the batch stops, injection.status is partial, and keys or buttons this batch still holds are released. "
        + "focusActual is the foreground window from the check that stopped the batch. Cleanup retries a release SendInput did not fully accept. If a held input remains down, stopReason reports the accepted release count. "
        + "Calls that target the same input desktop run one at a time until the helper exits, including those releases. "
        + "Windows can change the foreground between that check and SendInput, and a mouse click still goes to the window under the pointer. "
        + "injection.status submitted means SendInput accepted the events into the input stream. "
        + "injection.timing is captured in the helper around the first and last native submission that accepted events: utc, qpcTicks and qpcFrequency for the same host. Tick values are decimal strings so they stay exact. "
        + "The interval excludes helper startup, RPC, trailing waits and observe delays. Cleanup releases are inside the interval when includesCleanupReleases is true. A rejection before any submission omits timing. "
        + "click and key accept holdMilliseconds. Any action accepts delayAfterMilliseconds. "
        + "observe:true waits observeDelayMs, then compares a fresh inspect_screen result with one taken before the input, except when injection.status is rejected. "
        + "response.status observed means the screen, window, or elements changed. A submitted injection is not an observed application response. observe needs Vestigo:Desktop:AllowScreenshots. "
        + "observeMode 'full' (default) compares pixels, window bounds and title, and UI Automation and OCR elements. 'pixels' skips UI Automation and OCR in both inspections and compares pixels only: observed when they changed, no-response when they did not, inconclusive when the comparison failed. "
        + "observeCropX, observeCropY, observeCropWidth and observeCropHeight (all four, unscaled physical pixels relative to the window, observeWindowHandle required) crop both inspections the same way as inspect_screen, clip a partial overlap, and the returned captureId and image coordinates match the effective crop. The crop never changes where input goes. "
        + "desktopSessionId sends input to that managed desktop only when it is already the input desktop and does not activate it. "
        + "Cannot reach the secure desktop (UAC prompt, lock screen, Ctrl+Alt+Del) and cannot "
        + "reach elevated windows unless elevated=true. Requires Vestigo:Input:AllowInput. Interactive session only.")]
    public async Task<CallToolResult> SendInput(
        [Description("Actions to perform in order.")] List<InputActionSpec> actions,
        [Description("captureId that element ids and x,y refer to.")] string? captureId = null,
        [Description("Hex window handle from list_windows to focus first.")] string? focusWindow = null,
        [Description("When true, focusWindow must be the foreground window immediately before every submission. Default false.")] bool requireFocus = false,
        [Description("Return a fresh inspect_screen result after the actions.")] bool observe = false,
        [Description("Milliseconds to wait before observing, 0 to 5000. Default 300.")] int observeDelayMs = 300,
        [Description("Observe only: window handle to inspect instead of the whole desktop.")] string? observeWindowHandle = null,
        [Description("Observe only: 'annotated' (default), 'raw' or 'none'.")] string imageMode = PerceptionTools.ImageAnnotated,
        [Description("Observe only: longest edge of the returned image, 320 to 3840.")] int maxEdge = ScreenAnalyzer.DefaultMaxEdge,
        [Description("Observe only: 'full' (default) keeps OCR, UI Automation and window comparison. 'pixels' compares pixels only and skips OCR and UI Automation.")] string observeMode = ObserveFull,
        [Description("Observe only: window-relative crop left in unscaled physical pixels. Pass the other three crop fields and observeWindowHandle too. Does not affect input.")] int? observeCropX = null,
        [Description("Observe only: window-relative crop top in unscaled physical pixels.")] int? observeCropY = null,
        [Description("Observe only: window-relative crop width in physical pixels. Must be greater than zero.")] int? observeCropWidth = null,
        [Description("Observe only: window-relative crop height in physical pixels. Must be greater than zero.")] int? observeCropHeight = null,
        [Description("Use the elevated token, needed to reach elevated windows.")] bool elevated = false,
        [Description("Managed desktop session id. The session must already be the input desktop.")] string? desktopSessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (!options.Input.AllowInput)
        {
            return DesktopTools.ErrorResult("input_disabled", "Input injection is disabled. Set Vestigo:Input:AllowInput to true.");
        }

        string mode = (imageMode ?? PerceptionTools.ImageAnnotated).Trim().ToLowerInvariant();
        string observation = (observeMode ?? ObserveFull).Trim().ToLowerInvariant();
        DesktopRect? windowCrop = null;
        if (observe)
        {
            if (!options.Desktop.AllowScreenshots)
            {
                return DesktopTools.ErrorResult("screenshots_disabled", "observe needs Vestigo:Desktop:AllowScreenshots to be true.");
            }

            if (mode is not (PerceptionTools.ImageAnnotated or PerceptionTools.ImageRaw or PerceptionTools.ImageNone))
            {
                return DesktopTools.ErrorResult("invalid_request", "imageMode must be 'annotated', 'raw' or 'none'.");
            }

            if (observeDelayMs is < 0 or > 5000 || maxEdge is < ScreenAnalyzer.MinMaxEdge or > ScreenAnalyzer.MaxMaxEdge)
            {
                return DesktopTools.ErrorResult("invalid_request", "observeDelayMs must be 0 to 5000 and maxEdge 320 to 3840.");
            }

            if (observation is not (ObserveFull or ObservePixels))
            {
                return DesktopTools.ErrorResult("invalid_request", "observeMode must be 'full' or 'pixels'.");
            }

            bool hasHandle = !string.IsNullOrWhiteSpace(observeWindowHandle);
            OperationResult<DesktopRect?> crop = PerceptionTools.ValidateWindowCrop(
                observeCropX,
                observeCropY,
                observeCropWidth,
                observeCropHeight,
                hasHandle,
                "observeCropX",
                "observeCropY",
                "observeCropWidth",
                "observeCropHeight",
                "observeWindowHandle is set");
            if (!crop.Success)
            {
                return DesktopTools.ErrorResult(crop.ErrorCode!, crop.Error!);
            }

            windowCrop = crop.Value;
        }

        OperationResult<InputBatch> plan = InputPlanner.Plan(actions, captureId, focusWindow, perception.Cache, options.Input, requireFocus);
        if (!plan.Success)
        {
            return DesktopTools.ErrorResult(plan.ErrorCode!, plan.Error!);
        }

        List<string> helperArguments = SessionHelper.BuildSendInputArguments(plan.Value!);
        if (helperArguments[^1].Length > SessionHelper.MaxBatchArgumentLength)
        {
            return DesktopTools.ErrorResult("invalid_request", "The action list is too large. Split it into several calls.");
        }

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated);
        if (!available.Success)
        {
            return DesktopTools.ErrorResult(available);
        }

        // Helper startup allowance plus the time every planned step takes, including waits and drag pauses.
        OperationResult<DesktopHelperTarget?> targeted = await DesktopHelperTargeting.ResolveAsync(desktops, desktopSessionId, requireInput: true, cancellationToken).ConfigureAwait(false);
        if (!targeted.Success)
        {
            return DesktopTools.ErrorResult(targeted.ErrorCode!, targeted.Error!, targeted.Reason);
        }

        double stepMilliseconds = plan.Value!.Steps.Sum(step =>
            250d + step.Milliseconds + (double)step.HoldMilliseconds * step.Count + step.DelayAfterMilliseconds + step.Count * 100d + (step.Text?.Length ?? 0) * 5d + (step.Kind == InputKinds.Drag ? 2000d : 0d));
        TimeSpan timeout = TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(stepMilliseconds);
        string? desktopPath = targeted.Value?.DesktopPath;
        IInspectCaptureSession? inspection = null;
        try
        {
            OperationResult<IInspectCaptureSession>? sessionStart = null;
            if (observe && ReuseInspection)
            {
                sessionStart = await OpenInspectionSessionAsync(elevated, desktopPath, cancellationToken).ConfigureAwait(false);
                if (sessionStart.Success)
                {
                    inspection = sessionStart.Value;
                }
            }

            OperationResult<PerceptionOutcome>? before = null;
            bool pixelOnly = observation == ObservePixels;
            if (observe)
            {
                // The before image is only compared, never returned, so it is never annotated.
                // A reusable helper that fails to start is not replaced with a one-shot helper.
                before = sessionStart is { Success: false }
                    ? sessionStart.FailAs<PerceptionOutcome>()
                    : await ObserveAsync(observeWindowHandle, mode, maxEdge, elevated, desktopPath, null, pixelOnly, windowCrop, annotate: false, cancellationToken, inspection).ConfigureAwait(false);
            }

            OperationResult<InputResult> sent = await _submissions.RunAsync(
                    desktopPath,
                    () => helper.RunInputAsync<InputResult>(helperArguments, elevated, timeout, HelperMaxOutputBytes, cancellationToken, desktopPath),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!sent.Success)
            {
                return DesktopTools.ErrorResult(sent);
            }

            // Typed text is never logged, only the kinds and counts.
            logger?.LogInformation(
                "Input actions={Count} kinds={Kinds} typedCharacters={Typed} observe={Observe} status={Status} submittedEvents={Submitted} observeMode={ObserveMode}",
                plan.Value!.Steps.Count,
                string.Join(',', plan.Value.Steps.Select(step => step.Kind).Distinct()),
                plan.Value.Steps.Sum(step => step.Text?.Length ?? 0),
                observe,
                sent.Value!.Status,
                sent.Value.SubmittedEvents,
                observe ? observation : "none");

            InputResult evidence = sent.Value!;
            bool rejected = string.Equals(evidence.Status, InputInjectionStatus.Rejected, StringComparison.Ordinal);
            JsonObject injection = new()
            {
                ["status"] = string.IsNullOrEmpty(evidence.Status) ? InputInjectionStatus.Submitted : evidence.Status,
                ["submittedEvents"] = evidence.SubmittedEvents,
                ["focusRequested"] = evidence.FocusRequested,
                ["focusActual"] = evidence.FocusActual,
                ["focusGranted"] = evidence.FocusGranted,
                ["cursorActual"] = evidence.CursorX is null ? null : new JsonObject { ["x"] = evidence.CursorX, ["y"] = evidence.CursorY },
                ["cleanupReleasedEvents"] = evidence.CleanupReleasedEvents
            };
            if (evidence.StopCode is not null)
            {
                injection["stopCode"] = evidence.StopCode;
            }

            if (evidence.StopReason is not null)
            {
                injection["stopReason"] = evidence.StopReason;
            }

            if (evidence.StoppedAtActionIndex is not null)
            {
                injection["stoppedAtActionIndex"] = evidence.StoppedAtActionIndex;
            }

            if (evidence.StoppedAtActionKind is not null)
            {
                injection["stoppedAtActionKind"] = evidence.StoppedAtActionKind;
            }

            if (evidence.Timing is not null)
            {
                injection["timing"] = JsonSerializer.SerializeToNode(evidence.Timing, VisionJson.Options);
            }

            JsonObject summary = new()
            {
                ["performed"] = evidence.Performed,
                ["sessionId"] = evidence.SessionId,
                ["injection"] = injection,
                ["response"] = observe
                    ? rejected
                        ? new JsonObject { ["status"] = "skipped", ["reason"] = "input_rejected" }
                        : new JsonObject { ["status"] = "pending" }
                    : new JsonObject { ["status"] = "not-requested" }
            };
            if (evidence.FocusWarning is not null)
            {
                summary["focusWarning"] = evidence.FocusWarning;
            }

            List<ContentBlock> content = [new TextContentBlock { Text = summary.ToJsonString(VisionJson.Options) }];
            if (!observe || rejected)
            {
                return new CallToolResult { Content = content };
            }

            if (observeDelayMs > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(observeDelayMs), _clock, cancellationToken).ConfigureAwait(false);
            }

            string? baselineId = before is { Success: true } ? before.Value!.Record.Id : null;
            OperationResult<PerceptionOutcome> observed = sessionStart is { Success: false }
                ? sessionStart.FailAs<PerceptionOutcome>()
                : await ObserveAsync(observeWindowHandle, mode, maxEdge, elevated, desktopPath, baselineId, pixelOnly, windowCrop, mode == PerceptionTools.ImageAnnotated, cancellationToken, inspection).ConfigureAwait(false);
            // Pixel mode compares only the same pixels, so a mismatched capture is inconclusive instead of retried.
            // The retry uses the same helper. Input is never sent again.
            if (!pixelOnly && !observed.Success && observed.ErrorCode == "capture_mismatch" && before is { Success: true } && sessionStart is not { Success: false })
            {
                OperationResult<PerceptionOutcome> retry = await ObserveAsync(observeWindowHandle, mode, maxEdge, elevated, desktopPath, null, false, windowCrop, mode == PerceptionTools.ImageAnnotated, cancellationToken, inspection).ConfigureAwait(false);
                if (retry.Success && retry.Value is not null && BoundsChanged(before.Value!, retry.Value))
                {
                    observed = retry;
                }
                else
                {
                    summary["response"] = new JsonObject
                    {
                        ["status"] = "inconclusive",
                        ["compared"] = string.IsNullOrWhiteSpace(observeWindowHandle) ? "screenshot-and-elements" : "window"
                    };
                    summary["observeError"] = JsonSerializer.SerializeToNode(
                        new { errorCode = observed.ErrorCode, error = observed.Error, reason = observed.Reason },
                        VisionJson.Options);
                    content[0] = new TextContentBlock { Text = summary.ToJsonString(VisionJson.Options) };
                    return new CallToolResult { Content = content };
                }
            }

            if (inspection is not null
                && observed.Success
                && observed.Value!.Capture.SessionId != evidence.SessionId)
            {
                summary["response"] = new JsonObject
                {
                    ["status"] = "inconclusive",
                    ["compared"] = pixelOnly
                        ? windowCrop is not null ? "window-crop-pixels" : string.IsNullOrWhiteSpace(observeWindowHandle) ? "pixels" : "window-pixels"
                        : string.IsNullOrWhiteSpace(observeWindowHandle) ? "screenshot-and-elements" : "window"
                };
                summary["observeError"] = JsonSerializer.SerializeToNode(
                    new
                    {
                        errorCode = "session_changed",
                        error = "The console session changed after the inspection helper started, so the later capture is not the session that received the input."
                    },
                    VisionJson.Options);
                content[0] = new TextContentBlock { Text = summary.ToJsonString(VisionJson.Options) };
                return new CallToolResult { Content = content };
            }

            string responseStatus = pixelOnly ? ClassifyPixels(before, observed) : ClassifyResponse(before, observed);
            summary["response"] = new JsonObject
            {
                ["status"] = responseStatus,
                ["compared"] = pixelOnly
                    ? windowCrop is not null ? "window-crop-pixels" : string.IsNullOrWhiteSpace(observeWindowHandle) ? "pixels" : "window-pixels"
                    : string.IsNullOrWhiteSpace(observeWindowHandle) ? "screenshot-and-elements" : "window"
            };
            if (!observed.Success)
            {
                summary["observeError"] = JsonSerializer.SerializeToNode(
                    new { errorCode = observed.ErrorCode, error = observed.Error, reason = observed.Reason },
                    VisionJson.Options);
                content[0] = new TextContentBlock { Text = summary.ToJsonString(VisionJson.Options) };
                return new CallToolResult { Content = content };
            }

            content[0] = new TextContentBlock { Text = summary.ToJsonString(VisionJson.Options) };
            CallToolResult perceived = PerceptionTools.Describe(observed.Value!, mode, null);
            content.AddRange(perceived.Content);
            return new CallToolResult { Content = content };
        }
        finally
        {
            if (inspection is not null)
            {
                await inspection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private Task<OperationResult<IInspectCaptureSession>> OpenInspectionSessionAsync(
        bool elevated,
        string? desktop,
        CancellationToken cancellationToken) =>
        InspectSessionFactory is not null
            ? InspectSessionFactory(cancellationToken)
            : InspectHelperSession.StartAsync(launcher, elevated, desktop, cancellationToken);

    private Task<OperationResult<PerceptionOutcome>> ObserveAsync(
        string? observeWindowHandle,
        string mode,
        int maxEdge,
        bool elevated,
        string? desktop,
        string? diffAgainst,
        bool pixelOnly,
        DesktopRect? windowCrop,
        bool annotate,
        CancellationToken cancellationToken,
        IInspectCaptureSession? session = null)
    {
        bool window = !string.IsNullOrWhiteSpace(observeWindowHandle);
        ScreenCaptureRequest capture = ScreenCaptureRequest.DesktopPreview with
        {
            Target = window ? ScreenCaptureTarget.Window : ScreenCaptureTarget.Desktop,
            WindowHandle = window ? observeWindowHandle!.Trim() : null,
            MaxEdge = 0,
            IncludeFullImage = true,
            MaxBytes = options.Desktop.MaxScreenshotBytes,
            WindowCrop = window ? windowCrop : null
        };
        PerceptionRequest perceptionRequest = new(capture, !pixelOnly, !pixelOnly, maxEdge, false, annotate, diffAgainst, elevated, Desktop: desktop);
        return session is null
            ? perception.InspectAsync(perceptionRequest, null, cancellationToken)
            : perception.InspectAsync(perceptionRequest, null, session, cancellationToken);
    }

    private static string ClassifyPixels(OperationResult<PerceptionOutcome>? before, OperationResult<PerceptionOutcome> after)
    {
        if (before is not { Success: true } || !after.Success || after.Value?.Diff is not { } diff)
        {
            return "inconclusive";
        }

        return diff.ChangedPercent > 0 || diff.Regions.Count > 0 ? "observed" : "no-response";
    }

    private static string ClassifyResponse(OperationResult<PerceptionOutcome>? before, OperationResult<PerceptionOutcome> after)
    {
        if (before is not { Success: true } || !after.Success || after.Value is null)
        {
            return "inconclusive";
        }

        PerceptionOutcome earlier = before.Value!;
        PerceptionOutcome later = after.Value;
        bool pixels = later.Diff is { ChangedPercent: > 0 };
        bool window = BoundsChanged(earlier, later);
        bool elements = ElementSignature(earlier.Record.Elements) != ElementSignature(later.Record.Elements);
        return pixels || window || elements ? "observed" : "no-response";
    }

    private static bool BoundsChanged(PerceptionOutcome earlier, PerceptionOutcome later) =>
        earlier.Capture.Bounds != later.Capture.Bounds || earlier.Capture.WindowTitle != later.Capture.WindowTitle;

    private static string ElementSignature(IReadOnlyList<ScreenElement> elements) =>
        string.Join('\n', elements.Select(element => $"{element.Source}|{element.Role}|{element.Name}|{element.Text}|{element.Box.X},{element.Box.Y},{element.Box.Width},{element.Box.Height}"));
}
