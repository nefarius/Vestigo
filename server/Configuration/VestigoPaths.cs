namespace Vestigo.Configuration;

/// <summary>
///     Machine-wide locations for mutable Vestigo state. These stay outside the install directory.
/// </summary>
public static class VestigoPaths
{
    public const string CompanyFolderName = "Nefarius Software Solutions";
    public const string ProductFolderName = "Vestigo";

    /// <summary>True when <c>VESTIGO_SETTINGS_DIR</c> relocates the state tree (tests and development).</summary>
    public static bool IsSettingsDirectoryOverridden =>
        Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR") is string overrideDirectory &&
        !string.IsNullOrWhiteSpace(overrideDirectory);

    public static string SettingsDirectory =>
        IsSettingsDirectoryOverridden
            ? Path.GetFullPath(Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR")!)
            : Path.Combine(SettingsRoot, CompanyFolderName, ProductFolderName);

    public static string SettingsFilePath => Path.Combine(SettingsDirectory, "appsettings.json");

    public static string SpoolDirectory => Path.Combine(SettingsDirectory, "spool");

    public static string LogDirectory => Path.Combine(SettingsDirectory, "logs");

    public static string OwnedSessionsPath => Path.Combine(SettingsDirectory, "owned-sessions.json");

    public static string OwnedDebugViewPath => Path.Combine(SettingsDirectory, "owned-debugview.json");

    public static string DefaultSymbolCache => Path.Combine(SettingsDirectory, "symcache");

    /// <summary>Default folder for ETL, PDB, TMF, and conversion-output paths when no trace roots are configured.</summary>
    public static string TraceDirectory => Path.Combine(SettingsDirectory, "traces");

    private static string SettingsRoot =>
        OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}
