namespace Vestigo.Diagnostics;

/// <summary>What the capture API reported before any frame was acquired.</summary>
public sealed record CaptureAdvertised(bool ApiPresent, bool OutputFound);

/// <summary>Frame arrival only. This record has no pixel, bitmap, or image fields.</summary>
public sealed record CaptureMeasured(int Frames, int? FirstFrameLatencyMs, string? Hresult, string? ErrorName);

/// <summary>One capture-support observation from the interactive session.</summary>
public sealed record CaptureProbeResult(
    int SessionId,
    string? DesktopName,
    string Backend,
    string Target,
    CaptureAdvertised Advertised,
    CaptureMeasured? Measured);
