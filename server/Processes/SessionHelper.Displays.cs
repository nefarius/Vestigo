using System.Text.Json;

using Vestigo.Desktop;

namespace Vestigo.Processes;

public static partial class SessionHelper
{

    /// <summary>Builds helper arguments (after the executable) for a display listing.</summary>
    public static List<string> BuildListDisplaysArguments(bool includeModes = false) =>
        includeModes
            ? [Flag, ListDisplaysCommand, "--include-modes", "true"]
            : [Flag, ListDisplaysCommand];

    /// <summary>Parses helper arguments (starting at the flag) for a display listing.</summary>
    public static bool TryParseListDisplays(string[] args, out bool includeModes, out string? error)
    {
        includeModes = false;
        error = null;
        if (args.Length < 2 || args[0] != Flag || args[1] != ListDisplaysCommand)
        {
            error = $"Usage: {Flag} {ListDisplaysCommand} [--include-modes true|false]";
            return false;
        }

        for (int index = 2; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                error = $"Option '{args[index]}' needs a value.";
                return false;
            }

            if (args[index] == "--include-modes" && TryParseFlag(args[index + 1], out includeModes))
            {
                continue;
            }

            error = $"Invalid option or value: {args[index]} {args[index + 1]}";
            return false;
        }

        return true;
    }

    private static int RunListDisplays(string[] args)
    {
        if (!TryParseListDisplays(args, out bool includeModes, out string? error))
        {
            return Reject(error!);
        }

        DisplayListResult result = DisplayTopology.List(includeModes);
        Console.Out.WriteLine(JsonSerializer.Serialize(result, Json));
        return 0;
    }
}
