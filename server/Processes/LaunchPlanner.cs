namespace Vestigo.Processes;

/// <summary>Executable plus arguments after script-host selection.</summary>
public sealed record LaunchPlan(string Executable, IReadOnlyList<string> Arguments);

/// <summary>
///     Pure helpers that decide how a resolved file is started and that validate client-supplied arguments.
/// </summary>
public static class LaunchPlanner
{
    public const int MaxArguments = 512;

    public const int MaxEnvironmentEntries = 128;

    public static bool IsPowerShellScript(string path) =>
        path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Returns the plan for <paramref name="path" />. <c>.ps1</c> files run through pwsh.exe when
    ///     <paramref name="findPwsh" /> returns a path, otherwise through Windows PowerShell.
    /// </summary>
    public static LaunchPlan Plan(string path, IReadOnlyList<string> arguments, Func<string?> findPwsh, string windowsPowerShellPath)
    {
        if (!IsPowerShellScript(path))
        {
            return new LaunchPlan(path, arguments);
        }

        string host = findPwsh() ?? windowsPowerShellPath;
        List<string> hostArguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path];
        hostArguments.AddRange(arguments);
        return new LaunchPlan(host, hostArguments);
    }

    public static string WindowsPowerShellPath(string systemDirectory) =>
        Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Returns an error message, or null when the request shape is acceptable.</summary>
    public static string? Validate(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string? workingDirectory)
    {
        if (arguments.Count > MaxArguments)
        {
            return $"At most {MaxArguments} arguments are allowed.";
        }

        if (arguments.Any(argument => argument is null || argument.Contains('\0')))
        {
            return "Arguments cannot be null or contain NUL characters.";
        }

        if (environment.Count > MaxEnvironmentEntries)
        {
            return $"At most {MaxEnvironmentEntries} environment overrides are allowed.";
        }

        foreach ((string name, string value) in environment)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('=') || name.Contains('\0') || value.Contains('\0'))
            {
                return $"Environment variable '{name}' has an invalid name or value.";
            }
        }

        if (workingDirectory is not null && workingDirectory.Contains('\0'))
        {
            return "workingDirectory contains a NUL character.";
        }

        return null;
    }

    /// <summary>Returns an error message when the final command line would exceed the CreateProcess limit.</summary>
    public static string? ValidateCommandLine(LaunchPlan plan) =>
        CommandLineBuilder.Build(plan.Executable, plan.Arguments).Length > CommandLineBuilder.MaxLength
            ? "The command line exceeds 32766 characters."
            : null;
}

/// <summary>Refuses kill targets that would take down the host or the OS.</summary>
public static class ProcessGuards
{
    /// <summary>Returns an error message, or null when <paramref name="pid" /> may be killed.</summary>
    public static string? CheckKillTarget(int pid, int ownPid)
    {
        if (pid <= 0)
        {
            return "Process id must be positive.";
        }

        if (pid is 4)
        {
            return "Refusing to touch the System process (pid 4).";
        }

        return pid == ownPid ? "Refusing to touch the Vestigo process itself." : null;
    }
}

/// <summary>Builds and parses Windows environment blocks (UTF-16, NUL separated, double NUL terminated).</summary>
public static class EnvironmentBlock
{
    public static Dictionary<string, string> Parse(ReadOnlySpan<char> block)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        int start = 0;
        while (start < block.Length && block[start] != '\0')
        {
            int end = block[start..].IndexOf('\0');
            if (end < 0)
            {
                break;
            }

            ReadOnlySpan<char> entry = block.Slice(start, end);
            start += end + 1;

            // Entries such as "=C:=C:\dir" start with '=' and are skipped.
            int equals = entry.IndexOf('=');
            if (equals > 0)
            {
                result[entry[..equals].ToString()] = entry[(equals + 1)..].ToString();
            }
        }

        return result;
    }

    /// <summary>Overlays <paramref name="overrides" /> on <paramref name="baseline" /> and renders a sorted block.</summary>
    public static char[] Build(IReadOnlyDictionary<string, string> baseline, IReadOnlyDictionary<string, string> overrides)
    {
        SortedDictionary<string, string> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in baseline)
        {
            merged[name] = value;
        }

        foreach ((string name, string value) in overrides)
        {
            merged[name] = value;
        }

        System.Text.StringBuilder builder = new();
        foreach ((string name, string value) in merged)
        {
            builder.Append(name).Append('=').Append(value).Append('\0');
        }

        builder.Append('\0');
        if (merged.Count == 0)
        {
            builder.Append('\0');
        }

        return builder.ToString().ToCharArray();
    }
}
