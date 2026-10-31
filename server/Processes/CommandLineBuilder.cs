using System.Text;

namespace Vestigo.Processes;

/// <summary>
///     Builds a CreateProcess command line that <c>CommandLineToArgvW</c> and the C runtime split back into the
///     original arguments.
/// </summary>
public static class CommandLineBuilder
{
    /// <summary>Maximum characters CreateProcess accepts in a command line, including the terminator.</summary>
    public const int MaxLength = 32766;

    public static string Build(string executable, IReadOnlyList<string> arguments)
    {
        StringBuilder builder = new();
        AppendExecutable(builder, executable);
        foreach (string argument in arguments)
        {
            builder.Append(' ');
            AppendArgument(builder, argument);
        }

        return builder.ToString();
    }

    /// <summary>The program name is parsed specially (no escapes), so it is only wrapped in quotes.</summary>
    internal static void AppendExecutable(StringBuilder result, string executable)
    {
        if (executable.IndexOfAny([' ', '\t']) >= 0)
        {
            result.Append('"').Append(executable).Append('"');
        }
        else
        {
            result.Append(executable);
        }
    }

    /// <summary>
    ///     Quotes one argument: backslashes are literal unless they precede a quote, in which case they are doubled
    ///     and the quote is escaped; trailing backslashes are doubled before the closing quote.
    ///     Approach ported from SignRelay (MIT, same author).
    /// </summary>
    internal static void AppendArgument(StringBuilder result, string argument)
    {
        if (argument.Length == 0)
        {
            result.Append("\"\"");
            return;
        }

        if (argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            result.Append(argument);
            return;
        }

        result.Append('"');
        int index = 0;
        while (index < argument.Length)
        {
            int backslashes = 0;
            while (index < argument.Length && argument[index] == '\\')
            {
                index++;
                backslashes++;
            }

            if (index == argument.Length)
            {
                result.Append('\\', backslashes * 2);
                break;
            }

            if (argument[index] == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
            }
            else
            {
                result.Append('\\', backslashes);
                result.Append(argument[index]);
            }

            index++;
        }

        result.Append('"');
    }
}
