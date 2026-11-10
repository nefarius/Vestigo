using System.Text.Json.Serialization;

namespace Vestigo.Desktop;

/// <summary>Physical virtual-screen pixels under per-monitor DPI awareness v2.</summary>
public static class DesktopCoordinates
{
    public const string PhysicalScreenPixels = "physical-screen-pixels";

    public const string PerMonitorV2 = "per-monitor-v2";
}

/// <summary>One Win32 read. A failed result has no value, so a failed rectangle is never a zero rectangle.</summary>
public sealed record FieldResult<T>(
    bool Success,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] T? Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NativeError) where T : class
{
    public static FieldResult<T> Ok(T value) => new(true, value, null, null, null);

    public static FieldResult<T> Fail(string code, string error, int? nativeError = null) =>
        new(false, null, code, error, nativeError);
}

public sealed record CountFact(int Value);

public sealed record FlagFact(bool Value);

public sealed record TextFact(string Value);

public sealed record TimestampFact(DateTimeOffset Value);

/// <summary>DPI context requested for geometry, and the context actually entered.</summary>
public sealed record DpiContextInfo(string Requested, string? Actual, bool Established);

public sealed record FrameMetrics(int FrameWidth, int FrameHeight, int PaddedBorder, int CaptionHeight);

/// <summary>Non-client insets from the raw window rectangle to the client rectangle. Negative values are real.</summary>
public sealed record WindowInsets(int Left, int Top, int Right, int Bottom);

public sealed record WindowGeometryReport(
    string CoordinateSpace,
    FieldResult<DesktopRect> RawBounds,
    FieldResult<DesktopRect> DwmVisibleBounds,
    FieldResult<DesktopRect> ClientBounds,
    FieldResult<WindowInsets> Insets);

public sealed record WindowStyleInfo(
    string Style,
    string ExtendedStyle,
    IReadOnlyList<string> StyleNames,
    IReadOnlyList<string> ExtendedStyleNames);

public sealed record WindowVisibility(
    FieldResult<FlagFact> Visible,
    FieldResult<FlagFact> Minimized,
    FieldResult<FlagFact> Maximized,
    FieldResult<FlagFact> Cloaked);

public sealed record WindowRelations(string? ParentHandle, string? OwnerHandle, string? RootOwnerHandle);

public sealed record DisplayIntersection(
    string GdiDeviceName,
    bool Nearest,
    DesktopRect DisplayBounds,
    DesktopRect? RawOverlap,
    DesktopRect? ClientOverlap);

public sealed record WindowMonitorInfo(string? NearestDevice, IReadOnlyList<DisplayIntersection> Intersections);

public sealed record ProcessInspection(
    FieldResult<CountFact> SessionId,
    FieldResult<TextFact> Name,
    FieldResult<TextFact> ImagePath,
    FieldResult<TextFact> IntegrityLevel,
    FieldResult<TextFact> IntegritySid,
    FieldResult<FlagFact> Elevated,
    FieldResult<TextFact> ElevationType,
    FieldResult<FlagFact> LocalSystem);

/// <summary>
///     Detailed read of one window. <see cref="Geometry" /> is null when the physical DPI context could not be
///     entered, because those rectangles would not have a known coordinate space.
/// </summary>
public sealed record WindowInspectionReport(
    string Handle,
    int ProcessId,
    int ThreadId,
    FieldResult<TimestampFact> ProcessStartTime,
    FieldResult<DpiContextInfo> DpiContext,
    WindowGeometryReport? Geometry,
    FieldResult<FrameMetrics> Frame,
    FieldResult<WindowStyleInfo> Styles,
    WindowVisibility Visibility,
    FieldResult<WindowRelations> Relations,
    FieldResult<WindowMonitorInfo> Monitor,
    ProcessInspection Process);

public sealed record WindowProcessIdentity(
    int ProcessId,
    int ThreadId,
    bool StartTimeReadable,
    DateTimeOffset? StartTime,
    int? StartTimeError);
