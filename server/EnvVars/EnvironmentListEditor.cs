namespace Vestigo.EnvVars;

/// <summary>Edits ';'-separated lists such as PATH. Pure; does not touch the registry.</summary>
public static class EnvironmentListEditor
{
    /// <summary>
    ///     Adds the ';'-separated <paramref name="entries" /> to <paramref name="current" />, skipping entries that are
    ///     already present (case-insensitive, ignoring surrounding quotes and a trailing backslash) and dropping empty segments.
    /// </summary>
    public static string Add(string? current, string entries, bool prepend, out bool changed)
    {
        List<string> existing = Split(current);
        HashSet<string> seen = new(existing.Select(Key), StringComparer.OrdinalIgnoreCase);
        List<string> additions = [];
        foreach (string entry in Split(entries))
        {
            if (seen.Add(Key(entry)))
            {
                additions.Add(entry);
            }
        }

        changed = additions.Count > 0;
        IEnumerable<string> result = prepend ? additions.Concat(existing) : existing.Concat(additions);
        return string.Join(';', result);
    }

    private static List<string> Split(string? value) =>
        (value ?? string.Empty)
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    private static string Key(string entry)
    {
        string trimmed = entry.Trim().Trim('"');
        string withoutSlash = trimmed.TrimEnd('\\', '/');
        return withoutSlash.Length == 0 ? trimmed : withoutSlash;
    }
}
