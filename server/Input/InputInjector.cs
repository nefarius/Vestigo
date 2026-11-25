using System.ComponentModel;
using System.Runtime.InteropServices;

using Vestigo.Desktop;
using Vestigo.Processes;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Vestigo.Input;

/// <summary>
///     Replays an <see cref="InputBatch" /> with <c>SendInput</c> on the calling thread's input desktop. Runs inside
///     the session helper, so the input lands in the console user's session. Mouse positions are absolute
///     virtual-screen physical pixels and need a per-monitor-DPI-aware thread.
/// </summary>
public static class InputInjector
{
    [ThreadStatic]
    private static int Submitted;

    [ThreadStatic]
    private static List<ushort>? ActiveModifiers;

    [ThreadStatic]
    private static List<string>? ActiveButtons;

    private const int StepPauseMilliseconds = 30;

    private const int ClickPauseMilliseconds = 40;

    private const int DragSteps = 6;

    private const int WheelDelta = 120;

    private const int ErrorAccessDenied = 5;

    public static OperationResult<InputResult> Execute(InputBatch batch)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return OperationResult<InputResult>.Fail("windows_required", "Input injection requires Windows 10 or later.");
        }

        nint previousDpi = WindowEnumerator.SetDpiAware();
        try
        {
            if (!ScreenCapture.TryReadDesktopRelation(out string? desktopError, out bool inactive, requireDesktopNames: true) || inactive)
            {
                return inactive
                    ? OperationResult<InputResult>.Fail(
                        "unsupported_on_inactive_desktop",
                        "The helper is not running on the input desktop.",
                        "desktop_inactive")
                    : OperationResult<InputResult>.Fail("secure_desktop_active", desktopError!);
            }

            string? warning = null;
            if (!string.IsNullOrWhiteSpace(batch.FocusWindow))
            {
                OperationResult<string?> focused = Focus(batch.FocusWindow);
                if (!focused.Success)
                {
                    return OperationResult<InputResult>.Fail(focused.ErrorCode!, focused.Error!);
                }

                warning = focused.Value;
            }

            if (batch.StrictFocus && (warning is not null || FocusGranted(batch.FocusWindow, ReadForeground()) != true))
            {
                return OperationResult<InputResult>.Fail(
                    "foreground_not_granted",
                    "strictFocus requires the focus window to be in the foreground, and Windows did not grant it.");
            }

            Submitted = 0;
            ActiveModifiers = [];
            ActiveButtons = [];
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            int performed = 0;
            OperationResult<InputResult> result = OperationResult<InputResult>.Fail("input_failed", "Input stopped before completion.");
            try
            {
                bool stopped = false;
                foreach (InputStep step in batch.Steps)
                {
                    if (batch.StrictFocus && FocusGranted(batch.FocusWindow, ReadForeground()) != true)
                    {
                        result = FocusLost(batch, performed);
                        stopped = true;
                        break;
                    }

                    OperationResult<bool> done = Perform(step, batch);
                    if (!done.Success)
                    {
                        result = OperationResult<InputResult>.Fail(
                            done.ErrorCode!,
                            $"{done.Error} ({performed} of {batch.Steps.Count} actions were performed first. {Submitted} Win32 input events were submitted.)");
                        stopped = true;
                        break;
                    }

                    performed++;
                    if (step.DelayAfterMilliseconds > 0)
                    {
                        Thread.Sleep(step.DelayAfterMilliseconds);
                    }
                    else if (step.Kind != InputKinds.Wait)
                    {
                        Thread.Sleep(StepPauseMilliseconds);
                    }
                }

                if (!stopped)
                {
                    string? actualFocus = ReadForeground();
                    bool? granted = FocusGranted(batch.FocusWindow, actualFocus);
                    if (batch.StrictFocus && granted != true)
                    {
                        result = FocusLost(batch, performed);
                    }
                    else
                    {
                        ReadCursor(out int? cursorX, out int? cursorY);
                        result = OperationResult<InputResult>.Ok(new InputResult(
                            performed,
                            System.Diagnostics.Process.GetCurrentProcess().SessionId,
                            warning,
                            Submitted,
                            string.IsNullOrWhiteSpace(batch.FocusWindow) ? null : batch.FocusWindow.Trim(),
                            actualFocus,
                            granted,
                            cursorX,
                            cursorY,
                            startedAt,
                            DateTimeOffset.UtcNow));
                    }
                }
            }
            finally
            {
                OperationResult<bool> released = ReleaseActiveModifiers();
                if (result.Success && !released.Success)
                {
                    result = OperationResult<InputResult>.Fail(
                        released.ErrorCode!,
                        $"{released.Error} ({performed} of {batch.Steps.Count} actions were performed first. {Submitted} Win32 input events were submitted.)");
                }

                if (!result.Success)
                {
                    ReleaseActiveButtons();
                }

                ActiveModifiers = null;
                ActiveButtons = null;
            }

            return result;
        }
        finally
        {
            WindowEnumerator.RestoreDpi(previousDpi);
        }
    }

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

    private static OperationResult<string?> Focus(string handleText)
    {
        if (!WindowInspection.TryParseHandle(handleText, out HWND hwnd) || !PInvoke.IsWindow(hwnd))
        {
            return OperationResult<string?>.Fail("window_not_found", "focusWindow is not an existing window.");
        }

        if (PInvoke.IsIconic(hwnd))
        {
            PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
        }

        PInvoke.SetForegroundWindow(hwnd);
        Thread.Sleep(120);
        return PInvoke.GetForegroundWindow() == hwnd
            ? OperationResult<string?>.Ok(null)
            : OperationResult<string?>.Ok("foreground_not_granted: Windows did not bring the window to the foreground; input goes to the current foreground window.");
    }

    private static OperationResult<bool> Perform(InputStep step, InputBatch batch)
    {
        switch (step.Kind)
        {
            case InputKinds.Wait:
                Thread.Sleep(step.Milliseconds);
                return Done();

            case InputKinds.Move:
                return MoveTo(step.X!.Value, step.Y!.Value);

            case InputKinds.Click:
                return Click(step, batch);

            case InputKinds.Down:
                return Button(step, up: false);

            case InputKinds.Up:
                return Button(step, up: true);

            case InputKinds.Scroll:
                return Scroll(step);

            case InputKinds.Drag:
                return Drag(step, batch);

            case InputKinds.Type:
                if (IsKeysMode(step.InputMode))
                {
                    return TypeKeys(step.Text ?? string.Empty, batch);
                }

                if (IsUnicodeMode(step.InputMode))
                {
                    return Type(step.Text!);
                }

                return OperationResult<bool>.Fail("invalid_request", "inputMode must be unicode or keys.");

            case InputKinds.Key:
                if (SendKeys(step.Keys!, release: false) is { Success: false } pressFailure)
                {
                    return pressFailure;
                }

                if (step.HoldMilliseconds > 0)
                {
                    Thread.Sleep(step.HoldMilliseconds);
                }

                OperationResult<bool> released = SendKeys(step.Keys!.Reverse().ToList(), release: true);
                return released.Success ? RequireFocus(batch) : released;

            default:
                return OperationResult<bool>.Fail("invalid_request", $"Unknown action '{step.Kind}'.");
        }
    }

    private static OperationResult<bool> Click(InputStep step, InputBatch batch)
    {
        if (step.X is { } x && step.Y is { } y && MoveTo(x, y) is { Success: false } moveFailure)
        {
            return moveFailure;
        }

        for (int index = 0; index < step.Count; index++)
        {
            if (index > 0)
            {
                OperationResult<bool> stillFocused = RequireFocus(batch);
                if (!stillFocused.Success)
                {
                    return stillFocused;
                }
            }

            OperationResult<bool> down = SendMouse(ButtonFlag(step.Button, up: false), 0, 0, 0);
            if (!down.Success)
            {
                return down;
            }

            if (step.HoldMilliseconds > 0)
            {
                Thread.Sleep(step.HoldMilliseconds);
            }

            OperationResult<bool> up = SendMouse(ButtonFlag(step.Button, up: true), 0, 0, 0);
            if (!up.Success)
            {
                return up;
            }

            if (index + 1 < step.Count)
            {
                Thread.Sleep(ClickPauseMilliseconds);
            }
        }

        return Done();
    }

    private static OperationResult<bool> Button(InputStep step, bool up)
    {
        if (step.X is { } x && step.Y is { } y && MoveTo(x, y) is { Success: false } moveFailure)
        {
            return moveFailure;
        }

        OperationResult<bool> sent = SendMouse(ButtonFlag(step.Button, up), 0, 0, 0);
        if (sent.Success && ActiveButtons is not null)
        {
            RememberButton(ActiveButtons, step.Button, up);
        }

        return sent;
    }

    private static OperationResult<bool> Scroll(InputStep step)
    {
        if (step.X is { } x && step.Y is { } y && MoveTo(x, y) is { Success: false } moveFailure)
        {
            return moveFailure;
        }

        if (step.DeltaY != 0)
        {
            // Positive wheel data scrolls up, and the contract says positive scrolls down.
            OperationResult<bool> vertical = SendMouse(MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(-step.DeltaY * WheelDelta)));
            if (!vertical.Success)
            {
                return vertical;
            }
        }

        return step.DeltaX != 0
            ? SendMouse(MOUSE_EVENT_FLAGS.MOUSEEVENTF_HWHEEL, 0, 0, unchecked((uint)(step.DeltaX * WheelDelta)))
            : Done();
    }

    private static OperationResult<bool> Drag(InputStep step, InputBatch batch)
    {
        int fromX = step.X!.Value;
        int fromY = step.Y!.Value;
        int toX = step.ToX!.Value;
        int toY = step.ToY!.Value;
        OperationResult<bool> result = MoveTo(fromX, fromY);
        if (!result.Success)
        {
            return result;
        }

        result = SendMouse(ButtonFlag(step.Button, up: false), 0, 0, 0);
        if (!result.Success)
        {
            return result;
        }

        try
        {
            Thread.Sleep(60);
            for (int index = 1; index <= DragSteps; index++)
            {
                OperationResult<bool> stillFocused = RequireFocus(batch);
                if (!stillFocused.Success)
                {
                    return stillFocused;
                }

                int x = fromX + (toX - fromX) * index / DragSteps;
                int y = fromY + (toY - fromY) * index / DragSteps;
                result = MoveTo(x, y);
                if (!result.Success)
                {
                    return result;
                }

                Thread.Sleep(20);
            }

            Thread.Sleep(60);
        }
        finally
        {
            // Always release so a failed drag does not leave the button held down.
            _ = SendMouse(ButtonFlag(step.Button, up: true), 0, 0, 0);
        }

        return Done();
    }

    private static OperationResult<bool> Type(string text)
    {
        List<INPUT> inputs = [];
        foreach (char c in text)
        {
            switch (c)
            {
                case '\r':
                    continue;
                case '\n':
                    inputs.Add(Key((ushort)VIRTUAL_KEY.VK_RETURN, 0, up: false));
                    inputs.Add(Key((ushort)VIRTUAL_KEY.VK_RETURN, 0, up: true));
                    break;
                case '\t':
                    inputs.Add(Key((ushort)VIRTUAL_KEY.VK_TAB, 0, up: false));
                    inputs.Add(Key((ushort)VIRTUAL_KEY.VK_TAB, 0, up: true));
                    break;
                default:
                    inputs.Add(Key(0, c, up: false, unicode: true));
                    inputs.Add(Key(0, c, up: true, unicode: true));
                    break;
            }
        }

        return Send(inputs);
    }

    private static bool IsUnicodeMode(string? inputMode) =>
        string.IsNullOrWhiteSpace(inputMode) || inputMode.Trim().Equals("unicode", StringComparison.OrdinalIgnoreCase);

    private static bool IsKeysMode(string? inputMode) =>
        !string.IsNullOrWhiteSpace(inputMode) && inputMode.Trim().Equals("keys", StringComparison.OrdinalIgnoreCase);

    private static OperationResult<bool> TypeKeys(string text, InputBatch batch)
    {
        WindowsKeyboardLayout layout = new(LayoutWindow(batch));
        if (!PhysicalKeyTranslator.TryPlan(text, layout, out PhysicalKeyPlan? plan, out PhysicalKeyFailure? failure))
        {
            return OperationResult<bool>.Fail(
                "unsupported_character",
                $"Unsupported character at position {failure?.Position ?? 0}.");
        }

        List<ushort> down = ActiveModifiers ??= [];
        Func<bool>? focusStillHeld = batch.StrictFocus
            ? () => FocusGranted(batch.FocusWindow, ReadForeground()) == true
            : null;
        HWND layoutWindow = LayoutWindow(batch);
        OperationResult<int> sent = PhysicalKeySender.Send(
            plan!,
            focusStillHeld,
            stroke => SendPhysicalStroke(stroke, down),
            ReleaseNamedModifiers,
            UserHoldsModifier,
            () => layout.SameLayout(layoutWindow)
                ? null
                : OperationResult<bool>.Fail(
                    "layout_changed",
                    "The keyboard layout changed before this character was sent."));
        return sent.Success ? Done() : OperationResult<bool>.Fail(sent.ErrorCode!, sent.Error!);
    }

    private static HWND LayoutWindow(InputBatch batch)
    {
        if (FocusGranted(batch.FocusWindow, ReadForeground()) == true
            && WindowInspection.TryParseHandle(batch.FocusWindow, out HWND hwnd))
        {
            return hwnd;
        }

        return PInvoke.GetForegroundWindow();
    }

    private static bool UserHoldsModifier(ushort virtualKey) => (PInvoke.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static OperationResult<InputResult> FocusLost(InputBatch batch, int performed) =>
        OperationResult<InputResult>.Fail(
            "focus_lost",
            $"The focus window is no longer in the foreground. ({performed} of {batch.Steps.Count} actions were performed first. {Submitted} Win32 input events were submitted.)");

    private static OperationResult<bool> RequireFocus(InputBatch batch) =>
        !batch.StrictFocus || FocusGranted(batch.FocusWindow, ReadForeground()) == true
            ? Done()
            : OperationResult<bool>.Fail("focus_lost", "The focus window is no longer in the foreground.");

    private static OperationResult<int> SendPhysicalStroke(PhysicalKeyStroke stroke, List<ushort> down)
    {
        ushort[] needed = PhysicalKeySender.Modifiers(stroke);
        List<ushort> pressing = [];
        List<INPUT> inputs = [];
        foreach (ushort vk in needed)
        {
            if (!down.Contains(vk))
            {
                pressing.Add(vk);
                inputs.Add(Key(vk, 0, up: false));
            }
        }

        inputs.Add(Key(stroke.Vk, stroke.Scan, up: false, extended: stroke.Extended));
        inputs.Add(Key(stroke.Vk, stroke.Scan, up: true, extended: stroke.Extended));
        int before = Submitted;
        OperationResult<bool> sent = Send(inputs);
        int delivered = Submitted - before;
        if (!sent.Success)
        {
            RememberPartialStroke(down, pressing, stroke, delivered);
            ReleaseTrackedKeys(down, KeysToRelease(pressing, stroke, delivered));
            return OperationResult<int>.Fail(sent.ErrorCode!, sent.Error!);
        }

        down.AddRange(pressing);
        return OperationResult<int>.Ok(inputs.Count);
    }

    internal static void RememberPartialStroke(List<ushort> down, IReadOnlyList<ushort> pressing, PhysicalKeyStroke stroke, int delivered)
    {
        int modifiersAccepted = Math.Min(Math.Max(delivered, 0), pressing.Count);
        for (int index = 0; index < modifiersAccepted; index++)
        {
            if (!down.Contains(pressing[index]))
            {
                down.Add(pressing[index]);
            }
        }

        if (delivered > pressing.Count && !down.Contains(stroke.Vk))
        {
            down.Add(stroke.Vk);
        }
    }

    internal static List<ushort> KeysToRelease(IReadOnlyList<ushort> pressing, PhysicalKeyStroke stroke, int delivered)
    {
        List<ushort> order = [];
        int modifiersAccepted = Math.Min(Math.Max(delivered, 0), pressing.Count);
        for (int index = modifiersAccepted - 1; index >= 0; index--)
        {
            order.Add(pressing[index]);
        }

        if (delivered > pressing.Count)
        {
            order.Add(stroke.Vk);
        }

        return order;
    }

    private static void ReleaseTrackedKeys(List<ushort> down, IReadOnlyList<ushort> virtualKeys)
    {
        if (virtualKeys.Count == 0)
        {
            return;
        }

        List<INPUT> ups = [];
        foreach (ushort vk in virtualKeys)
        {
            ups.Add(Key(vk, 0, up: true));
        }

        int before = Submitted;
        _ = Send(ups);
        int accepted = Math.Min(Submitted - before, virtualKeys.Count);
        for (int index = 0; index < accepted; index++)
        {
            down.Remove(virtualKeys[index]);
        }
    }

    internal static void RememberButton(List<string> held, string? button, bool up)
    {
        string name = button switch
        {
            "right" => "right",
            "middle" => "middle",
            _ => "left"
        };
        if (up)
        {
            held.Remove(name);
        }
        else if (!held.Contains(name))
        {
            held.Add(name);
        }
    }

    internal static List<ushort> PendingReleases(IReadOnlyList<ushort> requested, IReadOnlyList<ushort> active)
    {
        List<ushort> pending = [];
        foreach (ushort vk in requested)
        {
            if (active.Contains(vk))
            {
                pending.Add(vk);
            }
        }

        return pending;
    }

    private static void ReleaseActiveButtons()
    {
        if (ActiveButtons is not { Count: > 0 } held)
        {
            return;
        }

        for (int index = held.Count - 1; index >= 0; index--)
        {
            _ = SendMouse(ButtonFlag(held[index], up: true), 0, 0, 0);
        }

        held.Clear();
    }

    private static OperationResult<bool> ReleaseNamedModifiers(IReadOnlyList<ushort> virtualKeys)
    {
        if (virtualKeys.Count == 0 || ActiveModifiers is null)
        {
            return Done();
        }

        List<ushort> pending = PendingReleases(virtualKeys, ActiveModifiers);
        if (pending.Count == 0)
        {
            return Done();
        }

        List<INPUT> inputs = [];
        foreach (ushort vk in pending)
        {
            inputs.Add(Key(vk, 0, up: true));
        }

        int before = Submitted;
        OperationResult<bool> sent = Send(inputs);
        int accepted = Math.Min(Math.Max(Submitted - before, 0), pending.Count);
        for (int index = 0; index < accepted; index++)
        {
            ActiveModifiers.Remove(pending[index]);
        }

        return sent;
    }

    private static OperationResult<bool> ReleaseActiveModifiers()
    {
        if (ActiveModifiers is not { Count: > 0 } held)
        {
            return Done();
        }

        ushort[] virtualKeys = new ushort[held.Count];
        for (int index = 0; index < held.Count; index++)
        {
            virtualKeys[index] = held[held.Count - 1 - index];
        }

        return ReleaseNamedModifiers(virtualKeys);
    }

    private static OperationResult<bool> SendKeys(IReadOnlyList<KeyStroke> keys, bool release)
    {
        List<INPUT> inputs = [];
        foreach (KeyStroke key in keys)
        {
            inputs.Add(Key(key.Vk, 0, release, extended: key.Extended));
        }

        return Send(inputs);
    }

    private static INPUT Key(ushort vk, ushort scan, bool up, bool unicode = false, bool extended = false)
    {
        KEYBD_EVENT_FLAGS flags = 0;
        if (up)
        {
            flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
        }

        if (unicode)
        {
            flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE;
        }

        if (extended)
        {
            flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY;
        }

        INPUT input = default;
        input.type = INPUT_TYPE.INPUT_KEYBOARD;
        input.Anonymous.ki = new KEYBDINPUT
        {
            wVk = (VIRTUAL_KEY)vk,
            wScan = scan,
            dwFlags = flags
        };
        return input;
    }

    private static OperationResult<bool> MoveTo(int x, int y)
    {
        int left = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        int top = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        int width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
        int height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);
        if (width <= 1 || height <= 1)
        {
            return OperationResult<bool>.Fail("desktop_unavailable", "No displays are attached.");
        }

        if (x < left || y < top || x >= left + width || y >= top + height)
        {
            return OperationResult<bool>.Fail(
                "invalid_request",
                $"Point ({x},{y}) is outside the virtual screen ({left},{top} {width}x{height}).");
        }

        int normalizedX = (int)((x - left) * 65535L / (width - 1));
        int normalizedY = (int)((y - top) * 65535L / (height - 1));
        return SendMouse(
            MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK,
            normalizedX,
            normalizedY,
            0);
    }

    private static MOUSE_EVENT_FLAGS ButtonFlag(string? button, bool up) => (button, up) switch
    {
        ("right", false) => MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN,
        ("right", true) => MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP,
        ("middle", false) => MOUSE_EVENT_FLAGS.MOUSEEVENTF_MIDDLEDOWN,
        ("middle", true) => MOUSE_EVENT_FLAGS.MOUSEEVENTF_MIDDLEUP,
        (_, false) => MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN,
        _ => MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP
    };

    private static OperationResult<bool> SendMouse(MOUSE_EVENT_FLAGS flags, int dx, int dy, uint data)
    {
        INPUT input = default;
        input.type = INPUT_TYPE.INPUT_MOUSE;
        input.Anonymous.mi = new MOUSEINPUT
        {
            dx = dx,
            dy = dy,
            mouseData = data,
            dwFlags = flags
        };
        return Send([input]);
    }

    private static OperationResult<bool> Send(List<INPUT> inputs)
    {
        if (inputs.Count == 0)
        {
            return Done();
        }

        INPUT[] array = [.. inputs];
        uint sent = PInvoke.SendInput(array, Marshal.SizeOf<INPUT>());
        Submitted += (int)sent;
        if (sent == array.Length)
        {
            return Done();
        }

        int error = Marshal.GetLastWin32Error();
        return error == ErrorAccessDenied
            ? OperationResult<bool>.Fail("uipi_blocked", "Windows blocked the input: the foreground window is elevated or on another desktop. Retry with elevated=true.")
            : OperationResult<bool>.Fail("input_failed", $"SendInput delivered {sent} of {array.Length} events: {new Win32Exception(error).Message}");
    }

    private static OperationResult<bool> Done() => OperationResult<bool>.Ok(true);

    private static unsafe string? ReadForeground()
    {
        HWND foreground = PInvoke.GetForegroundWindow();
        return foreground.IsNull ? null : "0x" + ((nint)foreground.Value).ToString("X");
    }

    private static void ReadCursor(out int? x, out int? y)
    {
        x = null;
        y = null;
        if (PInvoke.GetCursorPos(out System.Drawing.Point cursor))
        {
            x = cursor.X;
            y = cursor.Y;
        }
    }
}
