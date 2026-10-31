namespace Vestigo.Build;

/// <summary>
/// Pinned nefcon release bundled next to Vestigo.exe under <c>tools\</c>. Bump all values together
/// (see docs/release.md); the build refuses an archive whose SHA-256 differs or an executable
/// without a valid Authenticode signature from <see cref="UpdateFeed.PublisherSubject" />.
/// </summary>
public static class NefconPin
{
    public const string Version = "1.21.0";
    public const string ArchiveSha256 = "4c8e68f6b663598d77b7c3f76459b16fad00994e53ea8af0bd106a805dd5c18b";
    public const string ArchiveUrl = "https://github.com/nefarius/nefcon/releases/download/v1.21.0/nefcon_v1.21.0.zip";

    /// <summary>Entry inside the release archive (forward slashes, case-insensitive match).</summary>
    public const string ArchiveEntryPath = "x64/nefconc.exe";

    public const string Executable = "nefconc.exe";
    public const string LicenseFileName = "nefcon-LICENSE.txt";
    public const string StageDirectoryName = "tools";
}
