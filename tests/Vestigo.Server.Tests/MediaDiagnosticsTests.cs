using Microsoft.Extensions.Logging.Abstractions;

using Vestigo.Configuration;
using Vestigo.Diagnostics;
using Vestigo.Processes;

namespace Vestigo.Server.Tests;

public class MediaDiagnosticsTests
{
    [Fact]
    public void AudioProbeFormat_AcceptsPcm16AndRejectsOtherLayouts()
    {
        Assert.True(AudioProbeFormat.TryParse("pcm-48000-16-2", out int rate, out int bits, out int channels));
        Assert.Equal(48000, rate);
        Assert.Equal(16, bits);
        Assert.Equal(2, channels);
        Assert.Equal("pcm-48000-16-2", AudioProbeFormat.Describe(rate, bits, channels));
        Assert.False(AudioProbeFormat.TryParse("pcm-48000-32-2", out _, out _, out _));
        Assert.False(AudioProbeFormat.TryParse("pcm-1000-16-2", out _, out _, out _));
        Assert.False(AudioProbeFormat.TryParse("float-48000-32-2", out _, out _, out _));
    }

    [Fact]
    public async Task ProbeAudioFormat_StaysSilentUntilPlaybackIsEnabled()
    {
        FakeAudioFormatProbe probe = new();
        MediaTools silent = Tools(probe, playback: false);
        OperationResult<AudioFormatProbeResult> refused = await silent.ProbeAudioFormat("endpoint", "pcm-48000-16-2", playback: true);
        Assert.Equal("audio_playback_disabled", refused.ErrorCode);
        Assert.Equal(0, probe.Calls);

        OperationResult<AudioFormatProbeResult> check = await silent.ProbeAudioFormat("endpoint", "pcm-48000-16-2", durationMs: 2000);
        Assert.True(check.Success);
        Assert.False(probe.LastPlayback);
        Assert.Equal(0, probe.LastDuration);
        Assert.Equal("format-check", check.Value!.Workload);
        Assert.False(check.Value.Played);

        MediaTools audible = Tools(probe, playback: true);
        OperationResult<AudioFormatProbeResult> tone = await audible.ProbeAudioFormat(" default-render ", "PCM-44100-16-1", durationMs: 50_000, playback: true);
        Assert.True(tone.Success);
        Assert.True(probe.LastPlayback);
        Assert.Equal(2000, probe.LastDuration);
        Assert.Equal("default-render", probe.LastEndpoint);
        Assert.Equal("pcm-44100-16-1", probe.LastFormat);
        Assert.Equal("invalid_request", (await silent.ProbeAudioFormat("endpoint", "pcm-48000-24-2")).ErrorCode);
    }

    [Fact]
    public void ListVideoEncoders_RejectsOtherBackendsAndPassesTheLimit()
    {
        FakeVideoEncoderSource source = new();
        MediaTools tools = Tools(audio: new FakeAudioFormatProbe(), encoders: source);
        Assert.Equal("unsupported", tools.ListVideoEncoders("nvenc").ErrorCode);
        Assert.Equal(0, source.Calls);

        OperationResult<VideoEncoderList> listed = tools.ListVideoEncoders(limit: 5000);
        Assert.True(listed.Success);
        Assert.Equal(200, source.LastLimit);
        Assert.Equal("enumerated", listed.Value!.Stage);
        Assert.False(listed.Value.Activated);
        Assert.False(listed.Value.ProcessedSamples);
        Assert.Equal("MFTEnumEx", listed.Value.DiscoverySource);
    }

    [Fact]
    public void ListCameraFormats_OmitsActivationUntilADeviceIsSelected()
    {
        FakeCameraFormatSource source = new();
        MediaTools tools = Tools(audio: new FakeAudioFormatProbe(), cameras: source);
        OperationResult<CameraFormatList> devices = tools.ListCameraFormats();
        Assert.Equal("enumerated", devices.Value!.Stage);
        Assert.False(devices.Value.Activated);
        Assert.False(devices.Value.ProcessedSamples);
        Assert.Null(source.LastDevice);

        OperationResult<CameraFormatList> formats = tools.ListCameraFormats("Camera");
        Assert.Equal("advertised", formats.Value!.Stage);
        Assert.True(formats.Value.Activated);
        Assert.False(formats.Value.ProcessedSamples);
        Assert.Equal("Camera", source.LastDevice);
        Assert.Equal("invalid_request", tools.ListCameraFormats("bad\nname").ErrorCode);
    }

    [Fact]
    public void ProbeAudioFormatArguments_RequireEndpointFormatAndPlayback()
    {
        Assert.True(SessionHelper.TryParseProbeAudioFormat(
            SessionHelper.BuildProbeAudioFormatArguments("default-render", "pcm-48000-16-2", 1000, false).ToArray(),
            out string endpoint,
            out string format,
            out int duration,
            out bool playback,
            out _));
        Assert.Equal("default-render", endpoint);
        Assert.Equal("pcm-48000-16-2", format);
        Assert.Equal(1000, duration);
        Assert.False(playback);
        Assert.False(SessionHelper.TryParseProbeAudioFormat(
            [SessionHelper.Flag, SessionHelper.ProbeAudioFormatCommand, "--playback", "true"],
            out _,
            out _,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void WindowsMedia_RequiresWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal("windows_required", new MediaFoundationVideoEncoders().List(1).ErrorCode);
        Assert.Equal("windows_required", new MediaFoundationCameras().List(null, 1).ErrorCode);
        Assert.Equal("windows_required", WasapiAudioFormatProbe.Run("default-render", 48000, 16, 2, 0, false, CancellationToken.None).ErrorCode);
    }

    private static MediaTools Tools(
        FakeAudioFormatProbe audio,
        bool playback = false,
        FakeVideoEncoderSource? encoders = null,
        FakeCameraFormatSource? cameras = null) =>
        new(
            encoders ?? new FakeVideoEncoderSource(),
            cameras ?? new FakeCameraFormatSource(),
            audio,
            new VestigoOptions
            {
                Diagnostics =
                {
                    AllowAudioPlayback = playback,
                    MaxAudioProbeDuration = TimeSpan.FromSeconds(2),
                    MaxVideoEncoders = 200,
                    MaxCameraFormats = 64
                }
            },
            NullLogger<MediaTools>.Instance);

    private sealed class FakeVideoEncoderSource : IVideoEncoderSource
    {
        public int Calls { get; private set; }

        public int LastLimit { get; private set; }

        public OperationResult<VideoEncoderList> List(int limit)
        {
            Calls++;
            LastLimit = limit;
            return OperationResult<VideoEncoderList>.Ok(new VideoEncoderList("media-foundation", "MFTEnumEx", "enumerated", false, false, false, []));
        }
    }

    private sealed class FakeCameraFormatSource : ICameraFormatSource
    {
        public string? LastDevice { get; private set; }

        public OperationResult<CameraFormatList> List(string? device, int limit)
        {
            LastDevice = device;
            string stage = device is null ? "enumerated" : "advertised";
            return OperationResult<CameraFormatList>.Ok(new CameraFormatList(stage, device is not null, false, false, []));
        }
    }

    private sealed class FakeAudioFormatProbe : IAudioFormatProbe
    {
        public int Calls { get; private set; }

        public bool LastPlayback { get; private set; }

        public int LastDuration { get; private set; }

        public string? LastEndpoint { get; private set; }

        public string? LastFormat { get; private set; }

        public Task<OperationResult<AudioFormatProbeResult>> ProbeAsync(
            string endpoint,
            string format,
            int durationMs,
            bool playback,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastPlayback = playback;
            LastDuration = durationMs;
            LastEndpoint = endpoint;
            LastFormat = format;
            return Task.FromResult(OperationResult<AudioFormatProbeResult>.Ok(new AudioFormatProbeResult(
                endpoint,
                endpoint,
                format,
                null,
                "wasapi",
                "shared",
                playback ? "synthetic-tone" : "format-check",
                playback ? "processed" : "activated",
                true,
                playback,
                true,
                playback,
                playback,
                playback ? durationMs : null,
                playback ? 440 : null,
                playback ? 1 : 0)));
        }
    }
}
