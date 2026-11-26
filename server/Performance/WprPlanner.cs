namespace Vestigo.Performance;

/// <summary>Allow-listed WPR profiles and fixed argument lists. Callers never pass free-form arguments.</summary>
public static class WprPlanner
{
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

    public static IReadOnlyList<string> StartArguments(string profile) => ["-start", Canonical(profile), "-filemode"];

    public static IReadOnlyList<string> StopArguments(string output) => ["-stop", output];

    public static IReadOnlyList<string> CancelArguments() => ["-cancel"];

    public static string Canonical(string profile) =>
        AllowedProfiles.First(item => string.Equals(item, profile.Trim(), StringComparison.OrdinalIgnoreCase));
}
