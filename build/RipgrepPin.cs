namespace Vestigo.Build;

/// <summary>
///     Pinned ripgrep release bundled next to Vestigo.exe under <c>tools\</c>. Bump all values together
///     (see docs/release.md). The build refuses an archive or an extracted <c>rg.exe</c> whose SHA-256 differs.
///     The executable is not Authenticode-signed, so the publisher check used for nefcon does not apply.
/// </summary>
public static class RipgrepPin
{
    public const string Version = "15.2.0";
    public const string ArchiveSha256 = "71b2fef860abe467217a538ff31de02f5258807c0129f771846f87bd029aafc5";
    public const string ArchiveUrl =
        "https://github.com/BurntSushi/ripgrep/releases/download/15.2.0/ripgrep-15.2.0-x86_64-pc-windows-msvc.zip";

    /// <summary>Entry inside the release archive (forward slashes, case-insensitive match).</summary>
    public const string ArchiveEntryPath = "ripgrep-15.2.0-x86_64-pc-windows-msvc/rg.exe";

    /// <summary>SHA-256 of the extracted <c>rg.exe</c>; Vestigo re-checks it before every launch.</summary>
    public const string ExecutableSha256 = "14231169855ec5205cf5a1b6f1db358ff4aed4247c86b69ce8aae647c77f6680";

    public const string Executable = "rg.exe";
    public const string LicenseFileName = "ripgrep-LICENSE.txt";
    public const string StageDirectoryName = "tools";
}
