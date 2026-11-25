using Windows.Win32;
using Windows.Win32.UI.HiDpi;

namespace Vestigo.Desktop;

/// <summary>Per-monitor v2 DPI awareness for the calling thread, restored after the input or enumeration call.</summary>
internal static class ThreadDpiAwareness
{
    internal static nint SetDpiAware()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            return 0;
        }

        return (nint)PInvoke.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }

    internal static void RestoreDpi(nint previous)
    {
        if (previous == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            return;
        }

        PInvoke.SetThreadDpiAwarenessContext((DPI_AWARENESS_CONTEXT)previous);
    }
}
