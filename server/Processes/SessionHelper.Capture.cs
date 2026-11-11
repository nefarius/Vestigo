using System.Globalization;
using System.Text.Json;

using Vestigo.Desktop;

namespace Vestigo.Processes;

public static partial class SessionHelper
{

    /// <summary>Builds helper arguments (after the executable) for a screenshot.</summary>
    public static List<string> BuildCaptureArguments(ScreenCaptureRequest request)
    {
        List<string> arguments =
        [
            Flag,
            CaptureScreenCommand,
            "--target",
            request.Target,
            "--cursor",
            request.IncludeCursor ? "true" : "false",
            "--max-edge",
            request.MaxEdge.ToString(CultureInfo.InvariantCulture),
            "--full",
            request.IncludeFullImage ? "true" : "false",
            "--max-bytes",
            request.MaxBytes.ToString(CultureInfo.InvariantCulture)
        ];
        if (!string.IsNullOrEmpty(request.Display))
        {
            arguments.Add("--display");
            arguments.Add(request.Display);
        }

        if (!string.IsNullOrEmpty(request.WindowHandle))
        {
            arguments.Add("--hwnd");
            arguments.Add(request.WindowHandle);
        }

        foreach (int pid in request.Pids)
        {
            arguments.Add("--pid");
            arguments.Add(pid.ToString(CultureInfo.InvariantCulture));
        }

        foreach (string name in request.ProcessNames)
        {
            arguments.Add("--name");
            arguments.Add(name);
        }

        if (!string.IsNullOrEmpty(request.TitlePattern))
        {
            arguments.Add("--title");
            arguments.Add(request.TitlePattern);
        }

        if (request.Region is { } region)
        {
            arguments.Add("--rect");
            arguments.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{region.X},{region.Y},{region.Width},{region.Height}"));
        }

        return arguments;
    }

    /// <summary>Parses helper arguments (starting at the flag) into a screenshot request.</summary>
    public static bool TryParseCapture(string[] args, out ScreenCaptureRequest request, out string? error)
    {
        request = ScreenCaptureRequest.DesktopPreview;
        error = null;
        if (args.Length < 2 || args[0] != Flag || args[1] != CaptureScreenCommand)
        {
            error = $"Usage: {Flag} {CaptureScreenCommand} [--target desktop|display|window|region] [--display NAME] [--hwnd HWND] [--pid N] [--name NAME] [--title REGEX] [--rect x,y,w,h] [--cursor true|false] [--max-edge N] [--full true|false] [--max-bytes N]";
            return false;
        }

        string target = ScreenCaptureTarget.Desktop;
        string? display = null;
        string? hwnd = null;
        List<int> pids = [];
        List<string> names = [];
        string? title = null;
        DesktopRect? region = null;
        bool cursor = false;
        int maxEdge = ScreenCaptureTarget.DefaultMaxEdge;
        bool full = false;
        long maxBytes = DesktopOptions.DefaultMaxScreenshotBytes;
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
                case "--target" when ScreenCaptureTarget.TryParse(value, out target, out error):
                    break;
                case "--display" when value.Length > 0:
                    display = value;
                    break;
                case "--hwnd" when value.Length > 0:
                    hwnd = value;
                    break;
                case "--pid" when int.TryParse(value, out int pid) && pid > 0:
                    pids.Add(pid);
                    break;
                case "--name" when value.Length > 0:
                    names.Add(value);
                    break;
                case "--title" when value.Length > 0:
                    title = value;
                    break;
                case "--rect" when TryParseRect(value, out region):
                    break;
                case "--cursor" when TryParseFlag(value, out cursor):
                    break;
                case "--max-edge" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    && parsed is >= 0 and <= ScreenCaptureTarget.MaxMaxEdge:
                    maxEdge = parsed;
                    break;
                case "--full" when TryParseFlag(value, out full):
                    break;
                case "--max-bytes" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
                    && parsed >= 0:
                    maxBytes = parsed;
                    break;
                default:
                    error = error ?? $"Invalid option or value: {args[index]} {value}";
                    return false;
            }
        }

        request = new ScreenCaptureRequest(target, display, hwnd, pids, names, title, region, cursor, maxEdge, full, maxBytes);
        return true;
    }

    private static int RunCaptureScreen(string[] args)
    {
        if (!TryParseCapture(args, out ScreenCaptureRequest request, out string? error))
        {
            return Reject(error!);
        }

        try
        {
            OperationResult<ScreenCaptureResult> result = ScreenCapture.Capture(request);
            if (!result.Success)
            {
                return Reject(result.Error!, result.ErrorCode!, result.Reason);
            }

            Console.Out.WriteLine(JsonSerializer.Serialize(result.Value, Json));
            return 0;
        }
        catch (System.Text.RegularExpressions.RegexParseException ex)
        {
            return Reject("Invalid title pattern: " + ex.Message);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException ex)
        {
            return Reject("Title pattern timed out: " + ex.Message);
        }
    }

    private static bool TryParseRect(string value, out DesktopRect? region)
    {
        region = null;
        string[] parts = value.Split(',');
        if (parts.Length != 4)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) ||
            !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height))
        {
            return false;
        }

        region = new DesktopRect(x, y, width, height);
        return true;
    }
}
