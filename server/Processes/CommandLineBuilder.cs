using System.Text;

using CliWrap.Builders;

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
            builder.Append(ArgumentsBuilder.Escape(argument));
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
}
