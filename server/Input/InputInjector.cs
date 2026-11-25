using System.ComponentModel;
using System.Globalization;

using Vestigo.Processes;

using Windows.Win32.Foundation;

namespace Vestigo.Input;

/// <summary>
///     Replays an <see cref="InputBatch" /> with <c>SendInput</c> on the calling thread's input desktop. Runs inside
///     the session helper, so the input lands in the console user's session. Mouse positions are absolute
///     virtual-screen physical pixels and need a per-monitor-DPI-aware thread.
/// </summary>
public static class InputInjector
{
    private const int StepPauseMilliseconds = 30;

    private const int ClickPauseMilliseconds = 40;

    private const int DragSteps = 6;

    private const int WheelDelta = 120;

    private const int ErrorAccessDenied = 5;

    private const int FocusSettleMilliseconds = 120;

    public static OperationResult<InputResult> Execute(InputBatch batch)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return OperationResult<InputResult>.Fail("windows_required", "Input injection requires Windows 10 or later.");
        }

        return Execute(batch, new WindowsInputPlatform());
    }

    internal static OperationResult<InputResult> Execute(InputBatch batch, IInputPlatform platform, IKeyboardLayout? keys = null) =>
        new Runner(batch, platform, keys).Run();

    internal static bool? FocusGranted(string? requested, string? actual)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return null;
        }

        return WindowInspection.TryParseHandle(requested, out HWND wanted)
               && WindowInspection.TryParseHandle(actual, out HWND seen)
               && (nint)wanted == (nint)seen;
    }

    private sealed class Runner(InputBatch batch, IInputPlatform platform, IKeyboardLayout? keys)
    {
        private readonly List<HeldInput> _held = [];

        private int _submitted;

        private int _performed;

        private bool _cleanupIncluded;

        private bool _capturedFocus;

        private string? _focusAtStop;

        private InputTimestamp? _first;

        private InputTimestamp? _last;

        public OperationResult<InputResult> Run()
        {
            if (!platform.TryReadDesktop(out string? desktopError, out bool inactive) || inactive)
            {
                return inactive
                    ? OperationResult<InputResult>.Fail(
                        "unsupported_on_inactive_desktop",
                        "The helper is not running on the input desktop.",
                        "desktop_inactive")
                    : OperationResult<InputResult>.Fail("secure_desktop_active", desktopError!);
            }

            nint previousDpi = platform.SetDpiAware();
            try
            {
                return Replay();
            }
            finally
            {
                platform.RestoreDpi(previousDpi);
            }
        }

        private OperationResult<InputResult> Replay()
        {
            string? focus = string.IsNullOrWhiteSpace(batch.FocusWindow) ? null : batch.FocusWindow.Trim();
            if (batch.RequireFocus && (focus is null || !WindowInspection.TryParseHandle(focus, out _)))
            {
                return OperationResult<InputResult>.Fail(
                    "invalid_request",
                    "requireFocus needs focusWindow to be a window handle from list_windows.");
            }

            string? warning = null;
            if (focus is not null)
            {
                if (!platform.IsWindow(focus))
                {
                    if (!batch.RequireFocus)
                    {
                        return OperationResult<InputResult>.Fail("window_not_found", "focusWindow is not an existing window.");
                    }

                    RememberFocus(platform.Foreground());
                    return Finish(InputStopCodes.WindowNotFound, 0, warning: null);
                }

                if (platform.IsIconic(focus))
                {
                    platform.RestoreWindow(focus);
                }

                platform.RequestForeground(focus);
                platform.Delay(FocusSettleMilliseconds);
                bool stillOpen = platform.IsWindow(focus);
                string? foreground = platform.Foreground();
                if (batch.RequireFocus && !stillOpen)
                {
                    RememberFocus(foreground);
                    return Finish(InputStopCodes.WindowNotFound, 0, warning: null);
                }

                if (FocusGranted(focus, foreground) != true)
                {
                    if (batch.RequireFocus)
                    {
                        RememberFocus(foreground);
                        return Finish(InputStopCodes.FocusNotGranted, 0, warning: null);
                    }

                    warning = "foreground_not_granted: Windows did not bring the window to the foreground; input goes to the current foreground window.";
                }
            }

            IReadOnlyList<InputStep> steps = batch.Steps;
            for (int index = 0; index < steps.Count; index++)
            {
                ActionOutcome done = Perform(steps[index]);
                if (done.FocusStopped)
                {
                    return Finish(done.StopCode!, index, warning: null);
                }

                if (!done.Ok)
                {
                    int cleanup = ReleaseHeld();
                    return OperationResult<InputResult>.Fail(
                        done.ErrorCode!,
                        $"{done.Error} ({_performed} of {steps.Count} actions were performed first. {_submitted} Win32 input events were submitted.){CleanupNote(cleanup, _held.Count)}");
                }

                _performed++;
                if (steps[index].DelayAfterMilliseconds > 0)
                {
                    platform.Delay(steps[index].DelayAfterMilliseconds);
                }
                else if (steps[index].Kind != InputKinds.Wait)
                {
                    platform.Delay(StepPauseMilliseconds);
                }
            }

            return Finish(stopCode: null, stopIndex: null, warning);
        }

        private OperationResult<InputResult> Finish(string? stopCode, int? stopIndex, string? warning)
        {
            int submittedBeforeCleanup = _submitted;
            int cleanup = stopCode is null ? 0 : ReleaseHeld();
            string status = stopCode is null
                ? InputInjectionStatus.Submitted
                : submittedBeforeCleanup == 0 ? InputInjectionStatus.Rejected : InputInjectionStatus.Partial;
            string? focus = string.IsNullOrWhiteSpace(batch.FocusWindow) ? null : batch.FocusWindow.Trim();
            string? actual = stopCode is null || !_capturedFocus ? platform.Foreground() : _focusAtStop;
            bool cursorKnown = platform.TryCursor(out int cursorX, out int cursorY);
            InputTiming? timing = _submitted == 0 || _first is null || _last is null
                ? null
                : new InputTiming(
                    InputTiming.QueryPerformanceCounter,
                    platform.PerformanceFrequency.ToString(CultureInfo.InvariantCulture),
                    _cleanupIncluded,
                    _first,
                    _last);
            string? kind = stopIndex is int stopped && stopped >= 0 && stopped < batch.Steps.Count
                ? batch.Steps[stopped].Kind
                : null;
            return OperationResult<InputResult>.Ok(new InputResult(
                _performed,
                platform.SessionId,
                status == InputInjectionStatus.Submitted ? warning : null,
                _submitted,
                focus,
                actual,
                FocusGranted(focus, actual),
                cursorKnown ? cursorX : null,
                cursorKnown ? cursorY : null,
                status,
                stopCode,
                stopCode is null ? null : Reason(stopCode, cleanup, _held.Count),
                stopIndex,
                kind,
                cleanup,
                timing));
        }

        private static string Reason(string stopCode, int cleanup, int stillHeld)
        {
            string reason = stopCode switch
            {
                InputStopCodes.FocusNotGranted => "The requested window was not the foreground window immediately before input submission.",
                InputStopCodes.FocusLost => "The requested window was no longer the foreground window. Later actions were not submitted.",
                InputStopCodes.WindowNotFound => "focusWindow is not an existing window.",
                _ => "Input stopped before the batch finished."
            };
            return reason + CleanupNote(cleanup, stillHeld);
        }

        private static string CleanupNote(int cleanup, int stillHeld)
        {
            if (stillHeld > 0)
            {
                string events = cleanup == 1 ? "event" : "events";
                string inputs = stillHeld == 1 ? "input" : "inputs";
                return $" Cleanup accepted {cleanup} release {events}; {stillHeld} held {inputs} could not be released.";
            }

            return cleanup > 0
                ? " Keys or buttons still held by this batch were released."
                : "";
        }

        private int ReleaseHeld()
        {
            int before = _submitted;
            int guard = _held.Count;
            while (_held.Count > 0 && guard-- > 0)
            {
                List<PreparedInput> releases = new(_held.Count);
                for (int index = _held.Count - 1; index >= 0; index--)
                {
                    releases.Add(_held[index].Release);
                }

                int at = _submitted;
                Submit(releases, checkFocus: false);
                if (_submitted == at)
                {
                    break;
                }
            }

            return _submitted - before;
        }

        private void RememberFocus(string? actual)
        {
            if (_capturedFocus)
            {
                return;
            }

            _focusAtStop = actual;
            _capturedFocus = true;
        }

        private ActionOutcome Perform(InputStep step)
        {
            switch (step.Kind)
            {
                case InputKinds.Wait:
                    platform.Delay(step.Milliseconds);
                    return ActionOutcome.Done();

                case InputKinds.Move:
                    return MoveTo(step.X!.Value, step.Y!.Value);

                case InputKinds.Click:
                    return Click(step);

                case InputKinds.Down:
                    return Button(step, up: false);

                case InputKinds.Up:
                    return Button(step, up: true);

                case InputKinds.Scroll:
                    return Scroll(step);

                case InputKinds.Drag:
                    return Drag(step);

                case InputKinds.Type:
                    return IsKeysMode(step.InputMode) ? TypeKeys(step.Text ?? string.Empty) : Type(step.Text!);

                case InputKinds.Key:
                    if (SendKeys(step.Keys!, release: false) is { Ok: false } pressFailure)
                    {
                        return pressFailure;
                    }

                    if (step.HoldMilliseconds > 0)
                    {
                        platform.Delay(step.HoldMilliseconds);
                    }

                    return SendKeys(step.Keys!.Reverse().ToList(), release: true);

                default:
                    return ActionOutcome.Fail("invalid_request", $"Unknown action '{step.Kind}'.");
            }
        }

        private ActionOutcome Click(InputStep step)
        {
            if (step.X is { } x && step.Y is { } y && MoveTo(x, y) is { Ok: false } moveFailure)
            {
                return moveFailure;
            }

            for (int index = 0; index < step.Count; index++)
            {
                ActionOutcome down = SendMouse(ButtonAction(step.Button, up: false), 0, 0, 0);
                if (!down.Ok)
                {
                    return down;
                }

                if (step.HoldMilliseconds > 0)
                {
                    platform.Delay(step.HoldMilliseconds);
                }

                ActionOutcome up = SendMouse(ButtonAction(step.Button, up: true), 0, 0, 0);
                if (!up.Ok)
                {
                    return up;
                }

                if (index + 1 < step.Count)
                {
                    platform.Delay(ClickPauseMilliseconds);
                }
            }

            return ActionOutcome.Done();
        }

        private ActionOutcome Button(InputStep step, bool up)
        {
            if (step.X is { } x && step.Y is { } y && MoveTo(x, y) is { Ok: false } moveFailure)
            {
                return moveFailure;
            }

            return SendMouse(ButtonAction(step.Button, up), 0, 0, 0);
        }

        private ActionOutcome Scroll(InputStep step)
        {
            if (step.X is { } x && step.Y is { } y && MoveTo(x, y) is { Ok: false } moveFailure)
            {
                return moveFailure;
            }

            if (step.DeltaY != 0)
            {
                ActionOutcome vertical = SendMouse(InputMouseAction.Wheel, 0, 0, unchecked((uint)(-step.DeltaY * WheelDelta)));
                if (!vertical.Ok)
                {
                    return vertical;
                }
            }

            return step.DeltaX != 0
                ? SendMouse(InputMouseAction.HorizontalWheel, 0, 0, unchecked((uint)(step.DeltaX * WheelDelta)))
                : ActionOutcome.Done();
        }

        private ActionOutcome Drag(InputStep step)
        {
            int fromX = step.X!.Value;
            int fromY = step.Y!.Value;
            int toX = step.ToX!.Value;
            int toY = step.ToY!.Value;
            ActionOutcome result = MoveTo(fromX, fromY);
            if (!result.Ok)
            {
                return result;
            }

            result = SendMouse(ButtonAction(step.Button, up: false), 0, 0, 0);
            if (!result.Ok)
            {
                return result;
            }

            platform.Delay(60);
            for (int index = 1; index <= DragSteps; index++)
            {
                int x = fromX + (toX - fromX) * index / DragSteps;
                int y = fromY + (toY - fromY) * index / DragSteps;
                result = MoveTo(x, y);
                if (!result.Ok)
                {
                    return result;
                }

                platform.Delay(20);
            }

            platform.Delay(60);
            return SendMouse(ButtonAction(step.Button, up: true), 0, 0, 0);
        }

        private ActionOutcome Type(string text)
        {
            List<PreparedInput> inputs = [];
            foreach (char c in text)
            {
                switch (c)
                {
                    case '\r':
                        continue;
                    case '\n':
                        inputs.Add(new PreparedKey((ushort)0x0D, 0, false, false, false));
                        inputs.Add(new PreparedKey((ushort)0x0D, 0, true, false, false));
                        break;
                    case '\t':
                        inputs.Add(new PreparedKey((ushort)0x09, 0, false, false, false));
                        inputs.Add(new PreparedKey((ushort)0x09, 0, true, false, false));
                        break;
                    default:
                        inputs.Add(new PreparedKey(0, c, false, true, false));
                        inputs.Add(new PreparedKey(0, c, true, true, false));
                        break;
                }
            }

            return Submit(inputs, checkFocus: true);
        }

        private static bool IsKeysMode(string? inputMode) =>
            !string.IsNullOrWhiteSpace(inputMode) && inputMode.Trim().Equals("keys", StringComparison.OrdinalIgnoreCase);

        private ActionOutcome TypeKeys(string text)
        {
            IKeyboardLayout layout = keys ?? WindowsLayout();
            if (!PhysicalKeyTranslator.TryPlan(text, layout, out PhysicalKeyPlan? plan, out PhysicalKeyFailure? failure))
            {
                return ActionOutcome.Fail(
                    "unsupported_character",
                    $"Unsupported character at position {failure?.Position ?? 0}.");
            }

            OperationResult<int> sent = PhysicalKeySender.Send(
                plan!,
                focusStillHeld: null,
                SendPhysicalStroke,
                ReleasePhysicalModifiers,
                platform.IsKeyDown,
                () => layout.Unchanged()
                    ? null
                    : OperationResult<bool>.Fail(
                        "layout_changed",
                        "The keyboard layout changed before this character was sent."));
            if (sent.Success)
            {
                return ActionOutcome.Done();
            }

            if (sent.ErrorCode is InputStopCodes.FocusLost or InputStopCodes.FocusNotGranted or InputStopCodes.WindowNotFound)
            {
                return ActionOutcome.Stopped(sent.ErrorCode);
            }

            return ActionOutcome.Fail(sent.ErrorCode!, sent.Error!);
        }

        private IKeyboardLayout WindowsLayout()
        {
            string? handle = FocusGranted(batch.FocusWindow, platform.Foreground()) == true
                ? batch.FocusWindow
                : platform.Foreground();
            HWND hwnd = default;
            if (!string.IsNullOrWhiteSpace(handle) && WindowInspection.TryParseHandle(handle, out HWND parsed))
            {
                hwnd = parsed;
            }

            return new WindowsKeyboardLayout(hwnd);
        }

        private OperationResult<int> SendPhysicalStroke(PhysicalKeyStroke stroke)
        {
            List<PreparedInput> inputs = [];
            foreach (ushort vk in PhysicalKeySender.Modifiers(stroke))
            {
                if (!HoldsVirtualKey(vk))
                {
                    inputs.Add(new PreparedKey(vk, 0, false, false, false));
                }
            }

            inputs.Add(new PreparedKey(stroke.Vk, stroke.Scan, false, false, stroke.Extended));
            inputs.Add(new PreparedKey(stroke.Vk, stroke.Scan, true, false, stroke.Extended));
            ActionOutcome sent = Submit(inputs, checkFocus: true);
            if (sent.FocusStopped)
            {
                return OperationResult<int>.Fail(sent.StopCode!, "Focus changed before this character was sent.");
            }

            return sent.Ok
                ? OperationResult<int>.Ok(inputs.Count)
                : OperationResult<int>.Fail(sent.ErrorCode!, sent.Error!);
        }

        private OperationResult<bool> ReleasePhysicalModifiers(IReadOnlyList<ushort> virtualKeys)
        {
            List<PreparedInput> inputs = [];
            foreach (ushort vk in virtualKeys)
            {
                if (HoldsVirtualKey(vk))
                {
                    inputs.Add(new PreparedKey(vk, 0, true, false, false));
                }
            }

            if (inputs.Count == 0)
            {
                return OperationResult<bool>.Ok(true);
            }

            ActionOutcome sent = Submit(inputs, checkFocus: false);
            return sent.Ok
                ? OperationResult<bool>.Ok(true)
                : OperationResult<bool>.Fail(sent.ErrorCode ?? "input_failed", sent.Error ?? "A modifier release was rejected.");
        }

        private bool HoldsVirtualKey(ushort vk) =>
            _held.Exists(held => held.Release is PreparedKey key && !key.Unicode && key.Vk == vk);

        private ActionOutcome SendKeys(IReadOnlyList<KeyStroke> keys, bool release)
        {
            List<PreparedInput> inputs = [];
            foreach (KeyStroke key in keys)
            {
                inputs.Add(new PreparedKey(key.Vk, 0, release, false, key.Extended));
            }

            return Submit(inputs, checkFocus: true);
        }

        private ActionOutcome MoveTo(int x, int y)
        {
            if (!platform.TryVirtualScreen(out int left, out int top, out int width, out int height))
            {
                return ActionOutcome.Fail("desktop_unavailable", "No displays are attached.");
            }

            if (x < left || y < top || x >= left + width || y >= top + height)
            {
                return ActionOutcome.Fail(
                    "invalid_request",
                    $"Point ({x},{y}) is outside the virtual screen ({left},{top} {width}x{height}).");
            }

            int normalizedX = (int)((x - left) * 65535L / (width - 1));
            int normalizedY = (int)((y - top) * 65535L / (height - 1));
            return SendMouse(InputMouseAction.Move, normalizedX, normalizedY, 0);
        }

        private static InputMouseAction ButtonAction(string? button, bool up) => (button, up) switch
        {
            ("right", false) => InputMouseAction.RightDown,
            ("right", true) => InputMouseAction.RightUp,
            ("middle", false) => InputMouseAction.MiddleDown,
            ("middle", true) => InputMouseAction.MiddleUp,
            (_, false) => InputMouseAction.LeftDown,
            _ => InputMouseAction.LeftUp
        };

        private ActionOutcome SendMouse(InputMouseAction action, int dx, int dy, uint data) =>
            Submit([new PreparedMouse(action, dx, dy, data)], checkFocus: true);

        private ActionOutcome Submit(IReadOnlyList<PreparedInput> inputs, bool checkFocus)
        {
            if (inputs.Count == 0)
            {
                return ActionOutcome.Done();
            }

            if (checkFocus && FocusBlocked() is { } stop)
            {
                return ActionOutcome.Stopped(stop);
            }

            (DateTimeOffset utcBefore, long qpcBefore) = platform.Now();
            (int sent, int error) = platform.Send(inputs);
            (DateTimeOffset utcAfter, long qpcAfter) = platform.Now();
            if (sent > 0)
            {
                _first ??= new InputTimestamp(utcBefore, qpcBefore.ToString(CultureInfo.InvariantCulture));
                _last = new InputTimestamp(utcAfter, qpcAfter.ToString(CultureInfo.InvariantCulture));
                _submitted += sent;
                if (!checkFocus)
                {
                    _cleanupIncluded = true;
                }

                Track(inputs, sent);
            }

            if (sent == inputs.Count)
            {
                return ActionOutcome.Done();
            }

            return error == ErrorAccessDenied
                ? ActionOutcome.Fail("uipi_blocked", "Windows blocked the input: the foreground window is elevated or on another desktop. Retry with elevated=true.")
                : ActionOutcome.Fail("input_failed", $"SendInput delivered {sent} of {inputs.Count} events: {new Win32Exception(error).Message}");
        }

        private string? FocusBlocked()
        {
            if (!batch.RequireFocus)
            {
                return null;
            }

            string? focus = batch.FocusWindow?.Trim();
            if (focus is null)
            {
                RememberFocus(platform.Foreground());
                return InputStopCodes.FocusNotGranted;
            }

            bool open = platform.IsWindow(focus);
            string? actual = platform.Foreground();
            if (!open)
            {
                RememberFocus(actual);
                return InputStopCodes.WindowNotFound;
            }

            if (FocusGranted(focus, actual) == true)
            {
                return null;
            }

            RememberFocus(actual);
            return _submitted == 0 ? InputStopCodes.FocusNotGranted : InputStopCodes.FocusLost;
        }

        private void Track(IReadOnlyList<PreparedInput> inputs, int sent)
        {
            int count = Math.Min(sent, inputs.Count);
            for (int index = 0; index < count; index++)
            {
                switch (inputs[index])
                {
                    case PreparedKey key when key.Up:
                        ReleaseLast(held => held.Matches(key));
                        break;
                    case PreparedKey key:
                        HoldOnce(HeldInput.FromKey(key));
                        break;
                    case PreparedMouse mouse when HeldInput.FromMouse(mouse) is { } held:
                        if (mouse.Action is InputMouseAction.LeftUp or InputMouseAction.RightUp or InputMouseAction.MiddleUp)
                        {
                            ReleaseLast(candidate => candidate.Matches(mouse));
                        }
                        else
                        {
                            HoldOnce(held);
                        }

                        break;
                }
            }
        }

        private void HoldOnce(HeldInput held)
        {
            if (!_held.Exists(candidate => candidate.Matches(held.Release)))
            {
                _held.Add(held);
            }
        }

        private void ReleaseLast(Func<HeldInput, bool> match)
        {
            for (int index = _held.Count - 1; index >= 0; index--)
            {
                if (match(_held[index]))
                {
                    _held.RemoveAt(index);
                    return;
                }
            }
        }
    }

    private abstract record HeldInput
    {
        public abstract PreparedInput Release { get; }

        public abstract bool Matches(PreparedInput input);

        public static HeldInput FromKey(PreparedKey key) => new HeldKey(key.Vk, key.Scan, key.Unicode, key.Extended);

        public static HeldInput? FromMouse(PreparedMouse mouse) => mouse.Action switch
        {
            InputMouseAction.LeftDown => new HeldButton(InputMouseAction.LeftUp),
            InputMouseAction.RightDown => new HeldButton(InputMouseAction.RightUp),
            InputMouseAction.MiddleDown => new HeldButton(InputMouseAction.MiddleUp),
            InputMouseAction.LeftUp or InputMouseAction.RightUp or InputMouseAction.MiddleUp => new HeldButton(mouse.Action),
            _ => null
        };
    }

    private sealed record HeldKey(ushort Vk, ushort Scan, bool Unicode, bool Extended) : HeldInput
    {
        public override PreparedInput Release => new PreparedKey(Vk, Scan, true, Unicode, Extended);

        public override bool Matches(PreparedInput input) =>
            input is PreparedKey key
            && key.Up
            && key.Unicode == Unicode
            && key.Vk == Vk
            && key.Scan == Scan
            && key.Extended == Extended;
    }

    private sealed record HeldButton(InputMouseAction Up) : HeldInput
    {
        public override PreparedInput Release => new PreparedMouse(Up, 0, 0, 0);

        public override bool Matches(PreparedInput input) =>
            input is PreparedMouse mouse && mouse.Action == Up;
    }

    private readonly struct ActionOutcome
    {
        public bool Ok { get; private init; }

        public bool FocusStopped { get; private init; }

        public string? StopCode { get; private init; }

        public string? ErrorCode { get; private init; }

        public string? Error { get; private init; }

        public static ActionOutcome Done() => new() { Ok = true };

        public static ActionOutcome Stopped(string stopCode) => new() { FocusStopped = true, StopCode = stopCode };

        public static ActionOutcome Fail(string code, string error) => new() { ErrorCode = code, Error = error };
    }
}
