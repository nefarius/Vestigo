namespace Vestigo.DebugOutput;

/// <summary>Settings for Win32 debug-output capture, bound from <c>Vestigo:DebugOutput</c>.</summary>
public sealed class DebugOutputOptions
{
    public const string SectionName = "DebugOutput";

    /// <summary>SHA-256 of the pinned DebugView++ 1.30.0.0 <c>DebugViewConsole.exe</c> release asset.</summary>
    public const string DefaultExpectedSha256 = "79b01acd3aaa630e17ff4419fe9be8c66f5e8f758328c78af7346a594106fd60";

    /// <summary>
    ///     Absolute path of <c>DebugViewConsole.exe</c>. Empty uses <c>tools\DebugViewConsole.exe</c> next to Vestigo.exe.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    ///     Lowercase SHA-256 of the executable that may be launched. The default is the pinned release asset.
    ///     Empty skips the check, which is intended for an administrator-overridden path.
    /// </summary>
    public string? ExpectedSha256 { get; set; } = DefaultExpectedSha256;

    /// <summary>
    ///     DebugViewConsole always attempts a Global <c>DBWIN_BUFFER</c> reader when the session is not session 0.
    ///     In session 0 (the Windows service) the local and global buffers are the same mapping.
    /// </summary>
    public bool CaptureGlobal { get; set; } = true;

    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromHours(1);

    public int MaxMessages { get; set; } = 1_000_000;

    public long MaxOutputBytes { get; set; } = 512L * 1024 * 1024;

    public int MaxFilterPatterns { get; set; } = 16;

    public int MaxPatternLength { get; set; } = 256;

    public void Validate()
    {
        if (!string.IsNullOrWhiteSpace(ExecutablePath) && !System.IO.Path.IsPathFullyQualified(ExecutablePath))
        {
            throw new InvalidOperationException("Vestigo:DebugOutput:ExecutablePath must be an absolute path.");
        }

        if (!string.IsNullOrWhiteSpace(ExpectedSha256) &&
            (ExpectedSha256.Length != 64 || ExpectedSha256.Any(static c => !Uri.IsHexDigit(c))))
        {
            throw new InvalidOperationException(
                "Vestigo:DebugOutput:ExpectedSha256 must be empty or a 64-character hexadecimal SHA-256 digest.");
        }

        if (MaxDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Vestigo:DebugOutput:MaxDuration must be positive.");
        }

        if (MaxMessages < 1 || MaxOutputBytes < 1 || MaxFilterPatterns < 1 || MaxPatternLength < 1)
        {
            throw new InvalidOperationException("Vestigo:DebugOutput size and count limits must be positive.");
        }
    }
}
