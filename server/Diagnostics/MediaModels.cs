namespace Vestigo.Diagnostics;

/// <summary>Media Foundation video encoders from one enumeration. This is not an activation or a vendor-SDK inventory.</summary>
public sealed record VideoEncoderList(
    string Backend,
    string DiscoverySource,
    string Stage,
    bool Activated,
    bool ProcessedSamples,
    bool Truncated,
    IReadOnlyList<VideoEncoder> Encoders);

/// <summary>One transform returned by <c>MFTEnumEx</c>. Attributes are read without calling <c>ActivateObject</c>.</summary>
public sealed record VideoEncoder(
    string? Name,
    string? ClassId,
    bool Hardware,
    bool Async,
    IReadOnlyList<string> InputTypes,
    IReadOnlyList<string> OutputTypes);

/// <summary>Video-capture sources and, when one device was selected, the media types it advertised.</summary>
public sealed record CameraFormatList(
    string Stage,
    bool Activated,
    bool ProcessedSamples,
    bool Truncated,
    IReadOnlyList<CameraDevice> Devices);

/// <summary>One video-capture source. <see cref="Formats"/> is empty until that source is selected and activated.</summary>
public sealed record CameraDevice(
    string? Name,
    string? SymbolicLink,
    IReadOnlyList<CameraFormat> Formats);

/// <summary>One advertised media type. No sample was read.</summary>
public sealed record CameraFormat(
    string? Subtype,
    int? Width,
    int? Height,
    double? FrameRate,
    string? Interlace);

/// <summary>A WASAPI format check, or a shared-mode synthetic tone when playback was requested.</summary>
public sealed record AudioFormatProbeResult(
    string Endpoint,
    string? ResolvedEndpoint,
    string Format,
    string? ClosestFormat,
    string Backend,
    string ShareMode,
    string Workload,
    string Stage,
    bool Activated,
    bool ProcessedSamples,
    bool Supported,
    bool PlaybackRequested,
    bool Played,
    int? DurationMs,
    int? FrequencyHz,
    int SamplesWritten);

/// <summary>Enumerates Media Foundation video encoders with <c>MFTEnumEx</c>.</summary>
public interface IVideoEncoderSource
{
    OperationResult<VideoEncoderList> List(int limit);
}

/// <summary>Lists video-capture sources and the media types one selected source advertises.</summary>
public interface ICameraFormatSource
{
    OperationResult<CameraFormatList> List(string? device, int limit);
}

/// <summary>Checks a WASAPI format and, when asked, renders a synthetic tone.</summary>
public interface IAudioFormatProbe
{
    Task<OperationResult<AudioFormatProbeResult>> ProbeAsync(
        string endpoint,
        string format,
        int durationMs,
        bool playback,
        CancellationToken cancellationToken);
}
