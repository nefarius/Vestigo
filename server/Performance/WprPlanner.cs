namespace Vestigo.Performance;

/// <summary>Allow-listed WPR profiles and fixed argument lists. Callers never pass free-form arguments.</summary>
public static class WprPlanner
{
    public const string InstancePrefix = "Vestigo-Wpr-";

    public static readonly IReadOnlyList<string> AllowedProfiles =
    [
        "GeneralProfile",
        "CPU",
        "DiskIO",
        "FileIO",
        "Minifilter",
        "Network",
        "Heap"
    ];

    public static string? ValidateProfile(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return "A WPR profile is required.";
        }

        return AllowedProfiles.Contains(profile.Trim(), StringComparer.OrdinalIgnoreCase)
            ? null
            : $"Unknown WPR profile '{profile}'. Allowed: {string.Join(", ", AllowedProfiles)}.";
    }

    public static string NewInstanceName() => InstancePrefix + Guid.NewGuid().ToString("N")[..12];

    public static bool IsOwnedInstance(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || !name.StartsWith(InstancePrefix, StringComparison.Ordinal)
            || name.Length <= InstancePrefix.Length)
        {
            return false;
        }

        ReadOnlySpan<char> rest = name.AsSpan(InstancePrefix.Length);
        if (rest.Length is < 8 or > 32)
        {
            return false;
        }

        foreach (char c in rest)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    public static IReadOnlyList<string> StartArguments(string profile, string instance) =>
        ["-start", Canonical(profile), "-instancename", instance];

    public static IReadOnlyList<string> StopArguments(string output, string instance) =>
        ["-stop", output, "-instancename", instance];

    public static IReadOnlyList<string> CancelArguments(string instance) =>
        ["-cancel", "-instancename", instance];

    public static string Canonical(string profile) =>
        AllowedProfiles.First(item => string.Equals(item, profile.Trim(), StringComparison.OrdinalIgnoreCase));
}
