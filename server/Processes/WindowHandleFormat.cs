using System.Globalization;

using Windows.Win32.Foundation;

namespace Vestigo.Processes;

/// <summary>Hex HWND text shared by window inspection and input injection.</summary>
internal static class WindowHandleFormat
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
}
