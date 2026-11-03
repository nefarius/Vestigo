namespace Vestigo.Diagnostics;

/// <summary>Processes that <c>capture_process_dump</c> refuses by name, in addition to protected-process checks.</summary>
internal static class ProcessDumpPolicy
{
    private static readonly HashSet<string> Refused = new(StringComparer.OrdinalIgnoreCase)
    {
        "system",
        "registry",
        "idle",
        "smss",
        "csrss",
        "wininit",
        "winlogon",
        "services",
        "lsass",
        "lsaiso",
        "secure system",
        "memory compression"
    };

    public static bool IsRefused(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string trimmed = name.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        return Refused.Contains(trimmed);
    }
}
