using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;

namespace Vestigo.Diagnostics;

/// <summary>Media Foundation inventories and a WASAPI format probe. Audible playback is a separate gate.</summary>
[McpServerToolType]
public sealed class MediaTools(
    IVideoEncoderSource encoders,
    ICameraFormatSource cameras,
    IAudioFormatProbe audio,
    VestigoOptions options,
    ILogger<MediaTools> logger)
{
    [McpServerTool(Name = "list_video_encoders")]
    [Description(
        "Lists video encoders registered with Media Foundation, using MFTEnumEx. "
        + "backend must be media-foundation. The result is an enumeration: transforms are not activated and no frames are submitted. "
        + "It is not an inventory of vendor encoder APIs that do not register an MFT. "
        + "discoverySource is MFTEnumEx, stage is enumerated, activated and processedSamples are false.")]
    public OperationResult<VideoEncoderList> ListVideoEncoders(
        [Description("Discovery backend. Only media-foundation is implemented.")] string backend = "media-foundation",
        [Description("Maximum encoders. Clamped to 1..Vestigo:Diagnostics:MaxVideoEncoders.")] int limit = 100)
    {
        if (!string.IsNullOrWhiteSpace(backend) && !backend.Trim().Equals("media-foundation", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<VideoEncoderList>.Fail("unsupported", "Only the media-foundation backend is implemented. Vendor encoder SDKs are not queried.");
        }

        int bounded = Math.Clamp(limit, 1, options.Diagnostics.MaxVideoEncoders);
        OperationResult<VideoEncoderList> result = encoders.List(bounded);
        logger.LogInformation("list_video_encoders success {Success} code {Code}", result.Success, result.ErrorCode);
        return result;
    }

    [McpServerTool(Name = "list_camera_formats")]
    [Description(
        "Lists video capture devices, or the advertised media types of one device. "
        + "Omit device to enumerate names and symbolic links without activating a source. "
        + "Pass a symbolic link or friendly name to activate that source, read its advertised types, and shut it down. "
        + "No camera sample is read. Some cameras may indicate activity while the source is open. "
        + "stage is enumerated or advertised. processedSamples is false.")]
    public OperationResult<CameraFormatList> ListCameraFormats(
        [Description("Symbolic link or friendly name. Omit to list devices without formats.")] string? device = null,
        [Description("Maximum devices when device is omitted, or maximum formats when one device is selected. Clamped to 1..Vestigo:Diagnostics:MaxCameraFormats.")] int limit = 32)
    {
        if (device is not null && (device.Length > 512 || device.IndexOfAny(['\r', '\n']) >= 0))
        {
            return OperationResult<CameraFormatList>.Fail("invalid_request", "device must be a symbolic link or friendly name.");
        }

        int bounded = Math.Clamp(limit, 1, options.Diagnostics.MaxCameraFormats);
        OperationResult<CameraFormatList> result = cameras.List(string.IsNullOrWhiteSpace(device) ? null : device.Trim(), bounded);
        logger.LogInformation("list_camera_formats success {Success} code {Code}", result.Success, result.ErrorCode);
        return result;
    }

    [McpServerTool(Name = "probe_audio_format")]
    [Description(
        "Checks whether a render or capture endpoint supports a shared-mode PCM format. "
        + "format is pcm-rate-16-channels, for example pcm-48000-16-2. endpoint is an endpoint id or default-render. "
        + "The format check does not start the stream and does not emit sound. "
        + "playback true renders a 440 Hz tone for durationMs, only when Vestigo:Diagnostics:AllowAudioPlayback is true, and only on a render endpoint. "
        + "The probe runs in the console user's session, uses shared mode, and does not change the default endpoint or record a microphone. "
        + "A result names the format, duration, backend wasapi, and workload format-check or synthetic-tone. It is not an application performance measurement.")]
    public Task<OperationResult<AudioFormatProbeResult>> ProbeAudioFormat(
        [Description("Endpoint id, or default-render.")] string endpoint,
        [Description("pcm-rate-16-channels. Rate is 8000..192000. Channels are 1 or 2.")] string format,
        [Description("Playback length in milliseconds. Ignored unless playback is true. Capped by Vestigo:Diagnostics:MaxAudioProbeDuration.")] int durationMs = 1000,
        [Description("Render a synthetic tone. Requires Vestigo:Diagnostics:AllowAudioPlayback.")] bool playback = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 512 || endpoint.IndexOfAny(['\r', '\n']) >= 0)
        {
            return Task.FromResult(OperationResult<AudioFormatProbeResult>.Fail("invalid_request", "endpoint must be an audio endpoint id or default-render."));
        }

        if (!AudioProbeFormat.TryParse(format, out _, out _, out _))
        {
            return Task.FromResult(OperationResult<AudioFormatProbeResult>.Fail("invalid_request", "format must be pcm-rate-16-channels."));
        }

        if (playback && !options.Diagnostics.AllowAudioPlayback)
        {
            logger.LogInformation("probe_audio_format refused {Code}", "audio_playback_disabled");
            return Task.FromResult(OperationResult<AudioFormatProbeResult>.Fail(
                "audio_playback_disabled",
                "Audio playback is disabled. Set Vestigo:Diagnostics:AllowAudioPlayback to true."));
        }

        int capped = playback
            ? (int)Math.Min(Math.Max(durationMs, 1), options.Diagnostics.MaxAudioProbeDuration.TotalMilliseconds)
            : 0;
        logger.LogInformation("probe_audio_format playback {Playback}", playback);
        return audio.ProbeAsync(endpoint.Trim(), format.Trim().ToLowerInvariant(), capped, playback, cancellationToken);
    }
}
