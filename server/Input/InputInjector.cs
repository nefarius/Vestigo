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
            if (!ScreenCapture.IsInputDesktopAvailable(out string? desktopError, requireDesktopNames: true))
            {
                return OperationResult<InputResult>.Fail("secure_desktop_active", desktopError!);
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

            int performed = 0;
            foreach (InputStep step in batch.Steps)
            {
                OperationResult<bool> done = Perform(step);
                if (!done.Success)
                {
                    return OperationResult<InputResult>.Fail(
                        done.ErrorCode!,
                        $"{done.Error} ({performed} of {batch.Steps.Count} actions were performed first.)");
                }

                performed++;
                if (step.Kind != InputKinds.Wait)
                {
                    Thread.Sleep(StepPauseMilliseconds);
                }
            }

            return OperationResult<InputResult>.Ok(new InputResult(performed, System.Diagnostics.Process.GetCurrentProcess().SessionId, warning));
        }
        finally
        {
            WindowEnumerator.RestoreDpi(previousDpi);
        }
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

        // A synthetic Alt tap lifts the foreground lock so SetForegroundWindow is honoured.
        _ = SendKeys([new KeyStroke((ushort)VIRTUAL_KEY.VK_MENU, false)], release: false);
        _ = SendKeys([new KeyStroke((ushort)VIRTUAL_KEY.VK_MENU, false)], release: true);
        PInvoke.SetForegroundWindow(hwnd);
        Thread.Sleep(120);
        return PInvoke.GetForegroundWindow() == hwnd
            ? OperationResult<string?>.Ok(null)
            : OperationResult<string?>.Ok("foreground_not_granted: Windows did not bring the window to the foreground; input goes to the current foreground window.");
    }

    private static OperationResult<bool> Perform(InputStep step)
    {
        switch (step.Kind)
        {
            case InputKinds.Wait:
                Thread.Sleep(step.Milliseconds);
                return Done();

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
                return Type(step.Text!);

            case InputKinds.Key:
                return SendKeys(step.Keys!, release: false) is { Success: false } pressFailure
                    ? pressFailure
                    : SendKeys(step.Keys!.Reverse().ToList(), release: true);

            default:
                return OperationResult<bool>.Fail("invalid_request", $"Unknown action '{step.Kind}'.");
        }
    }

    private static OperationResult<bool> Click(InputStep step)
    {
        if (step.X is { } x && step.Y is { } y && MoveTo(x, y) is { Success: false } moveFailure)
        {
            return moveFailure;
        }

        for (int index = 0; index < step.Count; index++)
        {
            OperationResult<bool> down = SendMouse(ButtonFlag(step.Button, up: false), 0, 0, 0);
            if (!down.Success)
            {
                return down;
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

        return SendMouse(ButtonFlag(step.Button, up), 0, 0, 0);
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

    private static OperationResult<bool> Drag(InputStep step)
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
}
