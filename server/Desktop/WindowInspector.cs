namespace Vestigo.Desktop;

/// <summary>Compares a live window identity with the identity the caller asked for.</summary>
public static class WindowIdentityGuard
{
    public static OperationResult<bool> Check(WindowProcessIdentity actual, int? expectedPid, DateTimeOffset? expectedStart)
    {
        if (expectedPid is int pid && pid != actual.ProcessId)
        {
            return OperationResult<bool>.Fail(
                "window_identity_mismatch",
                "The window is not owned by expectedPid. The handle may have been reused.");
        }

        if (expectedStart is DateTimeOffset expected)
        {
            if (!actual.StartTimeReadable || actual.StartTime is null)
            {
                return OperationResult<bool>.Fail(
                    "process_identity_unreadable",
                    "The process start time could not be read, so expectedStartTime was not checked.");
            }

            if (actual.StartTime.Value.UtcDateTime != expected.UtcDateTime)
            {
                return OperationResult<bool>.Fail(
                    "process_identity_mismatch",
                    "The process start time does not match expectedStartTime. The pid may have been reused.");
            }
        }

        return OperationResult<bool>.Ok(true);
    }

    public static bool Same(WindowProcessIdentity before, WindowProcessIdentity after) =>
        before.ProcessId == after.ProcessId
        && before.StartTimeReadable == after.StartTimeReadable
        && before.StartTime?.UtcDateTime == after.StartTime?.UtcDateTime;
}

/// <summary>Derives client insets and display overlaps from rectangles that were actually read.</summary>
public static class WindowGeometryMath
{
    public static WindowInsets Insets(DesktopRect raw, DesktopRect client) => new(
        client.X - raw.X,
        client.Y - raw.Y,
        raw.X + raw.Width - (client.X + client.Width),
        raw.Y + raw.Height - (client.Y + client.Height));

    public static DesktopRect Expand(DesktopRect client, WindowInsets insets) => new(
        client.X - insets.Left,
        client.Y - insets.Top,
        client.Width + insets.Left + insets.Right,
        client.Height + insets.Top + insets.Bottom);

    public static IReadOnlyList<DisplayIntersection> Intersections(
        DesktopRect? raw,
        DesktopRect? client,
        string? nearest,
        IReadOnlyList<DisplayInfo> displays)
    {
        List<DisplayIntersection> intersections = [];
        foreach (DisplayInfo display in displays)
        {
            DesktopRect? rawOverlap = raw?.Intersection(display.Bounds);
            DesktopRect? clientOverlap = client?.Intersection(display.Bounds);
            bool isNearest = nearest is not null && string.Equals(nearest, display.GdiDeviceName, StringComparison.OrdinalIgnoreCase);
            if (rawOverlap is null && clientOverlap is null && !isNearest)
            {
                continue;
            }

            intersections.Add(new DisplayIntersection(display.GdiDeviceName, isNearest, display.Bounds, rawOverlap, clientOverlap));
        }

        return intersections;
    }
}

/// <summary>A DPI-context change that restores the calling thread when disposed.</summary>
public sealed class PhysicalDpiLease : IDisposable
{
    public bool Established { get; init; }

    public string? Actual { get; init; }

    public string? ErrorCode { get; init; }

    public string? Error { get; init; }

    public int? NativeError { get; init; }

    public Action? Restore { get; init; }

    public bool Restored { get; private set; }

    public void Dispose()
    {
        if (Restored)
        {
            return;
        }

        Restored = true;
        Restore?.Invoke();
    }
}

/// <summary>Win32 reads used by <see cref="WindowInspector" />. Tests supply a fake.</summary>
public interface IWindowInspectionSource
{
    bool TryParseHandle(string? text, out nint handle);

    bool IsWindow(nint handle);

    OperationResult<WindowProcessIdentity> ReadIdentity(nint handle);

    PhysicalDpiLease EnterPhysicalCoordinates();

    WindowInspectionReport Read(nint handle, WindowProcessIdentity identity, PhysicalDpiLease dpi, string requestedHandle);
}

/// <summary>Reads one window and refuses a report whose identity changed during the read.</summary>
public sealed class WindowInspector(IWindowInspectionSource source)
{
    public OperationResult<WindowInspectionReport> Inspect(string? handle, int? expectedPid, DateTimeOffset? expectedStart)
    {
        if (expectedPid is <= 0)
        {
            return OperationResult<WindowInspectionReport>.Fail("invalid_request", "expectedPid must be positive.");
        }

        if (!source.TryParseHandle(handle, out nint hwnd))
        {
            return OperationResult<WindowInspectionReport>.Fail(
                "invalid_request",
                "windowHandle must be a non-zero hex handle such as 0x1A2B.");
        }

        if (!source.IsWindow(hwnd))
        {
            return OperationResult<WindowInspectionReport>.Fail("window_not_found", "The window handle is not a window.");
        }

        OperationResult<WindowProcessIdentity> before = source.ReadIdentity(hwnd);
        if (!before.Success)
        {
            return before.FailAs<WindowInspectionReport>();
        }

        OperationResult<bool> identity = WindowIdentityGuard.Check(before.Value!, expectedPid, expectedStart);
        if (!identity.Success)
        {
            return identity.FailAs<WindowInspectionReport>();
        }

        using PhysicalDpiLease dpi = source.EnterPhysicalCoordinates();
        WindowInspectionReport report = source.Read(hwnd, before.Value!, dpi, handle!.Trim());
        if (!dpi.Established)
        {
            report = report with
            {
                Geometry = null,
                Frame = FieldResult<FrameMetrics>.Fail(
                    dpi.ErrorCode ?? "dpi_context_unavailable",
                    dpi.Error ?? "The physical DPI context could not be established.",
                    dpi.NativeError),
                Monitor = FieldResult<WindowMonitorInfo>.Fail(
                    dpi.ErrorCode ?? "dpi_context_unavailable",
                    "Display intersections were omitted because the physical DPI context could not be established.",
                    dpi.NativeError)
            };
        }

        if (!source.IsWindow(hwnd))
        {
            return OperationResult<WindowInspectionReport>.Fail(
                "window_identity_mismatch",
                "The window closed while it was being read.");
        }

        OperationResult<WindowProcessIdentity> after = source.ReadIdentity(hwnd);
        if (!after.Success || !WindowIdentityGuard.Same(before.Value!, after.Value!))
        {
            return OperationResult<WindowInspectionReport>.Fail(
                "window_identity_mismatch",
                "The window or process identity changed while it was being read.");
        }

        return OperationResult<WindowInspectionReport>.Ok(report);
    }
}
