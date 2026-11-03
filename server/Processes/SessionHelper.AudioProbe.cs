using System.Globalization;
using System.Text.Json;

using Vestigo.Diagnostics;

namespace Vestigo.Processes;

public static partial class SessionHelper
{
    /// <summary>Builds helper arguments for an audio format probe. The service decides whether playback is allowed.</summary>
    public static List<string> BuildProbeAudioFormatArguments(string endpoint, string format, int durationMs, bool playback) =>
    [
        Flag,
        ProbeAudioFormatCommand,
        "--endpoint",
        endpoint,
        "--format",
        format,
        "--duration-ms",
        durationMs.ToString(CultureInfo.InvariantCulture),
        "--playback",
        playback ? "true" : "false"
    ];

    /// <summary>Parses helper arguments for an audio format probe.</summary>
    public static bool TryParseProbeAudioFormat(
        string[] args,
        out string endpoint,
        out string format,
        out int durationMs,
        out bool playback,
        out string? error)
    {
        endpoint = "";
        format = "";
        durationMs = 0;
        playback = false;
        error = null;
        if (args.Length < 2 || args[0] != Flag || args[1] != ProbeAudioFormatCommand)
        {
            error = $"Usage: {Flag} {ProbeAudioFormatCommand} --endpoint ID --format pcm-rate-16-channels --duration-ms N --playback true|false";
            return false;
        }

        bool sawPlayback = false;
        for (int index = 2; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                error = $"Option '{args[index]}' needs a value.";
                return false;
            }

            string value = args[index + 1];
            switch (args[index])
            {
                case "--endpoint" when value.Length is > 0 and <= 512 && value.IndexOfAny(['\r', '\n']) < 0:
                    endpoint = value;
                    break;
                case "--format" when AudioProbeFormat.TryParse(value, out _, out _, out _):
                    format = value.Trim().ToLowerInvariant();
                    break;
                case "--duration-ms" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed is >= 0 and <= 5_000:
                    durationMs = parsed;
                    break;
                case "--playback" when TryParseFlag(value, out playback):
                    sawPlayback = true;
                    break;
                default:
                    error = $"Invalid option or value: {args[index]}";
                    return false;
            }
        }

        if (endpoint.Length == 0 || format.Length == 0 || !sawPlayback)
        {
            error = "endpoint, format, and playback are required.";
            return false;
        }

        return true;
    }

    private static int RunProbeAudioFormat(string[] args)
    {
        if (!TryParseProbeAudioFormat(args, out string endpoint, out string format, out int durationMs, out bool playback, out string? error))
        {
            return Reject(error!);
        }

        if (!AudioProbeFormat.TryParse(format, out int rate, out int bits, out int channels))
        {
            return Reject("format must be pcm-rate-16-channels.");
        }

        OperationResult<AudioFormatProbeResult> result = WasapiAudioFormatProbe.Run(
            endpoint,
            rate,
            bits,
            channels,
            playback ? durationMs : 0,
            playback,
            CancellationToken.None);
        if (!result.Success)
        {
            return Reject(result.Error!, result.ErrorCode!);
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(result.Value, Json));
        return 0;
    }
}
