using System.Diagnostics;
using System.Globalization;

using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Vestigo.Processes;

/// <summary>Shared read helpers for window title, class, and owning process.</summary>
internal static class WindowInspection
{
    public static unsafe string FormatHandle(HWND window) =>
        window.IsNull ? string.Empty : "0x" + ((nuint)window.Value).ToString("X");

    public static string? FormatHandleOrNull(HWND window) =>
        window.IsNull ? null : FormatHandle(window);

    public static bool TryParseHandle(string? text, out HWND window)
    {
        window = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> span = text.Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        if (!nuint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out nuint value) || value == 0)
        {
            return false;
        }

        window = (HWND)(nint)value;
        return true;
    }

    public static unsafe string Title(HWND window)
    {
        // Always time out: GetWindowText sends WM_GETTEXT to another thread, even in-process.
        char[] buffer = new char[512];
        fixed (char* pointer = buffer)
        {
            LRESULT sent = PInvoke.SendMessageTimeout(
                window,
                PInvoke.WM_GETTEXT,
                (WPARAM)(nuint)buffer.Length,
                (LPARAM)(nint)pointer,
                SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG,
                50,
                out nuint copied);
            if (sent.Value == 0 || copied == 0)
            {
                return string.Empty;
            }

            return new string(buffer, 0, Math.Min((int)copied, buffer.Length - 1));
        }
    }

    public static unsafe string ClassName(HWND window)
    {
        char[] buffer = new char[256];
        fixed (char* pointer = buffer)
        {
            int copied = PInvoke.GetClassName(window, new PWSTR(pointer), buffer.Length);
            return copied <= 0 ? string.Empty : new string(buffer, 0, copied);
        }
    }

    public static string ProcessName(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    public static string? ProcessPath(int pid)
    {
        HANDLE handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle.IsNull)
        {
            return null;
        }

        using SafeFileHandle process = new(handle, ownsHandle: true);
        uint length = 32768;
        Span<char> buffer = stackalloc char[(int)length];
        if (!PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref length) || length == 0)
        {
            return null;
        }

        return new string(buffer[..(int)length]);
    }
}
