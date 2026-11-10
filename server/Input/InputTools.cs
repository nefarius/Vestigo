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
    ILogger<InputTools>? logger = null)
{
    private const long HelperMaxOutputBytes = 64 * 1024;

    [McpServerTool(Name = "send_input", Destructive = true, OpenWorld = false)]
    [Description(
        "Injects mouse and keyboard input as the console user. actions run in order: move, click (button, count), down, up, scroll (deltaY notches down, deltaX right), "
        + "drag (x,y to toX,toY), type (text as Unicode characters), key (a key or chord such as enter, ctrl+c, alt+f4, win+r) and wait (milliseconds). "
        + "A point is elementId from inspect_screen or find_on_screen, or x and y: in the pixels of the returned image when captureId is set, otherwise absolute virtual-screen physical pixels. "
        + "focusWindow (hex handle from list_windows) is brought to the foreground first. observe:true waits observeDelayMs, then returns a fresh inspect_screen result, "
        + "so one call does act and look; it needs Vestigo:Desktop:AllowScreenshots. Cannot reach the secure desktop (UAC prompt, lock screen, Ctrl+Alt+Del) and cannot "
        + "reach elevated windows unless elevated=true. Requires Vestigo:Input:AllowInput. Interactive session only.")]
    public async Task<CallToolResult> SendInput(
        [Description("Actions to perform in order.")] List<InputActionSpec> actions,
        [Description("captureId that element ids and x,y refer to.")] string? captureId = null,
        [Description("Hex window handle from list_windows to focus first.")] string? focusWindow = null,
        [Description("Return a fresh inspect_screen result after the actions.")] bool observe = false,
        [Description("Milliseconds to wait before observing, 0 to 5000. Default 300.")] int observeDelayMs = 300,
        [Description("Observe only: window handle to inspect instead of the whole desktop.")] string? observeWindowHandle = null,
        [Description("Observe only: 'annotated' (default), 'raw' or 'none'.")] string imageMode = PerceptionTools.ImageAnnotated,
        [Description("Observe only: longest edge of the returned image, 320 to 3840.")] int maxEdge = ScreenAnalyzer.DefaultMaxEdge,
        [Description("Use the elevated token, needed to reach elevated windows.")] bool elevated = false,
        CancellationToken cancellationToken = default)
    {
        if (!options.Input.AllowInput)
        {
            return DesktopTools.ErrorResult("input_disabled", "Input injection is disabled. Set Vestigo:Input:AllowInput to true.");
        }

        string mode = (imageMode ?? PerceptionTools.ImageAnnotated).Trim().ToLowerInvariant();
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
        }

        OperationResult<InputBatch> plan = InputPlanner.Plan(actions, captureId, focusWindow, perception.Cache, options.Input);
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
        double stepMilliseconds = plan.Value!.Steps.Sum(step =>
            250d + step.Milliseconds + step.Count * 100d + (step.Text?.Length ?? 0) * 5d + (step.Kind == InputKinds.Drag ? 2000d : 0d));
        TimeSpan timeout = TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(stepMilliseconds);
        OperationResult<InputResult> sent = await helper
            .RunAsync<InputResult>(helperArguments, elevated, timeout, HelperMaxOutputBytes, cancellationToken)
            .ConfigureAwait(false);
        if (!sent.Success)
        {
            return DesktopTools.ErrorResult(sent);
        }

        // Typed text is never logged, only the kinds and counts.
        logger?.LogInformation(
            "Input actions={Count} kinds={Kinds} typedCharacters={Typed} observe={Observe}",
            plan.Value!.Steps.Count,
            string.Join(',', plan.Value.Steps.Select(step => step.Kind).Distinct()),
            plan.Value.Steps.Sum(step => step.Text?.Length ?? 0),
            observe);

        JsonObject summary = new()
        {
            ["performed"] = sent.Value!.Performed,
            ["sessionId"] = sent.Value.SessionId
        };
        if (sent.Value.FocusWarning is not null)
        {
            summary["focusWarning"] = sent.Value.FocusWarning;
        }

        List<ContentBlock> content = [new TextContentBlock { Text = summary.ToJsonString(VisionJson.Options) }];
        if (!observe)
        {
            return new CallToolResult { Content = content };
        }

        if (observeDelayMs > 0)
        {
            await Task.Delay(observeDelayMs, cancellationToken).ConfigureAwait(false);
        }

        bool window = !string.IsNullOrWhiteSpace(observeWindowHandle);
        ScreenCaptureRequest capture = ScreenCaptureRequest.DesktopPreview with
        {
            Target = window ? ScreenCaptureTarget.Window : ScreenCaptureTarget.Desktop,
            WindowHandle = window ? observeWindowHandle!.Trim() : null,
            MaxEdge = 0,
            IncludeFullImage = true,
            MaxBytes = options.Desktop.MaxScreenshotBytes
        };
        OperationResult<PerceptionOutcome> observed = await perception.InspectAsync(
            new PerceptionRequest(capture, true, true, maxEdge, false, mode == PerceptionTools.ImageAnnotated, null, elevated),
            null,
            cancellationToken).ConfigureAwait(false);
        if (!observed.Success)
        {
            // The input already happened, so report the failure next to the success instead of as an error.
            summary["observeError"] = JsonSerializer.SerializeToNode(
                new { errorCode = observed.ErrorCode, error = observed.Error, reason = observed.Reason },
                VisionJson.Options);
            content[0] = new TextContentBlock { Text = summary.ToJsonString(VisionJson.Options) };
            return new CallToolResult { Content = content };
        }

        CallToolResult perceived = PerceptionTools.Describe(observed.Value!, mode, null);
        content.AddRange(perceived.Content);
        return new CallToolResult { Content = content };
    }
}
