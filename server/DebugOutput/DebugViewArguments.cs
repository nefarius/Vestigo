namespace Vestigo.DebugOutput;

/// <summary>
///     Builds the DebugViewConsole argument list. Callers never pass free-form arguments.
/// </summary>
/// <remarks>
///     DebugViewConsole 1.30.0.0 swaps <c>-t</c> and <c>-q</c> relative to its usage text:
///     <c>-q</c> enables tab-separated output and <c>-t</c> enables the high-precision QPC prefix.
///     The fixed flags are <c>-c -a -f -s -p -n -q</c> so stdout is flushed every 250 ms, tab-separated,
///     and prefixed with system time, PID, and process name. <c>-x</c> is never used: it signals every
///     DebugViewConsole instance on the machine.
/// </remarks>
public static class DebugViewArguments
{
    public static OperationResult<IReadOnlyList<string>> Build(
        DebugOutputOptions options,
        IReadOnlyList<string> includeMessages,
        IReadOnlyList<string> excludeMessages,
        IReadOnlyList<string> includeProcesses,
        IReadOnlyList<string> excludeProcesses)
    {
        int total = includeMessages.Count + excludeMessages.Count + includeProcesses.Count + excludeProcesses.Count;
        if (total > options.MaxFilterPatterns)
        {
            return OperationResult<IReadOnlyList<string>>.Fail(
                "invalid_request",
                $"At most {options.MaxFilterPatterns} capture-time filter patterns are allowed.");
        }

        List<string> arguments = ["-c", "-a", "-f", "-s", "-p", "-n", "-q"];
        if (!TryAdd(arguments, "-i", includeMessages, options, out string? error) ||
            !TryAdd(arguments, "-e", excludeMessages, options, out error) ||
            !TryAdd(arguments, "--include-process", includeProcesses, options, out error) ||
            !TryAdd(arguments, "--exclude-process", excludeProcesses, options, out error))
        {
            return OperationResult<IReadOnlyList<string>>.Fail("invalid_request", error!);
        }

        return OperationResult<IReadOnlyList<string>>.Ok(arguments);
    }

    private static bool TryAdd(
        List<string> arguments,
        string flag,
        IReadOnlyList<string> values,
        DebugOutputOptions options,
        out string? error)
    {
        error = null;
        foreach (string value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-') || value.Any(char.IsControl))
            {
                error = "A filter pattern is empty, starts with '-', or contains control characters.";
                return false;
            }

            if (value.Length > options.MaxPatternLength)
            {
                error = $"A filter pattern exceeds the {options.MaxPatternLength}-character limit.";
                return false;
            }

            arguments.Add(flag);
            arguments.Add(value);
        }

        return true;
    }
}
