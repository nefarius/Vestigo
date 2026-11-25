using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.StationsAndDesktops;

using Win32PInvoke = Windows.Win32.PInvoke;

namespace Vestigo.Desktop;

/// <summary>
///     Compares the calling thread's desktop with the input desktop. Input injection refuses a secure desktop
///     and a thread that is not already on the input desktop.
/// </summary>
internal static class InputDesktopCheck
{
    private const uint DesktopReadObjects = 0x0001;

    internal static unsafe bool TryReadDesktopRelation(out string? error, out bool inactive, bool requireDesktopNames = false)
    {
        error = null;
        inactive = false;
        HDESK input = Win32PInvoke.OpenInputDesktop(0, false, (DESKTOP_ACCESS_FLAGS)DesktopReadObjects);
        if (input.IsNull)
        {
            error = "The input desktop is not available (lock screen, UAC, or a secure desktop).";
            return false;
        }

        try
        {
            string? inputName = DesktopName((nint)input.Value);
            HDESK current = Win32PInvoke.GetThreadDesktop(Win32PInvoke.GetCurrentThreadId());
            string? currentName = current.IsNull ? null : DesktopName((nint)current.Value);
            if (requireDesktopNames && (string.IsNullOrEmpty(inputName) || string.IsNullOrEmpty(currentName)))
            {
                error = "The input desktop could not be identified, so input was not sent.";
                return false;
            }

            if (SecureDesktopName.IsSecure(inputName))
            {
                error = "The input desktop is not available (lock screen, UAC, or a secure desktop).";
                return false;
            }

            if (!string.IsNullOrEmpty(inputName) &&
                !string.IsNullOrEmpty(currentName) &&
                !string.Equals(inputName, currentName, StringComparison.OrdinalIgnoreCase))
            {
                inactive = true;
                error = "The helper is not running on the input desktop.";
            }

            return true;
        }
        finally
        {
            Win32PInvoke.CloseDesktop(input);
        }
    }

    private static unsafe string? DesktopName(nint handle)
    {
        uint needed = 0;
        Win32PInvoke.GetUserObjectInformation((HANDLE)handle, USER_OBJECT_INFORMATION_INDEX.UOI_NAME, null, 0, &needed);
        if (needed == 0)
        {
            return null;
        }

        byte[] buffer = new byte[needed];
        fixed (byte* pointer = buffer)
        {
            if (!Win32PInvoke.GetUserObjectInformation(
                    (HANDLE)handle,
                    USER_OBJECT_INFORMATION_INDEX.UOI_NAME,
                    pointer,
                    needed,
                    &needed))
            {
                return null;
            }
        }

        return buffer.Length < 2 ? null : MemoryMarshal.Cast<byte, char>(buffer.AsSpan()).ToString().TrimEnd('\0');
    }
}
