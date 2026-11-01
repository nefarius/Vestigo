namespace Vestigo.Build;

/// <summary>
///     Pinned DebugView++ release bundled next to Vestigo.exe under <c>tools\</c>. Bump all values together
///     (see docs/release.md). The build refuses an asset whose SHA-256 differs. The executable is not
///     Authenticode-signed by Nefarius, so the publisher check used for nefcon does not apply.
/// </summary>
public static class DebugViewPin
{
    public const string Version = "1.30.0.0";
    public const string Sha256 = "79b01acd3aaa630e17ff4419fe9be8c66f5e8f758328c78af7346a594106fd60";
    public const string AssetUrl =
        "https://github.com/CobaltFusion/DebugViewPP/releases/download/1.30.0.0/DebugViewConsole.exe";

    public const string Executable = "DebugViewConsole.exe";
    public const string LicenseFileName = "DebugViewPP-LICENSE.txt";
    public const string StageDirectoryName = "tools";
}
