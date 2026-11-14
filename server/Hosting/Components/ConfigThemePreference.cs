namespace Vestigo.Hosting.Components;

/// <summary>Browser-saved light or dark choice for the configuration page.</summary>
internal static class ConfigThemePreference
{
    internal const string StorageKey = "vestigo-config-theme";

    internal static bool? Parse(string? stored) => stored switch
    {
        "dark" => true,
        "light" => false,
        _ => null
    };

    internal static string Format(bool dark) => dark ? "dark" : "light";
}
