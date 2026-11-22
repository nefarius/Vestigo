namespace Vestigo.SystemInfo;

/// <summary>
///     Shared identity for the Vestigo Credential Provider COM DLL, its named pipe, and the reboot-free live path.
///     Keep these values in sync with <c>credential-provider/guid.h</c> and <c>credential-provider/Protocol.h</c>.
/// </summary>
public static class CredentialProviderIdentity
{
    public const string Clsid = "a3f1c8e2-4d7b-4e19-9c6a-8b2f1d5e7a90";

    public const string ClsidBraced = "{A3F1C8E2-4D7B-4E19-9C6A-8B2F1D5E7A90}";

    public const string PipeName = @"Vestigo.CredentialProvider.v1";

    public const string PipePath = @"\\.\pipe\" + PipeName;

    public const ushort ProtocolVersion = 1;

    public const uint ProtocolMagic = 0x314C5456;

    public const string StagingDirectoryName = "credential-provider";

    public const string LiveDirectoryName = "live";

    public const string StagingFileName = "Vestigo.CredentialProvider.dll";

    public const string ClassesClsidKey = @"SOFTWARE\Classes\CLSID\" + ClsidBraced;

    public const string InprocServer32Key = ClassesClsidKey + @"\InprocServer32";

    public const string CredentialProvidersKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\" + ClsidBraced;

    public static string StagingDirectory(string installDir) =>
        Path.Combine(installDir, StagingDirectoryName);

    public static string StagingPath(string installDir) =>
        Path.Combine(StagingDirectory(installDir), StagingFileName);

    public static string LiveDirectory(string installDir) =>
        Path.Combine(StagingDirectory(installDir), LiveDirectoryName);

    public static string LiveFileName(string sha256Hex)
    {
        string prefix = sha256Hex.Length >= 16 ? sha256Hex[..16] : sha256Hex;
        return "Vestigo.CredentialProvider." + prefix.ToLowerInvariant() + ".dll";
    }

    public static string LivePath(string installDir, string sha256Hex) =>
        Path.Combine(LiveDirectory(installDir), LiveFileName(sha256Hex));
}
