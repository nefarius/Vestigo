namespace Vestigo.Configuration;

/// <summary>
///     Machine-wide locations for mutable Vestigo state. These stay outside the install directory.
/// </summary>
public static class VestigoPaths
{
    public const string CompanyFolderName = "Nefarius Software Solutions";
    public const string ProductFolderName = "Vestigo";

    public static string SettingsDirectory =>
        Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR") is string overrideDirectory &&
        !string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.GetFullPath(overrideDirectory)
            : Path.Combine(SettingsRoot, CompanyFolderName, ProductFolderName);

    public static string SettingsFilePath => Path.Combine(SettingsDirectory, "appsettings.json");

    public static string SpoolDirectory => Path.Combine(SettingsDirectory, "spool");

    public static string LogDirectory => Path.Combine(SettingsDirectory, "logs");

    public static string OwnedSessionsPath => Path.Combine(SettingsDirectory, "owned-sessions.json");

    public static string DefaultSymbolCache => Path.Combine(SettingsDirectory, "symcache");

    private static string SettingsRoot =>
        OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}
