using System.Diagnostics;
using System.Runtime.InteropServices;

using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Vestigo.Diagnostics;

/// <summary>
///     Checks a shared-mode mix format. Playback renders a 440 Hz tone and never opens a capture client,
///     changes the default endpoint, or requests exclusive mode.
/// </summary>
internal static class WasapiAudioFormatProbe
{
    public const int ToneHz = 440;

    public static OperationResult<AudioFormatProbeResult> Run(
        string endpoint,
        int rate,
        int bits,
        int channels,
        int durationMs,
        bool playback,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<AudioFormatProbeResult>.Fail("windows_required", "Audio format probes require Windows.");
        }

        if (!AudioProbeFormat.TryParse(AudioProbeFormat.Describe(rate, bits, channels), out _, out _, out _))
        {
            return OperationResult<AudioFormatProbeResult>.Fail("invalid_request", "format must be pcm-rate-16-channels.");
        }

        try
        {
            return Probe(endpoint, rate, bits, channels, durationMs, playback, cancellationToken);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return OperationResult<AudioFormatProbeResult>.Fail(CodeFor(ex.HResult), "The audio endpoint could not be probed.");
        }
    }

    private static OperationResult<AudioFormatProbeResult> Probe(
        string endpoint,
        int rate,
        int bits,
        int channels,
        int durationMs,
        bool playback,
        CancellationToken cancellationToken)
    {
        using MMDeviceEnumerator enumerator = new();
        using MMDevice device = Open(enumerator, endpoint);
        if (playback && device.DataFlow != DataFlow.Render)
        {
            return OperationResult<AudioFormatProbeResult>.Fail("invalid_request", "Playback is only available for a render endpoint.");
        }

        WaveFormat format = new(rate, bits, channels);
        AudioClient client = device.AudioClient;
        bool supported = client.IsFormatSupported(AudioClientShareMode.Shared, format, out WaveFormatExtensible closest);
        string requested = AudioProbeFormat.Describe(rate, bits, channels);
        string? closestText = closest is null ? null : AudioProbeFormat.Describe(closest.SampleRate, closest.BitsPerSample, closest.Channels);
        if (!playback || !supported)
        {
            return OperationResult<AudioFormatProbeResult>.Ok(new AudioFormatProbeResult(
                endpoint,
                device.ID,
                requested,
                closestText,
                "wasapi",
                "shared",
                "format-check",
                "activated",
                true,
                false,
                supported,
                playback,
                false,
                null,
                null,
                0));
        }

        cancellationToken.ThrowIfCancellationRequested();
        int samples = Render(client, format, durationMs, cancellationToken);
        return OperationResult<AudioFormatProbeResult>.Ok(new AudioFormatProbeResult(
            endpoint,
            device.ID,
            requested,
            closestText,
            "wasapi",
            "shared",
            "synthetic-tone",
            "processed",
            true,
            samples > 0,
            true,
            true,
            samples > 0,
            durationMs,
            ToneHz,
            samples));
    }

    private static MMDevice Open(MMDeviceEnumerator enumerator, string endpoint)
    {
        if (endpoint.Equals("default-render", StringComparison.OrdinalIgnoreCase))
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        return enumerator.GetDevice(endpoint);
    }

    private static int Render(AudioClient client, WaveFormat format, int durationMs, CancellationToken cancellationToken)
    {
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, 1_000_000, 0, format, Guid.Empty);
        AudioRenderClient render = client.AudioRenderClient;
        int samples = 0;
        int sampleIndex = 0;
        client.Start();
        try
        {
            long started = Stopwatch.GetTimestamp();
            TimeSpan budget = TimeSpan.FromMilliseconds(durationMs);
            while (Stopwatch.GetElapsedTime(started) < budget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int frames = client.BufferSize - client.CurrentPadding;
                if (frames <= 0)
                {
                    Thread.Sleep(5);
                    continue;
                }

                samples += Write(render, Math.Min(frames, format.SampleRate / 50), format, ref sampleIndex);
            }
        }
        finally
        {
            client.Stop();
            render.Dispose();
        }

        return samples;
    }

    private static int Write(AudioRenderClient render, int frames, WaveFormat format, ref int sampleIndex)
    {
        int channels = format.Channels;
        byte[] data = new byte[frames * channels * 2];
        for (int frame = 0; frame < frames; frame++)
        {
            double angle = 2 * Math.PI * ToneHz * sampleIndex / format.SampleRate;
            short value = (short)(Math.Sin(angle) * 0.2 * short.MaxValue);
            sampleIndex++;
            for (int channel = 0; channel < channels; channel++)
            {
                int offset = (frame * channels + channel) * 2;
                data[offset] = (byte)value;
                data[offset + 1] = (byte)(value >> 8);
            }
        }

        IntPtr buffer = render.GetBuffer(frames);
        Marshal.Copy(data, 0, buffer, data.Length);
        render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
        return frames * channels;
    }

    private static string CodeFor(int hresult) => unchecked((uint)hresult) switch
    {
        0x80070005 => "access_denied",
        0x80070020 or 0x80070021 or 0x8889000A or 0x8889000E => "device_busy",
        0x88890004 => "device_invalidated",
        0x80070002 or 0x80070490 => "not_found",
        0x88890010 => "unavailable",
        _ => "audio_failed"
    };
}
