using System.Globalization;
using System.Text.Json;

using Vestigo.Desktop;

namespace Vestigo.Processes;

public static partial class SessionHelper
{

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
}
