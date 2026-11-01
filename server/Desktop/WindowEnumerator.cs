using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using Vestigo.Processes;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Vestigo.Desktop;

/// <summary>
///     Enumerates windows on the calling thread's desktop. Interactive listings therefore run inside the console
///     user's session through <c>Vestigo.exe --session-helper list-windows</c>.
/// </summary>
public static class WindowEnumerator
{
    public static WindowListResult List(WindowSelector selector)
    {
        IReadOnlyList<DisplayInfo> displays = DisplayTopology.Enumerate();
        Regex? titleRegex = WindowSelector.CompileTitle(selector.TitlePattern);
        nint previousDpi = SetDpiAware();
        EnumState state = new(selector, titleRegex, displays);
        GCHandle handle = GCHandle.Alloc(state);
        try
        {
            unsafe
            {
                PInvoke.EnumWindows(&EnumCallback, (LPARAM)GCHandle.ToIntPtr(handle));
            }
        }
        finally
        {
            handle.Free();
            RestoreDpi(previousDpi);
        }

        if (state.TitleTimeout is not null)
        {
            throw state.TitleTimeout;
        }

        return new WindowListResult(state.Windows, displays, state.Truncated, Process.GetCurrentProcess().SessionId);
    }

    private sealed class EnumState(WindowSelector selector, Regex? titleRegex, IReadOnlyList<DisplayInfo> displays)
    {
        public List<WindowInfo> Windows { get; } = [];

        public bool Truncated { get; private set; }

        public RegexMatchTimeoutException? TitleTimeout { get; set; }

        private readonly Dictionary<int, (string Name, string? Path)> _processes = [];

        public bool Visit(HWND window, bool children)
        {
            bool visible = PInvoke.IsWindowVisible(window);
            if (!selector.IncludeHidden && !visible)
            {
                return EnumerateChildren(window, children);
            }

            uint pid;
            uint threadId;
            unsafe
            {
                threadId = PInvoke.GetWindowThreadProcessId(window, &pid);
            }

            if (pid == 0)
            {
                return EnumerateChildren(window, children);
            }

            if (!_processes.TryGetValue((int)pid, out (string Name, string? Path) process))
            {
                process = (WindowInspection.ProcessName((int)pid), null);
                _processes[(int)pid] = process;
            }

            if (!selector.MatchesProcess((int)pid, process.Name))
            {
                return EnumerateChildren(window, children);
            }

            // Child control text is not a window title; omit it so titleRegex cannot match edit/static contents.
            string title = children ? WindowInspection.Title(window) : string.Empty;
            string className = WindowInspection.ClassName(window);
            try
            {
                if (selector.Matches(title, className, titleRegex))
                {
                    if (Windows.Count >= selector.Limit)
                    {
                        Truncated = true;
                        return false;
                    }

                    if (process.Path is null)
                    {
                        process = (process.Name, WindowInspection.ProcessPath((int)pid));
                        _processes[(int)pid] = process;
                    }

                    Windows.Add(Describe(window, (int)pid, (int)threadId, process.Name, process.Path, title, className, visible));
                }
            }
            catch (RegexMatchTimeoutException ex)
            {
                TitleTimeout = ex;
                return false;
            }

            return EnumerateChildren(window, children);
        }

        private bool EnumerateChildren(HWND window, bool children)
        {
            if (!children || !selector.IncludeChildren || Truncated || TitleTimeout is not null)
            {
                return TitleTimeout is null && !Truncated;
            }

            GCHandle handle = GCHandle.Alloc(this);
            try
            {
                unsafe
                {
                    PInvoke.EnumChildWindows(window, &ChildCallback, (LPARAM)GCHandle.ToIntPtr(handle));
                }
            }
            finally
            {
                handle.Free();
            }

            return TitleTimeout is null && !Truncated;
        }

        private WindowInfo Describe(
            HWND window,
            int pid,
            int threadId,
            string processName,
            string? processPath,
            string title,
            string className,
            bool visible)
        {
            HWND desktop = PInvoke.GetDesktopWindow();
            HWND parent = PInvoke.GetAncestor(window, GET_ANCESTOR_FLAGS.GA_PARENT);
            HWND owner = PInvoke.GetWindow(window, GET_WINDOW_CMD.GW_OWNER);
            HWND rootOwner = PInvoke.GetAncestor(window, GET_ANCESTOR_FLAGS.GA_ROOTOWNER);

            PInvoke.GetWindowRect(window, out RECT bounds);
            PInvoke.GetClientRect(window, out RECT client);
            DesktopRect rectangle = new(bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top);

            string? nearest = NearestDisplay(window, displays);
            List<string> intersecting = displays
                .Where(display => rectangle.Width > 0 && rectangle.Height > 0 && rectangle.Intersects(display.Bounds))
                .Select(display => display.GdiDeviceName)
                .ToList();

            return new WindowInfo(
                WindowInspection.FormatHandle(window),
                title,
                className,
                pid,
                threadId,
                processName,
                processPath,
                parent.IsNull || parent == desktop || parent == window ? null : WindowInspection.FormatHandle(parent),
                WindowInspection.FormatHandleOrNull(owner),
                rootOwner.IsNull || rootOwner == window ? null : WindowInspection.FormatHandle(rootOwner),
                visible,
                PInvoke.IsIconic(window),
                PInvoke.IsZoomed(window),
                IsCloaked(window),
                rectangle,
                ExtendedFrameBounds(window),
                new DesktopSize(client.right - client.left, client.bottom - client.top),
                WindowDpi(window),
                nearest,
                intersecting);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL EnumCallback(HWND window, LPARAM parameter)
    {
        EnumState state = (EnumState)GCHandle.FromIntPtr((nint)parameter.Value).Target!;
        try
        {
            return state.Visit(window, children: true);
        }
        catch (RegexMatchTimeoutException ex)
        {
            state.TitleTimeout = ex;
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL ChildCallback(HWND window, LPARAM parameter)
    {
        EnumState state = (EnumState)GCHandle.FromIntPtr((nint)parameter.Value).Target!;
        try
        {
            return state.Visit(window, children: false);
        }
        catch (RegexMatchTimeoutException ex)
        {
            state.TitleTimeout = ex;
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static unsafe bool IsCloaked(HWND window)
    {
        uint cloaked = 0;
        return PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint)).Succeeded
               && cloaked != 0;
    }

    private static unsafe DesktopRect? ExtendedFrameBounds(HWND window)
    {
        RECT bounds = default;
        if (!PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &bounds, (uint)sizeof(RECT)).Succeeded)
        {
            return null;
        }

        return new DesktopRect(bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top);
    }

    private static int WindowDpi(HWND window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            return 96;
        }

        uint dpi = PInvoke.GetDpiForWindow(window);
        return dpi == 0 ? 96 : (int)dpi;
    }

    private static string? NearestDisplay(HWND window, IReadOnlyList<DisplayInfo> displays)
    {
        HMONITOR monitor = PInvoke.MonitorFromWindow(window, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (monitor.IsNull)
        {
            return displays.FirstOrDefault(display => display.Primary)?.GdiDeviceName
                   ?? displays.FirstOrDefault()?.GdiDeviceName;
        }

        if (!DisplayTopology.TryGetMonitorInfo(monitor, out MONITORINFOEXW info))
        {
            return null;
        }

        string device = info.szDevice.AsReadOnlySpan().ToString().TrimEnd('\0');
        return string.IsNullOrEmpty(device) ? null : device;
    }

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
