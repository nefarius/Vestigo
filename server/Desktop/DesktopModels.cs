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
}

/// <summary>Width and height in physical pixels.</summary>
public sealed record DesktopSize(int Width, int Height);

/// <summary>One physical display target attached to a GDI device, including cloned outputs.</summary>
public sealed record PhysicalMonitor(
    string? FriendlyName,
    string? DevicePath,
    string? ManufacturerId,
    string? ProductCode,
    string? OutputTechnology,
    uint ConnectorInstance);

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
    int SessionId);

/// <summary>The GDI and physical display topology of the calling session.</summary>
public sealed record DisplayListResult(IReadOnlyList<DisplayInfo> Displays, int SessionId);

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
