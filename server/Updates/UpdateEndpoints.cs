namespace Vestigo.Updates;

/// <summary>
///     Buildbot feed addresses and the packaged updater names. Kept in the server so a check does not
///     depend on the build project. Tests assert these match <c>Vestigo.Build.UpdateFeed</c>.
/// </summary>
public static class UpdateEndpoints
{
    public const string DirectoryUrl = "https://buildbot.nefarius.at/builds/Vestigo/latest/";
    public const string ManifestFileName = "updates.json";
    public const string ManifestSignatureFileName = ManifestFileName + ".minisig";
    public const string ManifestUrl = DirectoryUrl + ManifestFileName;
    public const string ManifestSignatureUrl = DirectoryUrl + ManifestSignatureFileName;
    public const string ManifestPublicKey = "RWTZDJM+Ify8UukJiWfvLcLvjvrVlHcXY/A9cgbOIStr+c19FIlY8x4B";
    public const string RegistryKey = @"SOFTWARE\Nefarius Software Solutions\Vestigo";
    public const string RegistryValue = "Version";
    public const string UpdaterExecutable = "Nefarius_Vestigo_Updater.exe";
    public const string UpdaterConfiguration = "Nefarius_Vestigo_Updater.json";
    public const int MaxManifestBytes = 256 * 1024;
}
