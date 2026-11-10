using System.Globalization;
using System.Text.Json;

using Vestigo.Desktop;

namespace Vestigo.Processes;

public static partial class SessionHelper
{
    public const string InspectWindowCommand = "inspect-window";

    public const string WindowEventsCommand = "window-events";

    public static List<string> BuildInspectWindowArguments(string handle, int? expectedPid, DateTimeOffset? expectedStart)
    {
        List<string> arguments = [Flag, InspectWindowCommand, "--handle", handle];
        if (expectedPid is int pid)
        {
            arguments.Add("--pid");
            arguments.Add(pid.ToString(CultureInfo.InvariantCulture));
        }

        if (expectedStart is DateTimeOffset start)
        {
            arguments.Add("--start");
            arguments.Add(start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }

        return arguments;
    }

    public static bool TryParseInspectWindow(string[] args, out string handle, out int? pid, out DateTimeOffset? start, out string? error)
    {
        handle = string.Empty;
        pid = null;
        start = null;
        error = null;
        if (args.Length < 4 || args[0] != Flag || args[1] != InspectWindowCommand || args[2] != "--handle")
        {
            error = $"Usage: {Flag} {InspectWindowCommand} --handle HWND [--pid N] [--start ROUNDTRIP]";
            return false;
        }

        handle = args[3];
        for (int index = 4; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                error = $"Option '{args[index]}' needs a value.";
                return false;
            }

            string value = args[index + 1];
            switch (args[index])
            {
                case "--pid" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0:
                    pid = parsed;
                    break;
                case "--start" when DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed):
                    start = parsed.ToUniversalTime();
                    break;
                default:
                    error = $"Invalid option or value: {args[index]} {value}";
                    return false;
            }
        }

        return WindowInspection.TryParseHandle(handle, out _);
    }

    public static List<string> BuildWindowEventArguments(WindowEventStart start)
    {
        List<string> arguments =
        [
            Flag,
            WindowEventsCommand,
            "--duration-ms",
            Math.Max(1, (int)start.Duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            "--max-events",
            start.MaxEvents.ToString(CultureInfo.InvariantCulture)
        ];
        if (!string.IsNullOrWhiteSpace(start.Handle))
        {
            arguments.Add("--handle");
            arguments.Add(start.Handle);
        }

        if (start.ProcessId is int pid)
        {
            arguments.Add("--pid");
            arguments.Add(pid.ToString(CultureInfo.InvariantCulture));
        }

        if (start.ExpectedStartTime is DateTimeOffset expected)
        {
            arguments.Add("--start");
            arguments.Add(expected.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }

        return arguments;
    }

    public static bool TryParseWindowEvents(string[] args, out WindowEventStart start, out string? error)
    {
        start = new WindowEventStart(null, null, null, TimeSpan.FromSeconds(1), 1);
        error = null;
        if (args.Length < 6 || args[0] != Flag || args[1] != WindowEventsCommand)
        {
            error = $"Usage: {Flag} {WindowEventsCommand} --duration-ms N --max-events N [--handle HWND] [--pid N] [--start ROUNDTRIP]";
            return false;
        }

        string? handle = null;
        int? pid = null;
        DateTimeOffset? expected = null;
        int? durationMs = null;
        int? maxEvents = null;
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
                case "--handle" when WindowInspection.TryParseHandle(value, out _):
                    handle = value;
                    break;
                case "--pid" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0:
                    pid = parsed;
                    break;
                case "--start" when DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed):
                    expected = parsed.ToUniversalTime();
                    break;
                case "--duration-ms" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0:
                    durationMs = parsed;
                    break;
                case "--max-events" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0:
                    maxEvents = parsed;
                    break;
                default:
                    error = $"Invalid option or value: {args[index]} {value}";
                    return false;
            }
        }

        if (handle is null && pid is null || durationMs is null || maxEvents is null)
        {
            error = "window-events needs a handle or pid, a duration, and a maximum event count.";
            return false;
        }

        start = new WindowEventStart(handle, pid, expected, TimeSpan.FromMilliseconds(durationMs.Value), maxEvents.Value);
        return true;
    }

    private static int RunInspectWindow(string[] args)
    {
        if (!TryParseInspectWindow(args, out string handle, out int? pid, out DateTimeOffset? start, out string? error))
        {
            return Reject(error ?? "Invalid inspect-window request.");
        }

        OperationResult<WindowInspectionReport> result = new WindowInspector(new WindowsWindowInspectionSource()).Inspect(handle, pid, start);
        if (!result.Success)
        {
            return Reject(result.Error!, result.ErrorCode!);
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(result.Value, Json));
        return 0;
    }

    private static int RunWindowEvents(string[] args)
    {
        if (!TryParseWindowEvents(args, out WindowEventStart start, out string? error))
        {
            return Reject(error ?? "Invalid window-events request.");
        }

        return WindowEventCaptureLoop.Run(start, Console.Out, CancellationToken.None);
    }
}
