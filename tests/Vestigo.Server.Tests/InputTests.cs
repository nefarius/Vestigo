using System.Text.Json;

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

    private static InputActionSpec Spec(string type) => new() { Type = type };

    private static InputActionSpec Wait(int ms) => new() { Type = "wait", Milliseconds = ms };

    private static InputActionSpec Type(string text) => new() { Type = "type", Text = text };

    internal sealed class UnavailableLauncher : IProcessLauncher
    {
        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) =>
            OperationResult<bool>.Fail("no_interactive_session", "No user is logged on at the console.");

        public Task<int> RunAsync(LaunchRequest request, Vestigo.Jobs.JobContext output, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The launcher must not run.");
    }
}
