using System.Globalization;
using System.Text.Json;

using Vestigo.Diagnostics;

namespace Vestigo.Processes;

public static partial class SessionHelper
{
    /// <summary>Builds helper arguments for a capture-support probe. The service caps the duration first.</summary>
    public static List<string> BuildProbeCaptureArguments(string target, string backend, int durationMs) =>
        [Flag, ProbeCaptureCommand, "--target", target, "--backend", backend, "--duration-ms", durationMs.ToString(CultureInfo.InvariantCulture)];

    /// <summary>Parses helper arguments for a capture-support probe.</summary>
    public static bool TryParseProbeCapture(string[] args, out string target, out string backend, out int durationMs, out string? error)
    {
        target = "primary";
        backend = "dxgi";
        durationMs = 1000;
        error = null;
        if (args.Length < 2 || args[0] != Flag || args[1] != ProbeCaptureCommand)
        {
            error = $"Usage: {Flag} {ProbeCaptureCommand} [--target primary|DISPLAY] --backend dxgi|wgc --duration-ms N";
            return false;
        }

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
                case "--target" when value.Length is > 0 and <= 260 && value.IndexOfAny(['\r', '\n']) < 0:
                    target = value;
                    break;
                case "--backend" when value.Equals("dxgi", StringComparison.OrdinalIgnoreCase) || value.Equals("wgc", StringComparison.OrdinalIgnoreCase):
                    backend = value.ToLowerInvariant();
                    break;
                case "--duration-ms" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed is >= 1 and <= 30_000:
                    durationMs = parsed;
                    break;
                default:
                    error = $"Invalid option or value: {args[index]} {value}";
                    return false;
            }
        }

        return true;
    }

    private static int RunProbeCapture(string[] args)
    {
        if (!TryParseProbeCapture(args, out string target, out string backend, out int durationMs, out string? error))
        {
            return Reject(error!);
        }

        if (backend != "dxgi")
        {
            return Reject("Windows Graphics Capture is not available in this build.", "unsupported");
        }

        CaptureProbeResult result = DxgiCaptureProbe.Run(target, TimeSpan.FromMilliseconds(durationMs), CancellationToken.None);
        Console.Out.WriteLine(JsonSerializer.Serialize(result, Json));
        return 0;
    }
}
