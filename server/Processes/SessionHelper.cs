using System.Globalization;
using System.Text.Json;

using Vestigo.Audio;
using Vestigo.Desktop;

namespace Vestigo.Processes;

/// <summary>
///     Handles <c>Vestigo.exe --session-helper ...</c>. Program.cs calls this before the web host starts. The helper
///     runs inside the console user's session so it can see that desktop's windows, prints one JSON document to
///     stdout, and exits.
/// </summary>
public static class SessionHelper
{
    public const string Flag = "--session-helper";

    public const string CloseWindowCommand = "close-window";

    public const string ListWindowsCommand = "list-windows";

    public const string ListDisplaysCommand = "list-displays";

    public const string ListAudioCommand = "list-audio";

    public const string SetDefaultAudioCommand = "set-default-audio";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool IsInvocation(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Flag, StringComparison.Ordinal);

    /// <summary>Builds helper arguments (after the executable) for a close request.</summary>
    public static List<string> BuildCloseArguments(CloseSelector selector, int graceSeconds, int callerPid)
    {
        List<string> arguments = [Flag, CloseWindowCommand, "--timeout", graceSeconds.ToString(CultureInfo.InvariantCulture), "--exclude-pid", callerPid.ToString(CultureInfo.InvariantCulture)];
        foreach (int pid in selector.Pids)
        {
            arguments.Add("--pid");
            arguments.Add(pid.ToString(CultureInfo.InvariantCulture));
        }

        foreach (string name in selector.ProcessNames)
        {
            arguments.Add("--name");
            arguments.Add(name);
        }

        if (!string.IsNullOrEmpty(selector.TitlePattern))
        {
            arguments.Add("--title");
            arguments.Add(selector.TitlePattern);
        }

        return arguments;
    }

    /// <summary>Parses helper arguments (starting at the flag) into a selector, grace period and excluded pid.</summary>
    public static bool TryParseClose(
        string[] args,
        out CloseSelector selector,
        out int graceSeconds,
        out int excludePid,
        out string? error)
    {
        selector = new CloseSelector([], [], null);
        graceSeconds = 10;
        excludePid = 0;
        error = null;
        if (args.Length < 2 || args[0] != Flag || args[1] != CloseWindowCommand)
        {
            error = $"Usage: {Flag} {CloseWindowCommand} [--pid N] [--name NAME] [--title REGEX] [--timeout SECONDS]";
            return false;
        }

        List<int> pids = [];
        List<string> names = [];
        string? title = null;
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
                case "--pid" when int.TryParse(value, out int pid) && pid > 0:
                    pids.Add(pid);
                    break;
                case "--name" when value.Length > 0:
                    names.Add(value);
                    break;
                case "--title" when value.Length > 0:
                    title = value;
                    break;
                case "--timeout" when int.TryParse(value, out int seconds) && seconds is >= 0 and <= 600:
                    graceSeconds = seconds;
                    break;
                case "--exclude-pid" when int.TryParse(value, out int exclude):
                    excludePid = exclude;
                    break;
                default:
                    error = $"Invalid option or value: {args[index]} {value}";
                    return false;
            }
        }

        selector = new CloseSelector(pids, names, title);
        if (selector.IsEmpty)
        {
            error = "At least one of --pid, --name or --title is required.";
            return false;
        }

        return true;
    }

    /// <summary>Builds helper arguments (after the executable) for a window listing.</summary>
    public static List<string> BuildListWindowsArguments(WindowSelector selector)
    {
        List<string> arguments =
        [
            Flag,
            ListWindowsCommand,
            "--include-hidden",
            selector.IncludeHidden ? "true" : "false",
            "--include-children",
            selector.IncludeChildren ? "true" : "false",
            "--limit",
            selector.Limit.ToString(CultureInfo.InvariantCulture)
        ];
        foreach (int pid in selector.Pids)
        {
            arguments.Add("--pid");
            arguments.Add(pid.ToString(CultureInfo.InvariantCulture));
        }

        foreach (string name in selector.ProcessNames)
        {
            arguments.Add("--name");
            arguments.Add(name);
        }

        if (!string.IsNullOrEmpty(selector.TitlePattern))
        {
            arguments.Add("--title");
            arguments.Add(selector.TitlePattern);
        }

        if (!string.IsNullOrEmpty(selector.ClassName))
        {
            arguments.Add("--class");
            arguments.Add(selector.ClassName);
        }

        return arguments;
    }

    /// <summary>Parses helper arguments (starting at the flag) into a window listing selector.</summary>
    public static bool TryParseListWindows(string[] args, out WindowSelector selector, out string? error)
    {
        selector = WindowSelector.All;
        error = null;
        if (args.Length < 2 || args[0] != Flag || args[1] != ListWindowsCommand)
        {
            error = $"Usage: {Flag} {ListWindowsCommand} [--pid N] [--name NAME] [--title REGEX] [--class CLASS] [--include-hidden true|false] [--include-children true|false] [--limit N]";
            return false;
        }

        List<int> pids = [];
        List<string> names = [];
        string? title = null;
        string? className = null;
        bool includeHidden = false;
        bool includeChildren = false;
        int limit = WindowSelector.DefaultLimit;
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
                case "--pid" when int.TryParse(value, out int pid) && pid > 0:
                    pids.Add(pid);
                    break;
                case "--name" when value.Length > 0:
                    names.Add(value);
                    break;
                case "--title" when value.Length > 0:
                    title = value;
                    break;
                case "--class" when value.Length > 0:
                    className = value;
                    break;
                case "--include-hidden" when TryParseFlag(value, out includeHidden):
                    break;
                case "--include-children" when TryParseFlag(value, out includeChildren):
                    break;
                case "--limit" when int.TryParse(value, out int parsed) && parsed is >= 1 and <= WindowSelector.MaxLimit:
                    limit = parsed;
                    break;
                default:
                    error = $"Invalid option or value: {args[index]} {value}";
                    return false;
            }
        }

        selector = new WindowSelector(pids, names, title, className, includeHidden, includeChildren, limit);
        return true;
    }

    /// <summary>Builds helper arguments (after the executable) for a display listing.</summary>
    public static List<string> BuildListDisplaysArguments() => [Flag, ListDisplaysCommand];

    /// <summary>Builds helper arguments (after the executable) for an audio endpoint listing.</summary>
    public static List<string> BuildListAudioArguments(AudioSelector selector)
    {
        List<string> arguments =
        [
            Flag,
            ListAudioCommand,
            "--flow",
            selector.Flow,
            "--state",
            selector.State,
            "--include-sessions",
            selector.IncludeSessions ? "true" : "false",
            "--meter-ms",
            selector.MeterSampleMilliseconds.ToString(CultureInfo.InvariantCulture)
        ];
        if (!string.IsNullOrEmpty(selector.Id))
        {
            arguments.Add("--id");
            arguments.Add(selector.Id);
        }

        return arguments;
    }

    /// <summary>Parses helper arguments (starting at the flag) into an audio listing selector.</summary>
    public static bool TryParseListAudio(string[] args, out AudioSelector selector, out string? error)
    {
        selector = AudioSelector.Defaults;
        error = null;
        if (args.Length < 2 || args[0] != Flag || args[1] != ListAudioCommand)
        {
            error = $"Usage: {Flag} {ListAudioCommand} [--flow all|render|capture] [--state active|all] [--id ID] [--include-sessions true|false] [--meter-ms N]";
            return false;
        }

        string flow = AudioSelector.FlowAll;
        string state = AudioSelector.StateActive;
        string? id = null;
        bool includeSessions = true;
        int meterMs = AudioOptions.DefaultMeterSampleMilliseconds;
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
                case "--flow" when AudioSelector.TryParseFlow(value, out flow, out error):
                    break;
                case "--state" when AudioSelector.TryParseState(value, out state, out error):
                    break;
                case "--id" when value.Length > 0:
                    id = value;
                    break;
                case "--include-sessions" when TryParseFlag(value, out includeSessions):
                    break;
                case "--meter-ms" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    && parsed is >= 0 and <= AudioOptions.MaxMeterSampleMilliseconds:
                    meterMs = parsed;
                    break;
                default:
                    error = error ?? $"Invalid option or value: {args[index]} {value}";
                    return false;
            }
        }

        selector = new AudioSelector(flow, state, id, includeSessions, meterMs);
        return true;
    }

    /// <summary>Builds helper arguments (after the executable) for setting the default audio endpoint.</summary>
    public static List<string> BuildSetDefaultAudioArguments(string id, string role) =>
        [Flag, SetDefaultAudioCommand, "--id", id, "--role", role];

    /// <summary>Parses helper arguments (starting at the flag) for setting the default audio endpoint.</summary>
    public static bool TryParseSetDefaultAudio(string[] args, out string id, out string role, out string? error)
    {
        id = "";
        role = AudioSelector.RoleDefault;
        error = null;
        if (args.Length < 2 || args[0] != Flag || args[1] != SetDefaultAudioCommand)
        {
            error = $"Usage: {Flag} {SetDefaultAudioCommand} --id ID [--role default|communications|all]";
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
                case "--id" when value.Length > 0:
                    id = value;
                    break;
                case "--role" when AudioSelector.TryParseRole(value, out role, out _, out error):
                    break;
                default:
                    error = error ?? $"Invalid option or value: {args[index]} {value}";
                    return false;
            }
        }

        if (id.Length == 0)
        {
            error = "--id is required.";
            return false;
        }

        return true;
    }

    /// <summary>Parses helper arguments (starting at the flag) for a display listing.</summary>
    public static bool TryParseListDisplays(string[] args, out string? error)
    {
        error = null;
        if (args.Length < 2 || args[0] != Flag || args[1] != ListDisplaysCommand)
        {
            error = $"Usage: {Flag} {ListDisplaysCommand}";
            return false;
        }

        if (args.Length > 2)
        {
            error = $"Invalid option or value: {args[2]}";
            return false;
        }

        return true;
    }

    /// <summary>Runs the helper command and returns the process exit code.</summary>
    public static int Run(string[] args)
    {
        if (args.Length < 2 || args[0] != Flag)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { error = $"Usage: {Flag} <{CloseWindowCommand}|{ListWindowsCommand}|{ListDisplaysCommand}|{ListAudioCommand}|{SetDefaultAudioCommand}> ..." }, Json));
            return 2;
        }

        return args[1] switch
        {
            CloseWindowCommand => RunClose(args),
            ListWindowsCommand => RunListWindows(args),
            ListDisplaysCommand => RunListDisplays(args),
            ListAudioCommand => RunListAudio(args),
            SetDefaultAudioCommand => RunSetDefaultAudio(args),
            _ => Reject($"Unknown session-helper command '{args[1]}'.")
        };
    }

    private static int RunClose(string[] args)
    {
        if (!TryParseClose(args, out CloseSelector selector, out int grace, out int excludePid, out string? error))
        {
            return Reject(error!);
        }

        try
        {
            CloseReport report = WindowCloser.Close(selector, TimeSpan.FromSeconds(grace), excludePid == 0 ? Environment.ProcessId : excludePid);
            Console.Out.WriteLine(JsonSerializer.Serialize(report, Json));
            return 0;
        }
        catch (System.Text.RegularExpressions.RegexParseException ex)
        {
            return Reject("Invalid title pattern: " + ex.Message);
        }
    }

    private static int RunListWindows(string[] args)
    {
        if (!TryParseListWindows(args, out WindowSelector selector, out string? error))
        {
            return Reject(error!);
        }

        try
        {
            WindowListResult result = WindowEnumerator.List(selector);
            Console.Out.WriteLine(JsonSerializer.Serialize(result, Json));
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

    private static int RunListDisplays(string[] args)
    {
        if (!TryParseListDisplays(args, out string? error))
        {
            return Reject(error!);
        }

        DisplayListResult result = DisplayTopology.List();
        Console.Out.WriteLine(JsonSerializer.Serialize(result, Json));
        return 0;
    }

    private static int RunListAudio(string[] args)
    {
        if (!TryParseListAudio(args, out AudioSelector selector, out string? error))
        {
            return Reject(error!);
        }

        OperationResult<AudioEndpointListResult> result = AudioEndpoints.List(selector);
        if (!result.Success)
        {
            return Reject(result.Error!);
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(result.Value, Json));
        return 0;
    }

    private static int RunSetDefaultAudio(string[] args)
    {
        // Gated by AudioTools from the service's VestigoOptions. This process is the console
        // user and must not open the machine settings file (it holds API tokens).
        if (!TryParseSetDefaultAudio(args, out string id, out string role, out string? error))
        {
            return Reject(error!);
        }

        OperationResult<SetDefaultAudioResult> result = AudioEndpoints.SetDefault(id, role);
        if (!result.Success)
        {
            return Reject(result.Error!);
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(result.Value, Json));
        return 0;
    }

    private static int Reject(string error)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new { error }, Json));
        return 2;
    }

    private static bool TryParseFlag(string value, out bool flag)
    {
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1")
        {
            flag = true;
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || value == "0")
        {
            flag = false;
            return true;
        }

        flag = false;
        return false;
    }
}
