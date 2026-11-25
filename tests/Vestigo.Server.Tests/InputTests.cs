using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol.Protocol;

using Vestigo.Configuration;
using Vestigo.Desktop;
using Vestigo.Input;
using Vestigo.Modules;
using Vestigo.Processes;
using Vestigo.Vision;

namespace Vestigo.Server.Tests;

public class InputTests
{
    [Theory]
    [InlineData("enter", new ushort[] { 0x0D })]
    [InlineData("ctrl+c", new ushort[] { 0x11, 0x43 })]
    [InlineData("Ctrl+Shift+Esc", new ushort[] { 0x11, 0x10, 0x1B })]
    [InlineData("alt+f4", new ushort[] { 0x12, 0x73 })]
    [InlineData("win+r", new ushort[] { 0x5B, 0x52 })]
    [InlineData("ctrl++", new ushort[] { 0x11, 0xBB })]
    [InlineData("F24", new ushort[] { 0x87 })]
    public void KeyChord_ParsesKnownChords(string chord, ushort[] expected)
    {
        Assert.True(KeyChordParser.TryParse(chord, out IReadOnlyList<KeyStroke> keys, out string? error));
        Assert.Null(error);
        Assert.Equal(expected, keys.Select(key => key.Vk).ToArray());
    }

    [Fact]
    public void KeyChord_MarksExtendedKeys()
    {
        Assert.True(KeyChordParser.TryParse("delete", out IReadOnlyList<KeyStroke> keys, out _));
        Assert.True(Assert.Single(keys).Extended);
        Assert.True(KeyChordParser.TryParse("a", out keys, out _));
        Assert.False(Assert.Single(keys).Extended);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ctrl+")]
    [InlineData("ctrl+nope")]
    [InlineData("f25")]
    [InlineData("a+b+c+d+e+f+g")]
    public void KeyChord_RejectsBadChords(string chord)
    {
        Assert.False(KeyChordParser.TryParse(chord, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void InputOptions_ValidatesLimits()
    {
        new InputOptions().Validate();
        Assert.Throws<InvalidOperationException>(() => new InputOptions { MaxActions = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new InputOptions { MaxActions = InputOptions.MaxMaxActions + 1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new InputOptions { MaxWaitMilliseconds = -1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new InputOptions { MaxTypedCharacters = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { Input = { MaxActions = 0 } }.Validate());
        Assert.False(new InputOptions().AllowInput);
    }

    [Fact]
    public void Planner_RejectsEmptyOversizedAndUnknown()
    {
        CaptureCache cache = new();
        InputOptions limits = new() { MaxActions = 2 };

        Assert.Equal("invalid_request", InputPlanner.Plan(null, null, null, cache, limits).ErrorCode);
        Assert.Equal("invalid_request", InputPlanner.Plan([], null, null, cache, limits).ErrorCode);
        Assert.Equal("invalid_request", InputPlanner.Plan([Spec("click"), Spec("click"), Spec("click")], null, null, cache, limits).ErrorCode);
        Assert.Equal("invalid_request", InputPlanner.Plan([Spec("teleport")], null, null, cache, limits).ErrorCode);
        Assert.Equal("capture_expired", InputPlanner.Plan([Spec("click")], "cap-missing", null, cache, limits).ErrorCode);
    }

    [Fact]
    public void Planner_UsesAbsolutePixelsWithoutCapture()
    {
        OperationResult<InputBatch> plan = InputPlanner.Plan(
            [new InputActionSpec { Type = "click", X = 100, Y = -50, Button = "RIGHT", Count = 2 }],
            null,
            " 0x10 ",
            new CaptureCache(),
            new InputOptions());

        Assert.True(plan.Success);
        InputStep step = Assert.Single(plan.Value!.Steps);
        Assert.Equal((100, -50), (step.X, step.Y));
        Assert.Equal("right", step.Button);
        Assert.Equal(2, step.Count);
        Assert.Equal("0x10", plan.Value.FocusWindow);
    }

    [Fact]
    public void Planner_MapsPreviewPixelsAndElementsThroughCapture()
    {
        CaptureCache cache = new();
        // 2560x1440 physical, offset by a second monitor on the left, shown as a 1280x720 image.
        CaptureRecord record = cache.Add(
            new DesktopRect(-2560, 0, 2560, 1440),
            new DesktopSize(1280, 720),
            [new ScreenElement(1, ElementSource.Uia, "button", "OK", null, new DesktopRect(-2000, 100, 200, 60))],
            [1]);

        OperationResult<InputBatch> plan = InputPlanner.Plan(
            [
                new InputActionSpec { Type = "move", X = 640, Y = 360 },
                new InputActionSpec { Type = "click", ElementId = 1 },
                new InputActionSpec { Type = "drag", X = 0, Y = 0, ToX = 1280, ToY = 720 }
            ],
            record.Id,
            null,
            cache,
            new InputOptions());

        Assert.True(plan.Success);
        IReadOnlyList<InputStep> steps = plan.Value!.Steps;
        Assert.Equal((-1280, 720), (steps[0].X, steps[0].Y));
        Assert.Equal((-1900, 130), (steps[1].X, steps[1].Y));
        Assert.Equal((-2560, 0), (steps[2].X, steps[2].Y));
        // The far edge clamps to the last captured pixel instead of leaving the screen.
        Assert.Equal((-1, 1439), (steps[2].ToX, steps[2].ToY));
    }

    [Fact]
    public void Planner_ReportsElementAndPointProblems()
    {
        CaptureCache cache = new();
        CaptureRecord record = cache.Add(new DesktopRect(0, 0, 100, 100), new DesktopSize(100, 100), [], [1]);
        InputOptions limits = new();

        Assert.Equal("element_not_found", InputPlanner.Plan([new InputActionSpec { Type = "click", ElementId = 9 }], record.Id, null, cache, limits).ErrorCode);
        Assert.Equal("invalid_request", InputPlanner.Plan([new InputActionSpec { Type = "click", ElementId = 1 }], null, null, cache, limits).ErrorCode);
        Assert.Equal("invalid_request", InputPlanner.Plan([new InputActionSpec { Type = "move", X = 5 }], null, null, cache, limits).ErrorCode);
        Assert.Equal("invalid_request", InputPlanner.Plan([new InputActionSpec { Type = "move" }], null, null, cache, limits).ErrorCode);
        Assert.Equal("invalid_request", InputPlanner.Plan([new InputActionSpec { Type = "move", X = 500, Y = 5 }], record.Id, null, cache, limits).ErrorCode);
        Assert.Equal("invalid_request", InputPlanner.Plan([new InputActionSpec { Type = "drag", X = 1, Y = 1 }], null, null, cache, limits).ErrorCode);
    }

    [Fact]
    public void Planner_ValidatesActionFields()
    {
        CaptureCache cache = new();
        InputOptions limits = new() { MaxWaitMilliseconds = 1000, MaxTypedCharacters = 5 };

        Assert.Equal("invalid_request", Code(new InputActionSpec { Type = "click", Button = "extra" }));
        Assert.Equal("invalid_request", Code(new InputActionSpec { Type = "click", Count = 4 }));
        Assert.Equal("invalid_request", Code(new InputActionSpec { Type = "scroll" }));
        Assert.Equal("invalid_request", Code(new InputActionSpec { Type = "scroll", DeltaY = 51 }));
        Assert.Equal("invalid_request", Code(new InputActionSpec { Type = "type" }));
        Assert.Equal("invalid_request", Code(new InputActionSpec { Type = "key", Keys = "ctrl+nope" }));
        Assert.Equal("invalid_request", Code(new InputActionSpec { Type = "wait" }));
        Assert.Equal("invalid_request", Code(new InputActionSpec { Type = "wait", Milliseconds = -1 }));
        // Totals across actions are limited, not only single actions.
        Assert.Equal("invalid_request", InputPlanner.Plan([Wait(600), Wait(600)], null, null, cache, limits).ErrorCode);
        Assert.Equal("invalid_request", InputPlanner.Plan([Type("abc"), Type("def")], null, null, cache, limits).ErrorCode);
        Assert.True(InputPlanner.Plan([Wait(500), Wait(500), Type("abcde")], null, null, cache, limits).Success);
        Assert.True(InputPlanner.Plan([new InputActionSpec { Type = "scroll", DeltaY = -3 }, new InputActionSpec { Type = "key", Keys = "ctrl+a" }], null, null, cache, limits).Success);

        string? Code(InputActionSpec action) => InputPlanner.Plan([action], null, null, cache, limits).ErrorCode;
    }

    [Fact]
    public void SessionHelper_RoundTripsSendInputBatch()
    {
        InputBatch batch = new(
            [
                new InputStep("click", 10, 20, null, null, "left", 2, 0, 0, null, null, 0),
                new InputStep("type", null, null, null, null, null, 1, 0, 0, "héllo \"quoted\"\n", null, 0),
                new InputStep("key", null, null, null, null, null, 1, 0, 0, null, [new KeyStroke(0x11, false), new KeyStroke(0x2E, true)], 0)
            ],
            "0xABC");

        string[] args = SessionHelper.BuildSendInputArguments(batch).ToArray();

        Assert.True(SessionHelper.IsInvocation(args));
        Assert.True(SessionHelper.TryParseSendInput(args, out InputBatch parsed, out string? error));
        Assert.Null(error);
        Assert.Equal("0xABC", parsed.FocusWindow);
        Assert.False(parsed.RequireFocus);
        Assert.Equal(3, parsed.Steps.Count);
        Assert.Equal("héllo \"quoted\"\n", parsed.Steps[1].Text);
        Assert.True(parsed.Steps[2].Keys![1].Extended);

        Assert.False(SessionHelper.TryParseSendInput([SessionHelper.Flag, SessionHelper.SendInputCommand], out _, out _));
        Assert.False(SessionHelper.TryParseSendInput([SessionHelper.Flag, SessionHelper.SendInputCommand, "--batch", "!!!"], out _, out _));
    }

    [Fact]
    public void InputModule_IsOptInAndDependsOnDesktop()
    {
        VestigoModule module = Assert.Single(ModuleRegistry.All, candidate => candidate.Name == "Input");
        Assert.False(module.EnabledByDefault);
        Assert.Equal(["Desktop"], module.Dependencies);
        Assert.Contains(typeof(InputTools), module.ToolTypes);
        Assert.Contains(typeof(PerceptionTools), Assert.Single(ModuleRegistry.All, candidate => candidate.Name == "Desktop").ToolTypes);

        VestigoOptions inputOnly = new() { Modules = { ["Input"] = new ModuleSettings { Enabled = true } } };
        Assert.Throws<InvalidOperationException>(() => ModuleRegistry.ValidateConfiguration(inputOnly));

        VestigoOptions both = new()
        {
            Modules = { ["Input"] = new ModuleSettings { Enabled = true }, ["Desktop"] = new ModuleSettings { Enabled = true } }
        };
        ModuleRegistry.ValidateConfiguration(both);
        Assert.Null(ModuleRegistry.GetPrivilegedModuleWarning(new VestigoOptions { ApiTokens = ["t"] }));

        // Enabled but gated off still warns about the Desktop module only; the gate adds the input capability.
        string withoutGate = ModuleRegistry.GetPrivilegedModuleWarning(both)!;
        Assert.DoesNotContain("Input injection", withoutGate);
        both.Input.AllowInput = true;
        Assert.Contains("Input injection", ModuleRegistry.GetPrivilegedModuleWarning(both));
    }

    [Fact]
    public async Task SendInput_RefusesWhenDisabledOrInvalid()
    {
        InputTools disabled = CreateTools(new VestigoOptions());
        Assert.Equal("input_disabled", ErrorCode(await disabled.SendInput([Spec("click")])));

        VestigoOptions enabled = new() { Input = { AllowInput = true } };
        InputTools tools = CreateTools(enabled);
        Assert.Equal("invalid_request", ErrorCode(await tools.SendInput([])));
        Assert.Equal("invalid_request", ErrorCode(await tools.SendInput([Spec("bogus")])));
        Assert.Equal("capture_expired", ErrorCode(await tools.SendInput([Spec("click")], captureId: "cap-none")));
        Assert.Equal("no_interactive_session", ErrorCode(await tools.SendInput([new InputActionSpec { Type = "click", X = 1, Y = 1 }])));

        // observe needs the screenshot gate, and it is checked before any input is injected.
        Assert.Equal("screenshots_disabled", ErrorCode(await tools.SendInput([new InputActionSpec { Type = "click", X = 1, Y = 1 }], observe: true)));
        enabled.Desktop.AllowScreenshots = true;
        Assert.Equal("invalid_request", ErrorCode(await tools.SendInput([new InputActionSpec { Type = "click", X = 1, Y = 1 }], observe: true, imageMode: "gif")));
        Assert.Equal("invalid_request", ErrorCode(await tools.SendInput([new InputActionSpec { Type = "click", X = 1, Y = 1 }], observe: true, observeDelayMs: 9000)));
    }

    internal static InputTools CreateTools(VestigoOptions options)
    {
        UnavailableLauncher launcher = new();
        SessionHelperClient helper = new(launcher);
        CaptureCache cache = new();
        return new InputTools(helper, launcher, new PerceptionService(launcher, helper, cache, options), options);
    }

    internal static string? ErrorCode(CallToolResult result)
    {
        Assert.True(result.IsError);
        TextContentBlock text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using JsonDocument document = JsonDocument.Parse(text.Text);
        return document.RootElement.GetProperty("errorCode").GetString();
    }

    [Fact]
    public void Planner_AppliesHoldAndDelayToClickAndKey()
    {
        CaptureCache cache = new();
        InputOptions limits = new();
        OperationResult<InputBatch> plan = InputPlanner.Plan(
            [
                new InputActionSpec { Type = "click", HoldMilliseconds = 40, DelayAfterMilliseconds = 15 },
                new InputActionSpec { Type = "key", Keys = "enter", HoldMilliseconds = 25 }
            ],
            null,
            null,
            cache,
            limits);

        Assert.True(plan.Success, plan.Error);
        Assert.Equal(40, plan.Value!.Steps[0].HoldMilliseconds);
        Assert.Equal(15, plan.Value.Steps[0].DelayAfterMilliseconds);
        Assert.Equal(25, plan.Value.Steps[1].HoldMilliseconds);
        Assert.Equal("invalid_request", InputPlanner.Plan([new InputActionSpec { Type = "move", X = 1, Y = 2, HoldMilliseconds = 10 }], null, null, cache, limits).ErrorCode);
        Assert.Equal(
            "invalid_request",
            InputPlanner.Plan([new InputActionSpec { Type = "click", Count = 3, HoldMilliseconds = 5000 }], null, null, cache, limits).ErrorCode);
        Assert.True(InputPlanner.Plan([new InputActionSpec { Type = "click", Count = 1, HoldMilliseconds = 5000 }], null, null, cache, limits).Success);
    }

    [Fact]
    public async Task InactiveDesktop_AllowsEnumerationAndRefusesInput()
    {
        FixedDesktop desktops = new(input: false);
        OperationResult<DesktopHelperTarget?> listed = await DesktopHelperTargeting.ResolveAsync(desktops, "abc", requireInput: false, CancellationToken.None);
        OperationResult<DesktopHelperTarget?> injected = await DesktopHelperTargeting.ResolveAsync(desktops, "abc", requireInput: true, CancellationToken.None);

        Assert.True(listed.Success);
        Assert.False(listed.Value!.IsInputDesktop);
        Assert.Equal(DesktopHelperTargeting.Unsupported, injected.ErrorCode);
        Assert.Equal(DesktopHelperTargeting.InactiveReason, injected.Reason);
    }

    [Fact]
    public async Task SendInput_RefusesAnInactiveDesktopWithoutSwitchingOrLaunching()
    {
        RecordingLaunch launcher = new();
        VestigoOptions options = new() { Input = { AllowInput = true } };
        InputTools tools = new(
            new SessionHelperClient(launcher),
            launcher,
            new PerceptionService(launcher, new SessionHelperClient(launcher), new CaptureCache(), options),
            options,
            desktops: new FixedDesktop(input: false));

        CallToolResult result = await tools.SendInput([new InputActionSpec { Type = "click" }], desktopSessionId: "abc");

        Assert.Equal(DesktopHelperTargeting.Unsupported, ErrorCode(result));
        Assert.Equal(0, launcher.Runs);
    }

    [Fact]
    public async Task SendInput_LaunchesOnTheActiveManagedDesktop()
    {
        RecordingLaunch launcher = new();
        VestigoOptions options = new() { Input = { AllowInput = true } };
        InputTools tools = new(
            new SessionHelperClient(launcher),
            launcher,
            new PerceptionService(launcher, new SessionHelperClient(launcher), new CaptureCache(), options),
            options,
            desktops: new FixedDesktop(input: true));

        CallToolResult result = await tools.SendInput([new InputActionSpec { Type = "click" }], desktopSessionId: "abc");

        Assert.Equal(@"winsta0\Vestigo-0123456789ab", launcher.Desktop);
        Assert.Equal("helper_failed", ErrorCode(result));
    }

    [Fact]
    public async Task Placement_TargetsTheManagedDesktopAndRejectsAMismatchedToken()
    {
        RecordingLaunch launcher = new()
        {
            Response = """
                {"achieved":true,"coordinateSpace":"physical-screen-pixels","requestedClient":{"x":10,"y":10,"width":20,"height":20},"measuredClient":{"x":10,"y":10,"width":20,"height":20},"activationPreserved":true,"attempts":[],"capturedPlacement":{"handle":"0x11","processId":9,"startTimeReadable":false,"showCommand":1,"normalWidth":30,"normalHeight":40}}
                """
        };
        VestigoOptions options = new() { Desktop = { AllowWindowPlacement = true } };
        WindowPlacementStore store = new(TimeProvider.System);
        WindowPlacementTools tools = new(
            new WindowPlacer(new UnusedPlacement(), store),
            store,
            new SessionHelperClient(launcher),
            launcher,
            options,
            new FixedDesktop(input: false));

        OperationResult<WindowPlacementResult> placed = await tools.SetWindowClientRect(
            "0x11", 10, 10, 20, 20, savePlacement: true, desktopSessionId: "abc");

        Assert.True(placed.Success, placed.Error);
        Assert.Equal(@"winsta0\Vestigo-0123456789ab", launcher.Desktop);
        Assert.False(string.IsNullOrWhiteSpace(placed.Value!.SavedPlacementToken));

        OperationResult<WindowPlacementResult> mismatched = await tools.RestoreWindowPlacement(placed.Value.SavedPlacementToken!);
        Assert.Equal("placement_desktop_mismatch", mismatched.ErrorCode);

        OperationResult<WindowPlacementResult> restored = await tools.RestoreWindowPlacement(placed.Value.SavedPlacementToken!, desktopSessionId: "abc");
        Assert.True(restored.Success, restored.Error);
        Assert.Equal(@"winsta0\Vestigo-0123456789ab", launcher.Desktop);
    }

    private static InputActionSpec Spec(string type) => new() { Type = type };

    private static InputActionSpec Wait(int ms) => new() { Type = "wait", Milliseconds = ms };

    private static InputActionSpec Type(string text) => new() { Type = "type", Text = text };

    private sealed class UnusedPlacement : IWindowPlacementSource
    {
        public OperationResult<PlacementSample> Measure(string handle, int? expectedPid, DateTimeOffset? expectedStartTime) =>
            throw new NotSupportedException();

        public OperationResult<PlacementMove> MoveOuter(string handle, int? expectedPid, DateTimeOffset? expectedStartTime, DesktopRect outer) =>
            throw new NotSupportedException();

        public OperationResult<SavedWindowPlacement> ReadPlacement(string handle, int? expectedPid, DateTimeOffset? expectedStartTime) =>
            throw new NotSupportedException();

        public OperationResult<PlacementMove> ApplyPlacement(SavedWindowPlacement placement) =>
            throw new NotSupportedException();

        public string? Foreground() => null;
    }

    private sealed class FixedDesktop(bool input) : IManagedDesktopDirectory
    {
        public Task<OperationResult<ManagedDesktopLease>> LeaseForLaunchAsync(string sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<DesktopHelperTarget>> DescribeAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult<DesktopHelperTarget>.Ok(
                new DesktopHelperTarget(sessionId, @"winsta0\Vestigo-0123456789ab", "Vestigo-0123456789ab", input)));
    }

    [Fact]
    public void FocusGranted_IgnoresHexPadding()
    {
        Assert.True(InputInjector.FocusGranted("0x00001A2B", "0x1A2B"));
        Assert.False(InputInjector.FocusGranted("0x1A2B", "0x1A2C"));
        Assert.Null(InputInjector.FocusGranted("  ", "0x1A2B"));
    }

    [Fact]
    public void Planner_RequireFocusNeedsAHandle()
    {
        CaptureCache cache = new();
        InputOptions limits = new();
        Assert.Equal("invalid_request", InputPlanner.Plan([Spec("click")], null, null, cache, limits, requireFocus: true).ErrorCode);
        Assert.Equal("invalid_request", InputPlanner.Plan([Spec("click")], null, "next", cache, limits, requireFocus: true).ErrorCode);
        Assert.Equal("invalid_request", InputPlanner.Plan([Spec("click")], null, "0x0", cache, limits, requireFocus: true).ErrorCode);

        OperationResult<InputBatch> strict = InputPlanner.Plan([Spec("click")], null, " 0x2A ", cache, limits, requireFocus: true);
        Assert.True(strict.Success, strict.Error);
        Assert.True(strict.Value!.RequireFocus);
        Assert.Equal("0x2A", strict.Value.FocusWindow);

        OperationResult<InputBatch> open = InputPlanner.Plan([Spec("click")], null, null, cache, limits, requireFocus: false);
        Assert.True(open.Success, open.Error);
        Assert.False(open.Value!.RequireFocus);
        Assert.Null(open.Value.FocusWindow);
    }

    [Fact]
    public void SessionHelper_MissingRequireFocusStaysOff()
    {
        InputBatch batch = new([WaitStep(5)], "0x11", true);
        string json = JsonSerializer.Serialize(batch, SessionHelper.Json);
        JsonNode node = JsonNode.Parse(json)!;
        node.AsObject().Remove("requireFocus");
        string[] args =
        [
            SessionHelper.Flag,
            SessionHelper.SendInputCommand,
            "--batch",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(node.ToJsonString()))
        ];

        Assert.True(SessionHelper.TryParseSendInput(args, out InputBatch parsed, out string? error));
        Assert.Null(error);
        Assert.False(parsed.RequireFocus);
        Assert.Equal("0x11", parsed.FocusWindow);
    }

    [Fact]
    public void InputResult_RoundTripsLosslessTicksAndDefaultsStatus()
    {
        InputResult result = new(
            1,
            4,
            null,
            2,
            "0xA1",
            "0xA1",
            true,
            1,
            2,
            InputInjectionStatus.Submitted,
            Timing: new InputTiming(
                InputTiming.QueryPerformanceCounter,
                "10000000",
                false,
                new InputTimestamp(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), "9007199254740997"),
                new InputTimestamp(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(40), "9007199254740999")));
        string json = JsonSerializer.Serialize(result, SessionHelper.Json);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement ticks = document.RootElement.GetProperty("timing").GetProperty("firstSubmitted").GetProperty("qpcTicks");
        Assert.Equal(JsonValueKind.String, ticks.ValueKind);
        Assert.Equal("9007199254740997", ticks.GetString());
        long parsedTicks = long.Parse(ticks.GetString()!, CultureInfo.InvariantCulture);
        Assert.NotEqual(parsedTicks, (long)(double)parsedTicks);

        InputResult? restored = JsonSerializer.Deserialize<InputResult>(json, SessionHelper.Json);
        Assert.Equal("9007199254740999", restored!.Timing!.LastSubmitted.QpcTicks);
        InputResult? legacy = JsonSerializer.Deserialize<InputResult>(
            """{"performed":1,"sessionId":2,"focusWarning":null,"submittedEvents":0}""",
            SessionHelper.Json);
        Assert.Equal(InputInjectionStatus.Submitted, legacy!.Status);
        Assert.Null(legacy.Timing);
        Assert.Equal(0, legacy.CleanupReleasedEvents);
    }

    [Fact]
    public void Injector_RequireFocus_AcquiresFocus()
    {
        ScriptedInputPlatform platform = new();
        OperationResult<InputResult> result = InputInjector.Execute(Batch([KeyStep("a")], requireFocus: true), platform);

        Assert.True(result.Success, result.Error);
        Assert.Equal(InputInjectionStatus.Submitted, result.Value!.Status);
        Assert.True(result.Value.FocusGranted);
        Assert.Equal(1, result.Value.Performed);
        Assert.Equal(2, result.Value.SubmittedEvents);
        Assert.Equal(0, result.Value.CleanupReleasedEvents);
        Assert.Null(result.Value.StopCode);
        Assert.False(result.Value.Timing!.IncludesCleanupReleases);
        AssertOrdered(result.Value.Timing);
        Assert.Equal(2, platform.Submitted.Count);
    }

    [Fact]
    public void Injector_RequireFocus_DeniesInitialFocusWithoutSubmitting()
    {
        ScriptedInputPlatform platform = new() { ForegroundHandle = "0xB2" };
        OperationResult<InputResult> result = InputInjector.Execute(Batch([KeyStep("a")], requireFocus: true), platform);

        Assert.True(result.Success, result.Error);
        InputResult value = result.Value!;
        Assert.Equal(InputInjectionStatus.Rejected, value.Status);
        Assert.Equal(InputStopCodes.FocusNotGranted, value.StopCode);
        Assert.Equal(0, value.SubmittedEvents);
        Assert.Equal(0, value.CleanupReleasedEvents);
        Assert.Equal(0, value.Performed);
        Assert.Equal(0, value.StoppedAtActionIndex);
        Assert.Equal(InputKinds.Key, value.StoppedAtActionKind);
        Assert.Equal("0xA1", value.FocusRequested);
        Assert.Equal("0xB2", value.FocusActual);
        Assert.False(value.FocusGranted);
        Assert.Null(value.Timing);
        Assert.Empty(platform.Submitted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Injector_WithoutRequireFocus_StillSubmitsWhenForegroundDiffers(bool explicitFalse)
    {
        ScriptedInputPlatform platform = new() { ForegroundHandle = "0xB2" };
        InputBatch batch = explicitFalse
            ? new InputBatch([KeyStep("a")], "0xA1", false)
            : new InputBatch([KeyStep("a")], "0xA1");
        OperationResult<InputResult> result = InputInjector.Execute(batch, platform);

        Assert.True(result.Success, result.Error);
        Assert.Equal(InputInjectionStatus.Submitted, result.Value!.Status);
        Assert.False(result.Value.FocusGranted);
        Assert.Contains("foreground_not_granted", result.Value.FocusWarning, StringComparison.Ordinal);
        Assert.Null(result.Value.StopCode);
        Assert.Equal(2, result.Value.SubmittedEvents);
        Assert.Equal(2, platform.Submitted.Count);
    }

    [Fact]
    public void Injector_ClosedWindow_IsStructuredWhenStrictAndHardErrorOtherwise()
    {
        ScriptedInputPlatform closed = new() { WindowOpen = false };
        OperationResult<InputResult> strict = InputInjector.Execute(Batch([KeyStep("a")], requireFocus: true), closed);
        Assert.True(strict.Success, strict.Error);
        Assert.Equal(InputInjectionStatus.Rejected, strict.Value!.Status);
        Assert.Equal(InputStopCodes.WindowNotFound, strict.Value.StopCode);
        Assert.Equal(0, strict.Value.SubmittedEvents);
        Assert.Null(strict.Value.Timing);
        Assert.Empty(closed.Submitted);

        ScriptedInputPlatform legacy = new() { WindowOpen = false };
        OperationResult<InputResult> open = InputInjector.Execute(Batch([KeyStep("a")]), legacy);
        Assert.False(open.Success);
        Assert.Equal("window_not_found", open.ErrorCode);
        Assert.Empty(legacy.Submitted);
    }

    [Fact]
    public void Injector_RequireFocus_TargetClosedDuringLeadingWaitSubmitsNothing()
    {
        ScriptedInputPlatform platform = new();
        platform.CloseAfter(80);
        OperationResult<InputResult> result = InputInjector.Execute(
            Batch([WaitStep(80), KeyStep("a")], requireFocus: true),
            platform);

        Assert.True(result.Success, result.Error);
        Assert.Equal(InputInjectionStatus.Rejected, result.Value!.Status);
        Assert.Equal(InputStopCodes.WindowNotFound, result.Value.StopCode);
        Assert.Equal(0, result.Value.SubmittedEvents);
        Assert.Equal(1, result.Value.StoppedAtActionIndex);
        Assert.Equal(InputKinds.Key, result.Value.StoppedAtActionKind);
        Assert.Null(result.Value.Timing);
        Assert.Empty(platform.Submitted);
    }

    [Fact]
    public void Injector_RequireFocus_StopsDelayedSequenceAndSubmitsNoFurtherActions()
    {
        ScriptedInputPlatform platform = new();
        platform.StealAfter(200, "0xB2");
        OperationResult<InputResult> result = InputInjector.Execute(
            Batch([KeyStep("a"), WaitStep(200), TypeStep("Z")], requireFocus: true),
            platform);

        Assert.True(result.Success, result.Error);
        InputResult value = result.Value!;
        Assert.Equal(InputInjectionStatus.Partial, value.Status);
        Assert.Equal(InputStopCodes.FocusLost, value.StopCode);
        Assert.Equal(2, value.Performed);
        Assert.Equal(2, value.StoppedAtActionIndex);
        Assert.Equal(InputKinds.Type, value.StoppedAtActionKind);
        Assert.Equal(2, value.SubmittedEvents);
        Assert.Equal(0, value.CleanupReleasedEvents);
        Assert.False(value.Timing!.IncludesCleanupReleases);
        Assert.Equal("0xB2", value.FocusActual);
        Assert.Equal(2, platform.Submitted.OfType<PreparedKey>().Count(key => !key.Unicode));
        Assert.DoesNotContain(platform.Submitted.OfType<PreparedKey>(), key => key.Unicode);
    }

    [Fact]
    public void Injector_RequireFocus_ReleasesHeldKeyWhenFocusIsLostDuringHold()
    {
        ScriptedInputPlatform platform = new();
        platform.StealAfter(100, "0xB2");
        OperationResult<InputResult> result = InputInjector.Execute(
            Batch([KeyStep("a", hold: 100), TypeStep("Z")], requireFocus: true),
            platform);

        Assert.True(result.Success, result.Error);
        InputResult value = result.Value!;
        Assert.Equal(InputInjectionStatus.Partial, value.Status);
        Assert.Equal(InputStopCodes.FocusLost, value.StopCode);
        Assert.Equal(0, value.Performed);
        Assert.Equal(0, value.StoppedAtActionIndex);
        Assert.Equal(2, value.SubmittedEvents);
        Assert.Equal(1, value.CleanupReleasedEvents);
        Assert.True(value.Timing!.IncludesCleanupReleases);
        PreparedKey down = Assert.IsType<PreparedKey>(platform.Submitted[0]);
        PreparedKey up = Assert.IsType<PreparedKey>(platform.Submitted[1]);
        Assert.False(down.Up);
        Assert.True(up.Up);
        Assert.Equal(down.Vk, up.Vk);
        Assert.Equal(ScriptedInputPlatform.Target, platform.FocusAtSubmit[0]);
        Assert.Equal("0xB2", platform.FocusAtSubmit[1]);
        Assert.Equal(2, platform.Submitted.Count);
        Assert.DoesNotContain(platform.Submitted.OfType<PreparedKey>(), key => key.Unicode);
    }

    [Fact]
    public void Injector_RequireFocus_ReleasesHeldButtonAfterWaitAndDoesNotClickTheNewForeground()
    {
        ScriptedInputPlatform platform = new();
        platform.StealAfter(70, "0xB2");
        OperationResult<InputResult> result = InputInjector.Execute(
            Batch(
                [
                    new InputStep(InputKinds.Down, null, null, null, null, "left", 1, 0, 0, null, null, 0),
                    WaitStep(70),
                    new InputStep(InputKinds.Up, null, null, null, null, "left", 1, 0, 0, null, null, 0),
                    new InputStep(InputKinds.Click, 20, 20, null, null, "left", 1, 0, 0, null, null, 0)
                ],
                requireFocus: true),
            platform);

        Assert.True(result.Success, result.Error);
        Assert.Equal(InputInjectionStatus.Partial, result.Value!.Status);
        Assert.Equal(2, result.Value.Performed);
        Assert.Equal(2, result.Value.StoppedAtActionIndex);
        Assert.Equal(InputKinds.Up, result.Value.StoppedAtActionKind);
        Assert.Equal(1, result.Value.CleanupReleasedEvents);
        Assert.Equal(2, platform.Submitted.Count);
        Assert.Equal(InputMouseAction.LeftDown, Assert.IsType<PreparedMouse>(platform.Submitted[0]).Action);
        Assert.Equal(InputMouseAction.LeftUp, Assert.IsType<PreparedMouse>(platform.Submitted[1]).Action);
        Assert.Equal("0xB2", platform.FocusAtSubmit[1]);
        Assert.DoesNotContain(platform.Submitted.OfType<PreparedMouse>(), mouse => mouse.Action == InputMouseAction.Move);
    }

    [Fact]
    public void Injector_RequireFocus_StopsDragAndReleasesTheButton()
    {
        ScriptedInputPlatform platform = new();
        platform.StealAfter(20, "0xB2");
        InputStep drag = new(InputKinds.Drag, 10, 10, 70, 10, "left", 1, 0, 0, null, null, 0);
        OperationResult<InputResult> result = InputInjector.Execute(Batch([drag], requireFocus: true), platform);

        Assert.True(result.Success, result.Error);
        Assert.Equal(InputInjectionStatus.Partial, result.Value!.Status);
        Assert.Equal(InputStopCodes.FocusLost, result.Value.StopCode);
        Assert.Equal(1, result.Value.CleanupReleasedEvents);
        Assert.Equal(2, platform.Submitted.OfType<PreparedMouse>().Count(mouse => mouse.Action == InputMouseAction.Move));
        Assert.Equal(1, platform.Submitted.OfType<PreparedMouse>().Count(mouse => mouse.Action == InputMouseAction.LeftDown));
        PreparedMouse release = Assert.IsType<PreparedMouse>(platform.Submitted[^1]);
        Assert.Equal(InputMouseAction.LeftUp, release.Action);
        Assert.Equal("0xB2", platform.FocusAtSubmit[^1]);
        Assert.Equal(4, platform.Submitted.Count);
    }

    [Fact]
    public void Injector_TimestampsAreOrderedAndExcludeTrailingDelay()
    {
        ScriptedInputPlatform platform = new() { Ticks = 5_000 };
        OperationResult<InputResult> held = InputInjector.Execute(
            Batch([KeyStep("a", hold: 250, delayAfter: 1000)]),
            platform);
        Assert.True(held.Success, held.Error);
        long elapsed = Elapsed(held.Value!.Timing!);
        long holdTicks = 250L * platform.Frequency / 1000;
        long delayTicks = 1000L * platform.Frequency / 1000;
        AssertOrdered(held.Value.Timing!);
        Assert.InRange(elapsed, holdTicks, holdTicks + platform.Frequency / 10);
        Assert.True(elapsed < holdTicks + delayTicks / 2);

        ScriptedInputPlatform waited = new();
        OperationResult<InputResult> afterWait = InputInjector.Execute(Batch([KeyStep("a"), WaitStep(400)]), waited);
        Assert.True(afterWait.Success, afterWait.Error);
        Assert.True(Elapsed(afterWait.Value!.Timing!) < waited.Frequency / 20);
        Assert.False(afterWait.Value.Timing!.IncludesCleanupReleases);
    }

    [Fact]
    public void Injector_TimestampsUseLosslessQpcTicks()
    {
        long start = (1L << 53) + 5;
        ScriptedInputPlatform platform = new() { Ticks = start };
        OperationResult<InputResult> result = InputInjector.Execute(new InputBatch([KeyStep("a")], null), platform);

        Assert.True(result.Success, result.Error);
        Assert.Equal(start.ToString(CultureInfo.InvariantCulture), result.Value!.Timing!.FirstSubmitted.QpcTicks);
        Assert.Equal(InputTiming.QueryPerformanceCounter, result.Value.Timing.Clock);
        string json = JsonSerializer.Serialize(result.Value, SessionHelper.Json);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement ticks = document.RootElement.GetProperty("timing").GetProperty("qpcFrequency");
        Assert.Equal(JsonValueKind.String, ticks.ValueKind);
        Assert.Equal(platform.Frequency.ToString(CultureInfo.InvariantCulture), ticks.GetString());
    }

    [Fact]
    public void Injector_InactiveDesktopSubmitsNothing()
    {
        ScriptedInputPlatform platform = new() { Inactive = true };
        OperationResult<InputResult> result = InputInjector.Execute(Batch([KeyStep("a")], requireFocus: true), platform);
        Assert.Equal("unsupported_on_inactive_desktop", result.ErrorCode);
        Assert.Equal("desktop_inactive", result.Reason);
        Assert.Empty(platform.Submitted);
    }

    [Fact]
    public async Task SendInput_ObservationDoesNotChangeTheInputInterval()
    {
        InputTiming timing = new(
            InputTiming.QueryPerformanceCounter,
            "10000000",
            false,
            new InputTimestamp(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), "9007199254740997"),
            new InputTimestamp(new DateTimeOffset(2026, 3, 1, 0, 0, 1, TimeSpan.Zero), "9007199254740999"));
        InputResult injected = new(1, 7, null, 2, null, "0xA1", null, 3, 4, Timing: timing);
        ScriptedHelper launcher = new(JsonSerializer.Serialize(injected, SessionHelper.Json));
        VestigoOptions options = new()
        {
            Input = { AllowInput = true },
            Desktop = { AllowScreenshots = true }
        };
        SessionHelperClient helper = new(launcher);
        InputTools tools = new(helper, launcher, new PerceptionService(launcher, helper, new CaptureCache(), options), options);

        CallToolResult result = await tools.SendInput(
            [new InputActionSpec { Type = "key", Keys = "a" }],
            observe: true,
            observeDelayMs: 0);
        TextContentBlock text = Assert.IsType<TextContentBlock>(result.Content[0]);
        using JsonDocument document = JsonDocument.Parse(text.Text);
        JsonElement returned = document.RootElement.GetProperty("injection").GetProperty("timing");
        Assert.Equal("9007199254740997", returned.GetProperty("firstSubmitted").GetProperty("qpcTicks").GetString());
        Assert.Equal("9007199254740999", returned.GetProperty("lastSubmitted").GetProperty("qpcTicks").GetString());
        Assert.Equal(JsonValueKind.String, returned.GetProperty("firstSubmitted").GetProperty("qpcTicks").ValueKind);
        Assert.True(result.IsError is not true);
        Assert.Equal(3, launcher.Commands.Count);
        Assert.Contains(launcher.Commands, command => command.Contains("send-input", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendInput_RejectedObservationIsSkipped()
    {
        InputResult injected = new(
            0,
            7,
            null,
            0,
            "0xA1",
            "0xB2",
            false,
            Status: InputInjectionStatus.Rejected,
            StopCode: InputStopCodes.FocusNotGranted,
            StopReason: "The requested window was not the foreground window immediately before input submission.",
            StoppedAtActionIndex: 0,
            StoppedAtActionKind: InputKinds.Key);
        ScriptedHelper launcher = new(JsonSerializer.Serialize(injected, SessionHelper.Json));
        VestigoOptions options = new()
        {
            Input = { AllowInput = true },
            Desktop = { AllowScreenshots = true }
        };
        SessionHelperClient helper = new(launcher);
        InputTools tools = new(helper, launcher, new PerceptionService(launcher, helper, new CaptureCache(), options), options);

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        CallToolResult result = await tools.SendInput(
            [new InputActionSpec { Type = "key", Keys = "a" }],
            focusWindow: "0xA1",
            requireFocus: true,
            observe: true,
            observeDelayMs: 2000);
        watch.Stop();

        Assert.True(result.IsError is not true);
        TextContentBlock text = Assert.IsType<TextContentBlock>(result.Content[0]);
        using JsonDocument document = JsonDocument.Parse(text.Text);
        JsonElement injection = document.RootElement.GetProperty("injection");
        Assert.Equal(InputInjectionStatus.Rejected, injection.GetProperty("status").GetString());
        Assert.Equal(0, injection.GetProperty("submittedEvents").GetInt32());
        Assert.False(injection.TryGetProperty("timing", out _));
        Assert.Equal("skipped", document.RootElement.GetProperty("response").GetProperty("status").GetString());
        Assert.Equal("input_rejected", document.RootElement.GetProperty("response").GetProperty("reason").GetString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Single(launcher.Commands, command => command.Contains("send-input", StringComparison.Ordinal));
        Assert.Single(launcher.Commands, command => command.Contains("inspect-screen", StringComparison.Ordinal));
        string batch = launcher.Commands.Single(command => command.Contains("send-input", StringComparison.Ordinal));
        Assert.Contains("\"requireFocus\":true", Encoding.UTF8.GetString(Convert.FromBase64String(batch.Split(' ').Last())), StringComparison.Ordinal);
    }

    private static InputBatch Batch(IReadOnlyList<InputStep> steps, bool requireFocus = false) =>
        new(steps, "0xA1", requireFocus);

    private static InputStep KeyStep(string keys, int hold = 0, int delayAfter = 0)
    {
        Assert.True(KeyChordParser.TryParse(keys, out IReadOnlyList<KeyStroke> parsed, out string? error), error);
        return new InputStep(InputKinds.Key, null, null, null, null, null, 1, 0, 0, null, parsed, 0, hold, delayAfter);
    }

    private static InputStep WaitStep(int milliseconds) =>
        new(InputKinds.Wait, null, null, null, null, null, 1, 0, 0, null, null, milliseconds);

    private static InputStep TypeStep(string text) =>
        new(InputKinds.Type, null, null, null, null, null, 1, 0, 0, text, null, 0);

    private static long Elapsed(InputTiming timing) =>
        long.Parse(timing.LastSubmitted.QpcTicks, CultureInfo.InvariantCulture)
        - long.Parse(timing.FirstSubmitted.QpcTicks, CultureInfo.InvariantCulture);

    private static void AssertOrdered(InputTiming timing)
    {
        Assert.True(timing.FirstSubmitted.Utc <= timing.LastSubmitted.Utc);
        Assert.True(Elapsed(timing) >= 0);
    }

    private sealed class ScriptedInputPlatform : IInputPlatform
    {
        public const string Target = "0xA1";

        public string? ForegroundHandle { get; set; } = Target;

        public bool WindowOpen { get; set; } = true;

        public bool Inactive { get; set; }

        public long Frequency { get; set; } = 10_000_000;

        public long Ticks { get; set; } = 1_000_000;

        public DateTimeOffset Utc { get; set; } = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

        public Action<int>? OnDelay { get; set; }

        public void StealAfter(int milliseconds, string handle) =>
            OnDelay = delay =>
            {
                if (delay == milliseconds)
                {
                    ForegroundHandle = handle;
                }
            };

        public void CloseAfter(int milliseconds) =>
            OnDelay = delay =>
            {
                if (delay == milliseconds)
                {
                    WindowOpen = false;
                }
            };

        public List<PreparedInput> Submitted { get; } = [];

        public List<string?> FocusAtSubmit { get; } = [];

        public int SessionId => 3;

        public long PerformanceFrequency => Frequency;

        public bool TryReadDesktop(out string? error, out bool inactive)
        {
            inactive = Inactive;
            error = Inactive ? null : null;
            return !Inactive;
        }

        public nint SetDpiAware() => 1;

        public void RestoreDpi(nint previous)
        {
        }

        public bool IsWindow(string handle) =>
            WindowOpen && string.Equals(handle, Target, StringComparison.OrdinalIgnoreCase);

        public bool IsIconic(string handle) => false;

        public void RestoreWindow(string handle)
        {
        }

        public void RequestForeground(string handle)
        {
        }

        public string? Foreground() => ForegroundHandle;

        public void Delay(int milliseconds)
        {
            if (milliseconds <= 0)
            {
                return;
            }

            Ticks += milliseconds * Frequency / 1000;
            Utc = Utc.AddMilliseconds(milliseconds);
            OnDelay?.Invoke(milliseconds);
        }

        public bool TryVirtualScreen(out int left, out int top, out int width, out int height)
        {
            left = 0;
            top = 0;
            width = 1920;
            height = 1080;
            return true;
        }

        public bool TryCursor(out int x, out int y)
        {
            x = 4;
            y = 5;
            return true;
        }

        public (DateTimeOffset Utc, long Ticks) Now() => (Utc, Ticks);

        public (int Sent, int Win32Error) Send(IReadOnlyList<PreparedInput> inputs)
        {
            foreach (PreparedInput input in inputs)
            {
                Submitted.Add(input);
                FocusAtSubmit.Add(ForegroundHandle);
            }

            Ticks += 10;
            Utc = Utc.AddTicks(10);
            return (inputs.Count, 0);
        }
    }

    private sealed class ScriptedHelper(string sendInputJson) : IProcessLauncher
    {
        public List<string> Commands { get; } = [];

        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) => OperationResult<bool>.Ok(true);

        public Task<int> RunAsync(LaunchRequest request, Vestigo.Jobs.JobContext output, CancellationToken cancellationToken)
        {
            string command = string.Join(' ', request.Arguments);
            Commands.Add(command);
            if (command.Contains("send-input", StringComparison.Ordinal))
            {
                File.WriteAllText(output.OutputPath, sendInputJson);
                return Task.FromResult(0);
            }

            File.WriteAllText(output.OutputPath, """{"error":"observation is not under test","errorCode":"helper_failed"}""");
            return Task.FromResult(2);
        }
    }

    private sealed class RecordingLaunch : IProcessLauncher
    {
        public int Runs { get; private set; }

        public string? Desktop { get; private set; }

        public string? Response { get; init; }

        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) => OperationResult<bool>.Ok(true);

        public Task<int> RunAsync(LaunchRequest request, Vestigo.Jobs.JobContext output, CancellationToken cancellationToken)
        {
            Runs++;
            Desktop = request.Desktop;
            if (Response is not null)
            {
                File.WriteAllText(output.OutputPath, Response);
            }

            return Task.FromResult(0);
        }
    }

    internal sealed class UnavailableLauncher : IProcessLauncher
    {
        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) =>
            OperationResult<bool>.Fail("no_interactive_session", "No user is logged on at the console.");

        public Task<int> RunAsync(LaunchRequest request, Vestigo.Jobs.JobContext output, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The launcher must not run.");
    }
}
