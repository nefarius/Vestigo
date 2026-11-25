using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Vestigo.Desktop;
using Vestigo.Processes;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Vestigo.Input;

internal enum InputMouseAction
{
    Move,
    LeftDown,
    LeftUp,
    RightDown,
    RightUp,
    MiddleDown,
    MiddleUp,
    Wheel,
    HorizontalWheel
}

internal abstract record PreparedInput;

internal sealed record PreparedKey(ushort Vk, ushort Scan, bool Up, bool Unicode, bool Extended) : PreparedInput;

internal sealed record PreparedMouse(InputMouseAction Action, int Dx, int Dy, uint Data) : PreparedInput;

/// <summary>Win32 and clock operations used by <see cref="InputInjector" />. Tests supply a scripted implementation.</summary>
internal interface IInputPlatform
{
    int SessionId { get; }

    long PerformanceFrequency { get; }

    bool TryReadDesktop(out string? error, out bool inactive);

    nint SetDpiAware();

    void RestoreDpi(nint previous);

    bool IsWindow(string handle);

    bool IsIconic(string handle);

    void RestoreWindow(string handle);

    void RequestForeground(string handle);

    string? Foreground();

    void Delay(int milliseconds);

    bool TryVirtualScreen(out int left, out int top, out int width, out int height);

    bool TryCursor(out int x, out int y);

    (DateTimeOffset Utc, long Ticks) Now();

    (int Sent, int Win32Error) Send(IReadOnlyList<PreparedInput> inputs);
}

internal sealed class WindowsInputPlatform : IInputPlatform
{
    public WindowsInputPlatform()
    {
        long frequency = 0;
        if (!QueryFrequency(out frequency) || frequency <= 0)
        {
            frequency = Stopwatch.Frequency;
        }

        PerformanceFrequency = frequency;
    }

    public int SessionId => Process.GetCurrentProcess().SessionId;

    public long PerformanceFrequency { get; }

    public bool TryReadDesktop(out string? error, out bool inactive) =>
        ScreenCapture.TryReadDesktopRelation(out error, out inactive, requireDesktopNames: true);

    public nint SetDpiAware() => WindowEnumerator.SetDpiAware();

    public void RestoreDpi(nint previous) => WindowEnumerator.RestoreDpi(previous);

    public bool IsWindow(string handle) =>
        WindowInspection.TryParseHandle(handle, out HWND hwnd) && PInvoke.IsWindow(hwnd);

    public bool IsIconic(string handle) =>
        WindowInspection.TryParseHandle(handle, out HWND hwnd) && PInvoke.IsIconic(hwnd);

    public void RestoreWindow(string handle)
    {
        if (WindowInspection.TryParseHandle(handle, out HWND hwnd))
        {
            PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
        }
    }

    public void RequestForeground(string handle)
    {
        if (WindowInspection.TryParseHandle(handle, out HWND hwnd))
        {
            PInvoke.SetForegroundWindow(hwnd);
        }
    }

    public string? Foreground() => WindowInspection.FormatHandleOrNull(PInvoke.GetForegroundWindow());

    public void Delay(int milliseconds)
    {
        if (milliseconds > 0)
        {
            Thread.Sleep(milliseconds);
        }
    }

    public bool TryVirtualScreen(out int left, out int top, out int width, out int height)
    {
        left = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        top = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
        height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);
        return width > 1 && height > 1;
    }

    public bool TryCursor(out int x, out int y)
    {
        x = 0;
        y = 0;
        if (!PInvoke.GetCursorPos(out System.Drawing.Point cursor))
        {
            return false;
        }

        x = cursor.X;
        y = cursor.Y;
        return true;
    }

    public (DateTimeOffset Utc, long Ticks) Now()
    {
        long ticks = QueryCounter() ?? Stopwatch.GetTimestamp();
        return (DateTimeOffset.UtcNow, ticks);
    }

    public (int Sent, int Win32Error) Send(IReadOnlyList<PreparedInput> inputs)
    {
        if (inputs.Count == 0)
        {
            return (0, 0);
        }

        INPUT[] array = new INPUT[inputs.Count];
        for (int index = 0; index < inputs.Count; index++)
        {
            array[index] = ToNative(inputs[index]);
        }

        uint sent = PInvoke.SendInput(array, Marshal.SizeOf<INPUT>());
        return ((int)sent, sent == (uint)array.Length ? 0 : Marshal.GetLastWin32Error());
    }

    private static INPUT ToNative(PreparedInput input)
    {
        INPUT native = default;
        if (input is PreparedKey key)
        {
            KEYBD_EVENT_FLAGS flags = 0;
            if (key.Up)
            {
                flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
            }

            if (key.Unicode)
            {
                flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE;
            }

            if (key.Extended)
            {
                flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY;
            }

            native.type = INPUT_TYPE.INPUT_KEYBOARD;
            native.Anonymous.ki = new KEYBDINPUT
            {
                wVk = (VIRTUAL_KEY)key.Vk,
                wScan = key.Scan,
                dwFlags = flags
            };
            return native;
        }

        PreparedMouse mouse = (PreparedMouse)input;
        native.type = INPUT_TYPE.INPUT_MOUSE;
        native.Anonymous.mi = new MOUSEINPUT
        {
            dx = mouse.Dx,
            dy = mouse.Dy,
            mouseData = mouse.Data,
            dwFlags = mouse.Action switch
            {
                InputMouseAction.Move => MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK,
                InputMouseAction.LeftDown => MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN,
                InputMouseAction.LeftUp => MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP,
                InputMouseAction.RightDown => MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN,
                InputMouseAction.RightUp => MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP,
                InputMouseAction.MiddleDown => MOUSE_EVENT_FLAGS.MOUSEEVENTF_MIDDLEDOWN,
                InputMouseAction.MiddleUp => MOUSE_EVENT_FLAGS.MOUSEEVENTF_MIDDLEUP,
                InputMouseAction.Wheel => MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL,
                InputMouseAction.HorizontalWheel => MOUSE_EVENT_FLAGS.MOUSEEVENTF_HWHEEL,
                _ => 0
            }
        };
        return native;
    }

    private static unsafe bool QueryFrequency(out long frequency)
    {
        long value = 0;
        bool ok = PInvoke.QueryPerformanceFrequency(&value);
        frequency = value;
        return ok;
    }

    private static unsafe long? QueryCounter()
    {
        long value = 0;
        return PInvoke.QueryPerformanceCounter(&value) ? value : null;
    }
}
