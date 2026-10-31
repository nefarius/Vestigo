namespace Vestigo.Configuration;

/// <summary>
///     Settings for the <c>RemoteDebugger</c> module, bound from <c>Vestigo:RemoteDebugger</c>.
/// </summary>
public sealed class RemoteDebuggerOptions
{
    public const string SectionName = "RemoteDebugger";

    /// <summary>Port <c>start_remote_debugger</c> uses when the caller gives none. Visual Studio 2022 and 2026 both default to 4026.</summary>
    public int DefaultPort { get; set; } = 4026;

    /// <summary>Upper bound for downloading the installer inside the install job.</summary>
    public TimeSpan InstallerDownloadTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Extra absolute directories probed for <c>msvsmon.exe</c>, either directly or as <c>&lt;dir&gt;\&lt;x64|x86|arm64&gt;\msvsmon.exe</c>.
    ///     A hit counts for a Visual Studio version only when the file's major version matches (17 or 18).
    /// </summary>
    public List<string> ExtraSearchPaths { get; set; } = [];

    public void Validate()
    {
        if (DefaultPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("Vestigo:RemoteDebugger:DefaultPort must be between 1 and 65535.");
        }

        if (InstallerDownloadTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Vestigo:RemoteDebugger:InstallerDownloadTimeout must be positive.");
        }

        if (ExtraSearchPaths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)))
        {
            throw new InvalidOperationException("Vestigo:RemoteDebugger:ExtraSearchPaths entries must be absolute paths.");
        }
    }
}