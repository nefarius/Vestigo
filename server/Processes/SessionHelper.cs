using System.Text.Json;

namespace Vestigo.Processes;

/// <summary>
///     Handles <c>Vestigo.exe --session-helper close-window ...</c>. Program.cs calls this before the web host
///     starts. The helper runs inside the console user's session so it can see that desktop's windows, prints one
///     JSON document to stdout, and exits.
/// </summary>
public static class SessionHelper
{
    public const string Flag = "--session-helper";

    public const string CloseWindowCommand = "close-window";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool IsInvocation(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Flag, StringComparison.Ordinal);

    /// <summary>Builds helper arguments (after the executable) for a close request.</summary>
    public static List<string> BuildCloseArguments(CloseSelector selector, int graceSeconds, int callerPid)
    {
        List<string> arguments = [Flag, CloseWindowCommand, "--timeout", graceSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "--exclude-pid", callerPid.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        foreach (int pid in selector.Pids)
        {
            arguments.Add("--pid");
            arguments.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

    /// <summary>Runs the helper command and returns the process exit code.</summary>
    public static int Run(string[] args)
    {
        if (!TryParseClose(args, out CloseSelector selector, out int grace, out int excludePid, out string? error))
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { error }, Json));
            return 2;
        }

        try
        {
            CloseReport report = WindowCloser.Close(selector, TimeSpan.FromSeconds(grace), excludePid == 0 ? Environment.ProcessId : excludePid);
            Console.Out.WriteLine(JsonSerializer.Serialize(report, Json));
            return 0;
        }
        catch (System.Text.RegularExpressions.RegexParseException ex)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { error = "Invalid title pattern: " + ex.Message }, Json));
            return 2;
        }
    }
}
