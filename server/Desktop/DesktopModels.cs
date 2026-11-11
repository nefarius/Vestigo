using System.Text.RegularExpressions;

using Vestigo.Processes;

namespace Vestigo.Desktop;

/// <summary>A rectangle in physical pixels (per-monitor DPI-aware).</summary>
public sealed record DesktopRect(int X, int Y, int Width, int Height)
{
    public bool Intersects(DesktopRect other) =>
        X < other.X + other.Width
        && other.X < X + Width
        && Y < other.Y + other.Height
        && other.Y < Y + Height;

    /// <summary>The overlapping rectangle, or null when the overlap is empty.</summary>
    public DesktopRect? Intersection(DesktopRect other)
    {
        int left = Math.Max(X, other.X);
        int top = Math.Max(Y, other.Y);
        int right = Math.Min(X + Width, other.X + other.Width);
        int bottom = Math.Min(Y + Height, other.Y + other.Height);
        int width = right - left;
        int height = bottom - top;
        return width > 0 && height > 0 ? new DesktopRect(left, top, width, height) : null;
    }
}

/// <summary>Width and height in physical pixels.</summary>
public sealed record DesktopSize(int Width, int Height);

/// <summary>Active signal and color state for one target. Present only when <c>includeModes</c> is true. Unknown numbers are null.</summary>
public sealed record DisplaySignal(
    string? AdapterLuid,
    double? RefreshHz,
    int? Width,
    int? Height,
    string? Rotation,
    string? Scaling,
    bool? AdvancedColorSupported,
    bool? AdvancedColorEnabled,
    int? BitsPerColor);

/// <summary>One physical display target attached to a GDI device, including cloned outputs.</summary>
public sealed record PhysicalMonitor(
    string? FriendlyName,
    string? DevicePath,
    string? ManufacturerId,
    string? ProductCode,
    string? OutputTechnology,
    uint ConnectorInstance,
    DisplaySignal? Signal = null);

/// <summary>A GDI display device and the physical monitors currently driven by it.</summary>
public sealed record DisplayInfo(
    string GdiDeviceName,
    bool Primary,
    DesktopRect Bounds,
    DesktopRect WorkArea,
    int Dpi,
    string? AdapterName,
    IReadOnlyList<PhysicalMonitor> Monitors);

/// <summary>One Win32 window and the display it is rendered on.</summary>
/// <param name="Title">Top-level window title. Child windows use an empty title so control text is not exposed or matched.</param>
public sealed record WindowInfo(
    string Handle,
    string Title,
    string ClassName,
    int ProcessId,
    int ThreadId,
    string ProcessName,
    string? ProcessPath,
    string? ParentHandle,
    string? OwnerHandle,
    string? RootOwnerHandle,
    bool Visible,
    bool Minimized,
    bool Maximized,
    bool Cloaked,
    DesktopRect Bounds,
    DesktopRect? VisibleBounds,
    DesktopSize ClientSize,
    int Dpi,
    string? Display,
    IReadOnlyList<string> IntersectingDisplays);

/// <summary>Windows that matched a listing, plus the display topology seen at the same time.</summary>
public sealed record WindowListResult(
    IReadOnlyList<WindowInfo> Windows,
    IReadOnlyList<DisplayInfo> Displays,
    bool Truncated,
    int SessionId,
    string? DesktopName = null,
    bool? InputDesktop = null);

/// <summary>The GDI and physical display topology of the calling session.</summary>
public sealed record DisplayListResult(
    IReadOnlyList<DisplayInfo> Displays,
    int SessionId,
    string? DesktopName = null,
    bool? InputDesktop = null);

/// <summary>Filters applied together (AND) to decide which windows a listing returns.</summary>
public sealed record WindowSelector(
    IReadOnlyList<int> Pids,
    IReadOnlyList<string> ProcessNames,
    string? TitlePattern,
    string? ClassName,
    bool IncludeHidden,
    bool IncludeChildren,
    int Limit)
{
    public const int DefaultLimit = 500;

    public const int MaxLimit = 5000;

    public static WindowSelector All { get; } = new([], [], null, null, false, false, DefaultLimit);

    /// <summary>Pids and names are alternatives of each other.</summary>
    public bool MatchesProcess(int pid, string processName) =>
        Pids.Count == 0 && ProcessNames.Count == 0
        || Pids.Contains(pid)
        || ProcessNames.Any(name => CloseSelector.NamesEqual(name, processName));

    /// <summary>Title and class narrow a process that already matched <see cref="MatchesProcess" />.</summary>
    public bool Matches(string title, string className, Regex? titleRegex)
    {
        bool byTitle = titleRegex is null || titleRegex.IsMatch(title);
        bool byClass = string.IsNullOrEmpty(ClassName)
                       || string.Equals(ClassName, className, StringComparison.OrdinalIgnoreCase);
        return byTitle && byClass;
    }

    /// <summary>Tests one window. Pids and names are alternatives of each other; title and class narrow them.</summary>
    public bool Matches(int pid, string processName, string title, string className, Regex? titleRegex) =>
        MatchesProcess(pid, processName) && Matches(title, className, titleRegex);

    public static Regex? CompileTitle(string? pattern) => CloseSelector.CompileTitle(pattern);
}

/// <summary>Screenshot target accepted by <c>take_screenshot</c> and the session helper.</summary>
public static class ScreenCaptureTarget
{
    public const string Desktop = "desktop";

    public const string Display = "display";

    public const string Window = "window";

    public const string Region = "region";

    public const int DefaultMaxEdge = 1920;

    public const int MaxMaxEdge = 8192;

    public static bool TryParse(string? value, out string target, out string? error)
    {
        target = Desktop;
        error = null;
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, Desktop, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, Display, StringComparison.OrdinalIgnoreCase))
        {
            target = Display;
            return true;
        }

        if (string.Equals(value, Window, StringComparison.OrdinalIgnoreCase))
        {
            target = Window;
            return true;
        }

        if (string.Equals(value, Region, StringComparison.OrdinalIgnoreCase))
        {
            target = Region;
            return true;
        }

        error = "target must be 'desktop', 'display', 'window' or 'region'.";
        return false;
    }
}

/// <summary>Parameters for one screen capture, passed to the session helper as arguments.</summary>
public sealed record ScreenCaptureRequest(
    string Target,
    string? Display,
    string? WindowHandle,
    IReadOnlyList<int> Pids,
    IReadOnlyList<string> ProcessNames,
    string? TitlePattern,
    DesktopRect? Region,
    bool IncludeCursor,
    int MaxEdge,
    bool IncludeFullImage,
    long MaxBytes)
{
    public static ScreenCaptureRequest DesktopPreview { get; } = new(
        ScreenCaptureTarget.Desktop,
        null,
        null,
        [],
        [],
        null,
        null,
        false,
        ScreenCaptureTarget.DefaultMaxEdge,
        false,
        DesktopOptions.DefaultMaxScreenshotBytes);
}

/// <summary>PNG capture plus metadata. Image payloads are base64 for the session-helper JSON contract.</summary>
public sealed record ScreenCaptureResult(
    string Target,
    DesktopRect Bounds,
    DesktopSize Size,
    DesktopSize? PreviewSize,
    int Dpi,
    IReadOnlyList<string> Displays,
    string? WindowHandle,
    string? WindowTitle,
    string? PreviewPngBase64,
    string? FullPngBase64,
    int SessionId,
    string CaptureFidelity = "input-desktop");

/// <summary>Screenshot metadata returned to the MCP client. Image bytes travel as MCP image content, not here.</summary>
public sealed record ScreenCaptureInfo(
    string Target,
    DesktopRect Bounds,
    DesktopSize Size,
    DesktopSize? PreviewSize,
    int Dpi,
    IReadOnlyList<string> Displays,
    string? WindowHandle,
    string? WindowTitle,
    string? SavedPath,
    long? SavedSize,
    string? SavedSha256,
    bool? ReplacedExisting,
    int SessionId,
    string CaptureFidelity = "input-desktop");

/// <summary>Decoded helper capture ready to become a <c>CallToolResult</c>.</summary>
public sealed record CapturedScreenshot(ScreenCaptureInfo Info, byte[]? PreviewPng);
