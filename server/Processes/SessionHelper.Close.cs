using System.Globalization;
using System.Text.Json;

namespace Vestigo.Processes;

public static partial class SessionHelper
{

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
}
