using System.Runtime.InteropServices;

using Vestigo.Processes;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Vestigo.Desktop;

/// <summary>
///     Places a window with <c>SetWindowPos</c> without activating it or changing Z-order.
///     <c>SetWindowPlacement</c> is used only for an explicit restore and can still cause Windows to activate the window.
/// </summary>
public sealed class WindowsWindowPlacementSource : IWindowPlacementSource
{
    private const SET_WINDOW_POS_FLAGS MoveFlags =
        SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
        | SET_WINDOW_POS_FLAGS.SWP_NOZORDER
        | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;

    private readonly WindowsWindowInspectionSource _windows = new();

    public OperationResult<PlacementSample> Measure(string handle, int? expectedPid, DateTimeOffset? expectedStartTime)
    {
        if (!TryIdentity(handle, expectedPid, expectedStartTime, out nint hwnd, out _, out OperationResult<PlacementSample>? failed))
        {
            return failed!;
        }

        using PhysicalDpiLease lease = _windows.EnterPhysicalCoordinates();
        if (!lease.Established)
        {
            return OperationResult<PlacementSample>.Ok(new PlacementSample(false, null, null, lease.ErrorCode, lease.Error));
        }

        WindowGeometrySample sample = _windows.Sample(hwnd);
        return OperationResult<PlacementSample>.Ok(new PlacementSample(
            true,
            sample.Raw,
            sample.Client,
            sample.Raw is null || sample.Client is null ? "geometry_incomplete" : null,
            sample.Raw is null || sample.Client is null ? "The raw or client rectangle could not be read." : null));
    }

    public OperationResult<PlacementMove> MoveOuter(string handle, int? expectedPid, DateTimeOffset? expectedStartTime, DesktopRect outer)
    {
        if (!TryIdentity(handle, expectedPid, expectedStartTime, out nint hwnd, out _, out OperationResult<PlacementMove>? failed))
        {
            return failed!;
        }

        using PhysicalDpiLease lease = _windows.EnterPhysicalCoordinates();
        if (!lease.Established)
        {
            return OperationResult<PlacementMove>.Fail(lease.ErrorCode ?? "dpi_context_unavailable", lease.Error ?? "The physical DPI context could not be established.");
        }

        bool moved = PInvoke.SetWindowPos(new HWND(hwnd), default, outer.X, outer.Y, outer.Width, outer.Height, MoveFlags);
        int error = Marshal.GetLastWin32Error();
        return OperationResult<PlacementMove>.Ok(new PlacementMove(moved, moved ? null : error, moved ? null : "SetWindowPos failed."));
    }

    public OperationResult<SavedWindowPlacement> ReadPlacement(string handle, int? expectedPid, DateTimeOffset? expectedStartTime)
    {
        if (!TryIdentity(handle, expectedPid, expectedStartTime, out nint hwnd, out WindowProcessIdentity? identity, out OperationResult<SavedWindowPlacement>? failed))
        {
            return failed!;
        }

        unsafe
        {
            WINDOWPLACEMENT placement = default;
            placement.length = (uint)sizeof(WINDOWPLACEMENT);
            if (!PInvoke.GetWindowPlacement(new HWND(hwnd), &placement))
            {
                return OperationResult<SavedWindowPlacement>.Fail(
                "win32_failed",
                "GetWindowPlacement failed (" + Marshal.GetLastWin32Error() + ").");
            }

            return OperationResult<SavedWindowPlacement>.Ok(ToSaved(handle, identity!, placement));
        }
    }

    public OperationResult<PlacementMove> ApplyPlacement(SavedWindowPlacement placement)
    {
        if (!TryIdentity(placement.Handle, placement.ProcessId, placement.StartTimeReadable ? placement.ProcessStartTime : null, out nint hwnd, out _, out OperationResult<PlacementMove>? failed))
        {
            return failed!;
        }

        unsafe
        {
            WINDOWPLACEMENT native = default;
            native.length = (uint)sizeof(WINDOWPLACEMENT);
            native.flags = (WINDOWPLACEMENT_FLAGS)placement.Flags;
            native.showCmd = (SHOW_WINDOW_CMD)placement.ShowCommand;
            native.ptMinPosition.X = placement.MinX;
            native.ptMinPosition.Y = placement.MinY;
            native.ptMaxPosition.X = placement.MaxX;
            native.ptMaxPosition.Y = placement.MaxY;
            native.rcNormalPosition.left = placement.NormalX;
            native.rcNormalPosition.top = placement.NormalY;
            native.rcNormalPosition.right = placement.NormalX + placement.NormalWidth;
            native.rcNormalPosition.bottom = placement.NormalY + placement.NormalHeight;
            bool applied = PInvoke.SetWindowPlacement(new HWND(hwnd), &native);
            int error = Marshal.GetLastWin32Error();
            return OperationResult<PlacementMove>.Ok(new PlacementMove(applied, applied ? null : error, applied ? null : "SetWindowPlacement failed."));
        }
    }

    public string? Foreground()
    {
        HWND window = PInvoke.GetForegroundWindow();
        return window.IsNull ? null : WindowInspection.FormatHandle(window);
    }

    private bool TryIdentity<T>(
        string handle,
        int? expectedPid,
        DateTimeOffset? expectedStart,
        out nint hwnd,
        out WindowProcessIdentity? identity,
        out OperationResult<T>? failed)
    {
        identity = null;
        failed = null;
        if (!_windows.TryParseHandle(handle, out hwnd) || !_windows.IsWindow(hwnd))
        {
            failed = OperationResult<T>.Fail("window_not_found", "The window handle is not a window.");
            return false;
        }

        OperationResult<WindowProcessIdentity> read = _windows.ReadIdentity(hwnd);
        if (!read.Success)
        {
            failed = read.FailAs<T>();
            return false;
        }

        OperationResult<bool> check = WindowIdentityGuard.Check(read.Value!, expectedPid, expectedStart);
        if (!check.Success)
        {
            failed = check.FailAs<T>();
            return false;
        }

        identity = read.Value;
        return true;
    }

    private static unsafe SavedWindowPlacement ToSaved(string handle, WindowProcessIdentity identity, WINDOWPLACEMENT placement) => new(
        handle,
        identity.ProcessId,
        identity.StartTime,
        identity.StartTimeReadable,
        (int)placement.showCmd,
        placement.ptMinPosition.X,
        placement.ptMinPosition.Y,
        placement.ptMaxPosition.X,
        placement.ptMaxPosition.Y,
        placement.rcNormalPosition.left,
        placement.rcNormalPosition.top,
        placement.rcNormalPosition.right - placement.rcNormalPosition.left,
        placement.rcNormalPosition.bottom - placement.rcNormalPosition.top,
        (uint)placement.flags);
}
