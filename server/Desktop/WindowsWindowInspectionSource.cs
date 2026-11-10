using System.ComponentModel;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

using Vestigo.Processes;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Vestigo.Desktop;

/// <summary>CsWin32-backed window reads. Geometry is collected only after per-monitor v2 is verified.</summary>
public sealed class WindowsWindowInspectionSource : IWindowInspectionSource
{
    public bool TryParseHandle(string? text, out nint handle)
    {
        if (!WindowInspection.TryParseHandle(text, out HWND window))
        {
            handle = 0;
            return false;
        }

        handle = window;
        return true;
    }

    public bool IsWindow(nint handle) => PInvoke.IsWindow(new HWND(handle));

    public OperationResult<WindowProcessIdentity> ReadIdentity(nint handle)
    {
        uint threadId = PInvoke.GetWindowThreadProcessId(new HWND(handle), out uint pid);
        if (pid == 0 || threadId == 0)
        {
            return OperationResult<WindowProcessIdentity>.Fail(
                "window_not_found",
                "The window handle has no owning process (" + Marshal.GetLastWin32Error() + ").");
        }

        (bool readable, DateTimeOffset? start, int? error) = ReadStart((int)pid);
        return OperationResult<WindowProcessIdentity>.Ok(
            new WindowProcessIdentity((int)pid, (int)threadId, readable, start, error));
    }

    public PhysicalDpiLease EnterPhysicalCoordinates()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            return new PhysicalDpiLease
            {
                Established = false,
                ErrorCode = "dpi_context_unavailable",
                Error = "Per-monitor DPI awareness v2 requires Windows 10 version 1607 or later."
            };
        }

        DPI_AWARENESS_CONTEXT previous = PInvoke.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        DPI_AWARENESS_CONTEXT current = PInvoke.GetThreadDpiAwarenessContext();
        bool established = PInvoke.AreDpiAwarenessContextsEqual(
            current,
            DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (!established)
        {
            if (!previous.IsNull)
            {
                PInvoke.SetThreadDpiAwarenessContext(previous);
            }

            return new PhysicalDpiLease
            {
                Established = false,
                ErrorCode = "dpi_context_unavailable",
                Error = "Windows did not enter per-monitor DPI awareness v2 on this thread.",
                NativeError = Marshal.GetLastWin32Error()
            };
        }

        return new PhysicalDpiLease
        {
            Established = true,
            Actual = DesktopCoordinates.PerMonitorV2,
            Restore = () => PInvoke.SetThreadDpiAwarenessContext(previous)
        };
    }

    public WindowInspectionReport Read(nint handle, WindowProcessIdentity identity, PhysicalDpiLease dpi, string requestedHandle)
    {
        HWND window = new(handle);
        FieldResult<DpiContextInfo> context = dpi.Established
            ? FieldResult<DpiContextInfo>.Ok(new DpiContextInfo(DesktopCoordinates.PerMonitorV2, dpi.Actual, true))
            : FieldResult<DpiContextInfo>.Fail(
                dpi.ErrorCode ?? "dpi_context_unavailable",
                dpi.Error ?? "The physical DPI context could not be established.",
                dpi.NativeError);
        WindowGeometryReport? geometry = dpi.Established ? ReadGeometry(window) : null;
        return new WindowInspectionReport(
            requestedHandle,
            identity.ProcessId,
            identity.ThreadId,
            identity.StartTimeReadable && identity.StartTime is DateTimeOffset start
                ? FieldResult<TimestampFact>.Ok(new TimestampFact(start))
                : FieldResult<TimestampFact>.Fail(
                    "process_start_unreadable",
                    "The process start time could not be read.",
                    identity.StartTimeError),
            context,
            geometry,
            dpi.Established ? ReadFrame(window) : FieldResult<FrameMetrics>.Fail("dpi_context_unavailable", "Frame metrics need the physical DPI context."),
            ReadStyles(window),
            ReadVisibility(window),
            ReadRelations(window),
            dpi.Established ? ReadMonitor(window, geometry) : FieldResult<WindowMonitorInfo>.Fail("dpi_context_unavailable", "Display intersections need the physical DPI context."),
            ReadProcess(identity.ProcessId));
    }

    public WindowGeometrySample Sample(nint handle)
    {
        using PhysicalDpiLease lease = EnterPhysicalCoordinates();
        HWND window = new(handle);
        if (!lease.Established)
        {
            return new WindowGeometrySample(null, null, null, null, [lease.ErrorCode ?? "dpi_context_unavailable"]);
        }

        WindowGeometryReport geometry = ReadGeometry(window);
        uint dpi = PInvoke.GetDpiForWindow(window);
        List<string> errors = [];
        if (!geometry.RawBounds.Success && geometry.RawBounds.ErrorCode is not null)
        {
            errors.Add(geometry.RawBounds.ErrorCode);
        }

        if (!geometry.ClientBounds.Success && geometry.ClientBounds.ErrorCode is not null)
        {
            errors.Add(geometry.ClientBounds.ErrorCode);
        }

        return new WindowGeometrySample(
            geometry.CoordinateSpace,
            geometry.RawBounds.Value,
            geometry.ClientBounds.Value,
            dpi == 0 ? null : (int)dpi,
            errors);
    }

    private static WindowGeometryReport ReadGeometry(HWND window)
    {
        FieldResult<DesktopRect> raw = ReadRect(window, static (HWND hwnd, out RECT rect) => PInvoke.GetWindowRect(hwnd, out rect), "GetWindowRect");
        FieldResult<DesktopRect> dwm = ReadDwm(window);
        FieldResult<DesktopRect> client = ReadClient(window);
        FieldResult<WindowInsets> insets = raw.Success && client.Success
            ? FieldResult<WindowInsets>.Ok(WindowGeometryMath.Insets(raw.Value!, client.Value!))
            : FieldResult<WindowInsets>.Fail("geometry_incomplete", "Insets need both the raw window rectangle and the client rectangle.");
        return new WindowGeometryReport(DesktopCoordinates.PhysicalScreenPixels, raw, dwm, client, insets);
    }

    private static FieldResult<DesktopRect> ReadRect(HWND window, RectReader read, string api)
    {
        if (!read(window, out RECT rect))
        {
            return Win32<DesktopRect>(api);
        }

        return FieldResult<DesktopRect>.Ok(new DesktopRect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top));
    }

    private static unsafe FieldResult<DesktopRect> ReadDwm(HWND window)
    {
        RECT rect = default;
        HRESULT result = PInvoke.DwmGetWindowAttribute(
            window,
            DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
            &rect,
            (uint)sizeof(RECT));
        if (result.Failed)
        {
            return FieldResult<DesktopRect>.Fail("dwm_unavailable", "DWM did not return the extended frame bounds.", result.Value);
        }

        return FieldResult<DesktopRect>.Ok(new DesktopRect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top));
    }

    private static unsafe FieldResult<DesktopRect> ReadClient(HWND window)
    {
        if (!PInvoke.GetClientRect(window, out RECT client))
        {
            return Win32<DesktopRect>("GetClientRect");
        }

        System.Drawing.Point origin = default;
        if (!PInvoke.ClientToScreen(window, &origin))
        {
            return Win32<DesktopRect>("ClientToScreen");
        }

        return FieldResult<DesktopRect>.Ok(new DesktopRect(origin.X, origin.Y, client.right - client.left, client.bottom - client.top));
    }

    private static FieldResult<FrameMetrics> ReadFrame(HWND window)
    {
        uint dpi = PInvoke.GetDpiForWindow(window);
        if (dpi == 0)
        {
            return Win32<FrameMetrics>("GetDpiForWindow");
        }

        int frameX = Metric(SYSTEM_METRICS_INDEX.SM_CXFRAME, dpi, out int errorX);
        int frameY = Metric(SYSTEM_METRICS_INDEX.SM_CYFRAME, dpi, out int errorY);
        int padded = Metric(SYSTEM_METRICS_INDEX.SM_CXPADDEDBORDER, dpi, out int errorPad);
        int caption = Metric(SYSTEM_METRICS_INDEX.SM_CYCAPTION, dpi, out int errorCaption);
        int error = new[] { errorX, errorY, errorPad, errorCaption }.FirstOrDefault(value => value != 0);
        if (error != 0)
        {
            return FieldResult<FrameMetrics>.Fail("win32_failed", "GetSystemMetricsForDpi failed.", error);
        }

        return FieldResult<FrameMetrics>.Ok(new FrameMetrics(frameX, frameY, padded, caption));
    }

    private static int Metric(SYSTEM_METRICS_INDEX index, uint dpi, out int error)
    {
        PInvoke.SetLastError(0);
        int value = PInvoke.GetSystemMetricsForDpi(index, dpi);
        error = value == 0 ? Marshal.GetLastWin32Error() : 0;
        return value;
    }

    private static FieldResult<WindowStyleInfo> ReadStyles(HWND window)
    {
        int extendedError = 0;
        if (!TryStyle(window, WINDOW_LONG_PTR_INDEX.GWL_STYLE, out nint style, out int styleError)
            || !TryStyle(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, out nint extended, out extendedError))
        {
            return FieldResult<WindowStyleInfo>.Fail("win32_failed", "GetWindowLongPtr failed.", styleError != 0 ? styleError : extendedError);
        }

        return FieldResult<WindowStyleInfo>.Ok(new WindowStyleInfo(
            "0x" + ((nuint)style).ToString("X"),
            "0x" + ((nuint)extended).ToString("X"),
            Names(style, StyleNames),
            Names(extended, ExtendedStyleNames)));
    }

    private static bool TryStyle(HWND window, WINDOW_LONG_PTR_INDEX index, out nint value, out int error)
    {
        PInvoke.SetLastError(0);
        value = PInvoke.GetWindowLong(window, index);
        error = Marshal.GetLastWin32Error();
        return value != 0 || error == 0;
    }

    private static WindowVisibility ReadVisibility(HWND window) => new(
        FieldResult<FlagFact>.Ok(new FlagFact(PInvoke.IsWindowVisible(window))),
        FieldResult<FlagFact>.Ok(new FlagFact(PInvoke.IsIconic(window))),
        FieldResult<FlagFact>.Ok(new FlagFact(PInvoke.IsZoomed(window))),
        ReadCloak(window));

    private static unsafe FieldResult<FlagFact> ReadCloak(HWND window)
    {
        uint cloaked = 0;
        HRESULT result = PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint));
        return result.Failed
            ? FieldResult<FlagFact>.Fail("dwm_unavailable", "DWM did not return the cloaked state.", result.Value)
            : FieldResult<FlagFact>.Ok(new FlagFact(cloaked != 0));
    }

    private static FieldResult<WindowRelations> ReadRelations(HWND window)
    {
        HWND desktop = PInvoke.GetDesktopWindow();
        HWND parent = PInvoke.GetAncestor(window, GET_ANCESTOR_FLAGS.GA_PARENT);
        HWND owner = PInvoke.GetWindow(window, GET_WINDOW_CMD.GW_OWNER);
        HWND root = PInvoke.GetAncestor(window, GET_ANCESTOR_FLAGS.GA_ROOTOWNER);
        return FieldResult<WindowRelations>.Ok(new WindowRelations(
            parent.IsNull || parent == desktop || parent == window ? null : WindowInspection.FormatHandle(parent),
            WindowInspection.FormatHandleOrNull(owner),
            root.IsNull || root == window ? null : WindowInspection.FormatHandle(root)));
    }

    private static FieldResult<WindowMonitorInfo> ReadMonitor(HWND window, WindowGeometryReport? geometry)
    {
        IReadOnlyList<DisplayInfo> displays = DisplayTopology.Enumerate();
        string? nearest = null;
        HMONITOR monitor = PInvoke.MonitorFromWindow(window, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (!monitor.IsNull && DisplayTopology.TryGetMonitorInfo(monitor, out MONITORINFOEXW info))
        {
            nearest = info.szDevice.AsReadOnlySpan().ToString().TrimEnd('\0');
            if (nearest.Length == 0)
            {
                nearest = null;
            }
        }

        DesktopRect? raw = geometry?.RawBounds.Success == true ? geometry.RawBounds.Value : null;
        DesktopRect? client = geometry?.ClientBounds.Success == true ? geometry.ClientBounds.Value : null;
        return FieldResult<WindowMonitorInfo>.Ok(
            new WindowMonitorInfo(nearest, WindowGeometryMath.Intersections(raw, client, nearest, displays)));
    }

    private static ProcessInspection ReadProcess(int pid)
    {
        string name = WindowInspection.ProcessName(pid);
        FieldResult<TextFact> processName = name.Length == 0
            ? FieldResult<TextFact>.Fail("process_unreadable", "The process name could not be read.")
            : FieldResult<TextFact>.Ok(new TextFact(UntrustedText.Trim(name, 260)));
        return new ProcessInspection(
            ReadSession(pid),
            processName,
            ReadPath(pid),
            ReadToken(pid, out FieldResult<TextFact> sid, out FieldResult<FlagFact> elevated, out FieldResult<TextFact> elevation, out FieldResult<FlagFact> localSystem),
            sid,
            elevated,
            elevation,
            localSystem);
    }

    private static FieldResult<CountFact> ReadSession(int pid)
    {
        uint session;
        unsafe
        {
            if (!PInvoke.ProcessIdToSessionId((uint)pid, &session))
            {
                return Win32<CountFact>("ProcessIdToSessionId");
            }
        }

        return FieldResult<CountFact>.Ok(new CountFact((int)session));
    }

    private static FieldResult<TextFact> ReadPath(int pid)
    {
        string? path = WindowInspection.ProcessPath(pid);
        return path is null
            ? FieldResult<TextFact>.Fail("process_unreadable", "The process image path could not be read.", Marshal.GetLastWin32Error())
            : FieldResult<TextFact>.Ok(new TextFact(UntrustedText.Trim(path, 1024)));
    }

    private static FieldResult<TextFact> ReadToken(
        int pid,
        out FieldResult<TextFact> sid,
        out FieldResult<FlagFact> elevated,
        out FieldResult<TextFact> elevation,
        out FieldResult<FlagFact> localSystem)
    {
        sid = FieldResult<TextFact>.Fail("process_unreadable", "The process token was not opened.");
        elevated = FieldResult<FlagFact>.Fail("process_unreadable", "The process token was not opened.");
        elevation = FieldResult<TextFact>.Fail("process_unreadable", "The process token was not opened.");
        localSystem = FieldResult<FlagFact>.Fail("process_unreadable", "The process token was not opened.");
        HANDLE processHandle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (processHandle.IsNull)
        {
            FieldResult<TextFact> failed = Win32<TextFact>("OpenProcess");
            sid = failed;
            elevated = Win32<FlagFact>("OpenProcess");
            elevation = failed;
            localSystem = Win32<FlagFact>("OpenProcess");
            return failed;
        }

        using SafeFileHandle process = new(processHandle, ownsHandle: true);
        if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, out SafeFileHandle token))
        {
            token.Dispose();
            FieldResult<TextFact> failed = Win32<TextFact>("OpenProcessToken");
            sid = failed;
            elevated = Win32<FlagFact>("OpenProcessToken");
            elevation = failed;
            localSystem = Win32<FlagFact>("OpenProcessToken");
            return failed;
        }

        using SafeFileHandle ownedToken = token;
        elevated = ReadElevated(ownedToken);
        elevation = ReadElevationType(ownedToken);
        (FieldResult<TextFact> level, sid) = ReadIntegrity(ownedToken);
        localSystem = ReadLocalSystem(ownedToken);
        return level;
    }

    private static unsafe FieldResult<FlagFact> ReadElevated(SafeFileHandle token)
    {
        TOKEN_ELEVATION value = default;
        if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevation, &value, (uint)sizeof(TOKEN_ELEVATION), out _))
        {
            return Win32<FlagFact>("TokenElevation");
        }

        return FieldResult<FlagFact>.Ok(new FlagFact(value.TokenIsElevated != 0));
    }

    private static unsafe FieldResult<TextFact> ReadElevationType(SafeFileHandle token)
    {
        int type = 0;
        if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevationType, &type, sizeof(int), out _))
        {
            return Win32<TextFact>("TokenElevationType");
        }

        string name = type switch
        {
            1 => "default",
            2 => "full",
            3 => "limited",
            _ => "unknown"
        };
        return FieldResult<TextFact>.Ok(new TextFact(name));
    }

    private static unsafe (FieldResult<TextFact> Level, FieldResult<TextFact> Sid) ReadIntegrity(SafeFileHandle token)
    {
        uint needed = 0;
        PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, null, 0, out needed);
        if (needed is 0 or > 512)
        {
            FieldResult<TextFact> failed = Win32<TextFact>("TokenIntegrityLevel");
            return (failed, failed);
        }

        byte* buffer = stackalloc byte[(int)needed];
        if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buffer, needed, out needed))
        {
            FieldResult<TextFact> failed = Win32<TextFact>("TokenIntegrityLevel");
            return (failed, failed);
        }

        TOKEN_MANDATORY_LABEL* label = (TOKEN_MANDATORY_LABEL*)buffer;
        PSID integrity = label->Label.Sid;
        byte* countPointer = PInvoke.GetSidSubAuthorityCount(integrity);
        if (countPointer == null || *countPointer == 0)
        {
            FieldResult<TextFact> failed = FieldResult<TextFact>.Fail("process_unreadable", "The integrity SID could not be read.");
            return (failed, failed);
        }

        uint* ridPointer = PInvoke.GetSidSubAuthority(integrity, (uint)(*countPointer - 1));
        uint rid = ridPointer == null ? 0 : *ridPointer;
        string level = rid switch
        {
            0x0000 => "untrusted",
            0x1000 => "low",
            0x2000 => "medium",
            0x2010 => "medium-plus",
            0x3000 => "high",
            0x4000 => "system",
            _ => "0x" + rid.ToString("X")
        };
        return (FieldResult<TextFact>.Ok(new TextFact(level)), ReadSid(integrity));
    }

    private static unsafe FieldResult<TextFact> ReadSid(PSID sid)
    {
        PWSTR text = default;
        if (!PInvoke.ConvertSidToStringSid(sid, &text) || text.Value == null)
        {
            return Win32<TextFact>("ConvertSidToStringSid");
        }

        string value = text.ToString();
        PInvoke.LocalFree((HLOCAL)(nint)text.Value);
        return FieldResult<TextFact>.Ok(new TextFact(UntrustedText.Trim(value, 184)));
    }

    private static unsafe FieldResult<FlagFact> ReadLocalSystem(SafeFileHandle token)
    {
        uint size = 68;
        byte* wellKnown = stackalloc byte[68];
        if (!PInvoke.CreateWellKnownSid(WELL_KNOWN_SID_TYPE.WinLocalSystemSid, default, (PSID)wellKnown, ref size))
        {
            return Win32<FlagFact>("CreateWellKnownSid");
        }

        uint needed = 0;
        PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, null, 0, out needed);
        if (needed is 0 or > 256)
        {
            return Win32<FlagFact>("TokenUser");
        }

        byte* buffer = stackalloc byte[(int)needed];
        if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, buffer, needed, out needed))
        {
            return Win32<FlagFact>("TokenUser");
        }

        TOKEN_USER* user = (TOKEN_USER*)buffer;
        BOOL equal = PInvoke.EqualSid(user->User.Sid, (PSID)wellKnown);
        return FieldResult<FlagFact>.Ok(new FlagFact(equal));
    }

    private static (bool Readable, DateTimeOffset? Start, int? Error) ReadStart(int pid)
    {
        HANDLE handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle.IsNull)
        {
            return (false, null, Marshal.GetLastWin32Error());
        }

        using SafeFileHandle process = new(handle, ownsHandle: true);
        if (!ProcessStartTime.TryRead(process, out DateTimeOffset start, out int error))
        {
            return (false, null, error);
        }

        return (true, start, null);
    }

    private static FieldResult<T> Win32<T>(string api) where T : class
    {
        int error = Marshal.GetLastWin32Error();
        string code = error == 5 ? "access_denied" : "win32_failed";
        return FieldResult<T>.Fail(code, api + " failed: " + new Win32Exception(error).Message, error);
    }

    private static IReadOnlyList<string> Names(nint value, IReadOnlyList<(long Mask, string Name)> names)
    {
        long bits = (long)value;
        return names.Where(item => (bits & item.Mask) == item.Mask).Select(item => item.Name).ToArray();
    }

    private delegate bool RectReader(HWND window, out RECT rect);

    private static readonly (long Mask, string Name)[] StyleNames =
    [
        (0x80000000, "WS_POPUP"),
        (0x40000000, "WS_CHILD"),
        (0x10000000, "WS_VISIBLE"),
        (0x00C00000, "WS_CAPTION"),
        (0x00040000, "WS_THICKFRAME"),
        (0x00020000, "WS_MINIMIZEBOX"),
        (0x00010000, "WS_MAXIMIZEBOX"),
        (0x00CF0000, "WS_OVERLAPPEDWINDOW")
    ];

    private static readonly (long Mask, string Name)[] ExtendedStyleNames =
    [
        (0x00000001, "WS_EX_DLGMODALFRAME"),
        (0x00000080, "WS_EX_TOOLWINDOW"),
        (0x00000100, "WS_EX_WINDOWEDGE"),
        (0x00000200, "WS_EX_CLIENTEDGE"),
        (0x00040000, "WS_EX_APPWINDOW"),
        (0x00080000, "WS_EX_LAYERED"),
        (0x00200000, "WS_EX_NOREDIRECTIONBITMAP"),
        (0x00000008, "WS_EX_TOPMOST")
    ];
}

/// <summary>Geometry sampled when a window event arrives. Null rectangles were not read.</summary>
public readonly record struct WindowGeometrySample(
    string? CoordinateSpace,
    DesktopRect? Raw,
    DesktopRect? Client,
    int? Dpi,
    IReadOnlyList<string> Errors);

/// <summary>Bounds text collected from windows or processes before it is returned.</summary>
internal static class UntrustedText
{
    public static string Trim(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string cleaned = value.Replace("\0", string.Empty, StringComparison.Ordinal);
        return cleaned.Length <= max ? cleaned : cleaned[..max];
    }
}
