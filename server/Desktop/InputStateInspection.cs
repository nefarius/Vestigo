using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Vestigo.Desktop;

/// <summary>Foreground window and cursor on the input desktop. This does not inject input.</summary>
public sealed record InputStateReport(
    string? ForegroundHandle,
    int? ForegroundProcessId,
    string? ForegroundTitle,
    int? CursorX,
    int? CursorY,
    string? DesktopName);

public static class InputStateInspection
{
    public static unsafe OperationResult<InputStateReport> Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<InputStateReport>.Fail("windows_required", "Input-state inspection requires Windows.");
        }

        if (!ScreenCapture.TryReadDesktopRelation(out string? desktopError, out bool inactive, requireDesktopNames: true) || inactive)
        {
            return inactive
                ? OperationResult<InputStateReport>.Fail(
                    "unsupported_on_inactive_desktop",
                    "The helper is not running on the input desktop.",
                    "desktop_inactive")
                : OperationResult<InputStateReport>.Fail("secure_desktop_active", desktopError!);
        }

        HWND foreground = PInvoke.GetForegroundWindow();
        string? handle = foreground.IsNull ? null : "0x" + ((nint)foreground.Value).ToString("X");
        int? pid = null;
        string? title = null;
        if (!foreground.IsNull)
        {
            uint processId = 0;
            PInvoke.GetWindowThreadProcessId(foreground, &processId);
            pid = processId == 0 ? null : (int)processId;
            title = ReadTitle(foreground);
        }

        int? cursorX = null;
        int? cursorY = null;
        if (PInvoke.GetCursorPos(out System.Drawing.Point cursor))
        {
            cursorX = cursor.X;
            cursorY = cursor.Y;
        }

        return OperationResult<InputStateReport>.Ok(new InputStateReport(handle, pid, title, cursorX, cursorY, null));
    }

    private static string? ReadTitle(HWND hwnd)
    {
        int length = PInvoke.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return null;
        }

        char[] buffer = new char[length + 1];
        unsafe
        {
            fixed (char* pointer = buffer)
            {
                if (PInvoke.GetWindowText(hwnd, pointer, buffer.Length) <= 0)
                {
                    return null;
                }
            }
        }

        return new string(buffer, 0, length);
    }
}
