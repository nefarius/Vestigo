namespace Vestigo.Configuration;

/// <summary>Settings for the Environment module (<c>Vestigo:Environment</c>).</summary>
public sealed class EnvironmentOptions
{
    /// <summary>Variable names (case-insensitive) that the tools refuse to set or unset in any scope.</summary>
    public List<string> ProtectedNames { get; set; } = ["COMSPEC", "SystemRoot", "windir"];

    public void Validate()
    {
        if (ProtectedNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Vestigo:Environment:ProtectedNames cannot contain blank names.");
        }
    }
}

/// <summary>Settings for the Logs module (<c>Vestigo:Logs</c>).</summary>
public sealed class LogsOptions
{
    /// <summary>Upper bound for lines returned by one tail or read call.</summary>
    public int MaxTailLines { get; set; } = 2000;

    /// <summary>Upper bound for bytes examined by one tail or read call.</summary>
    public int MaxTailBytes { get; set; } = 1024 * 1024;

    /// <summary>SHA-256 of <c>rg.exe</c> from the pinned ripgrep 15.2.0 release.</summary>
    public const string DefaultRipgrepSha256 = "14231169855ec5205cf5a1b6f1db358ff4aed4247c86b69ce8aae647c77f6680";

    /// <summary>Absolute path of <c>rg.exe</c>. Empty uses <c>tools\rg.exe</c> next to Vestigo.exe.</summary>
    public string? RipgrepPath { get; set; }

    /// <summary>
    ///     Lowercase SHA-256 of the ripgrep executable that may be launched. The default is the pinned release.
    ///     Empty skips the check, which is intended for an administrator-overridden path.
    /// </summary>
    public string? RipgrepSha256 { get; set; } = DefaultRipgrepSha256;

    /// <summary>
    ///     Upper bound for lines (matches plus context) returned by one search_text call. ripgrep is stopped once reached.
    /// </summary>
    public int MaxSearchResults { get; set; } = 1000;

    /// <summary>Upper bound for context lines requested before or after each match.</summary>
    public int MaxSearchContextLines { get; set; } = 10;

    /// <summary>Longest line text returned per result; longer lines are cut to a window around the first match.</summary>
    public int MaxSearchLineLength { get; set; } = 1000;

    /// <summary>Longest time one search may run before ripgrep is stopped and partial results are returned.</summary>
    public TimeSpan MaxSearchDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Searches that may run at once. Further calls wait for a slot within their own time limit.</summary>
    public int MaxConcurrentSearches { get; set; } = 2;

    public void Validate()
    {
        if (MaxTailLines < 1 || MaxTailBytes < 16)
        {
            throw new InvalidOperationException("Vestigo:Logs limits must be positive (MaxTailBytes at least 16).");
        }

        if (!string.IsNullOrWhiteSpace(RipgrepPath) && !Path.IsPathFullyQualified(RipgrepPath))
        {
            throw new InvalidOperationException("Vestigo:Logs:RipgrepPath must be an absolute path.");
        }

        if (!string.IsNullOrWhiteSpace(RipgrepSha256) &&
            (RipgrepSha256.Length != 64 || RipgrepSha256.Any(static c => !Uri.IsHexDigit(c))))
        {
            throw new InvalidOperationException(
                "Vestigo:Logs:RipgrepSha256 must be empty or a 64-character hexadecimal SHA-256 digest.");
        }

        if (MaxSearchResults < 1 || MaxSearchContextLines < 0 || MaxSearchLineLength < 80 ||
            MaxSearchDuration <= TimeSpan.Zero || MaxConcurrentSearches < 1)
        {
            throw new InvalidOperationException(
                "Vestigo:Logs search limits must be positive (MaxSearchLineLength at least 80, MaxSearchContextLines not negative).");
        }
    }
}

/// <summary>Settings for the EventLog module (<c>Vestigo:EventLog</c>).</summary>
public sealed class EventLogOptions
{
    /// <summary>Upper bound for events returned by one query.</summary>
    public int MaxEvents { get; set; } = 500;

    /// <summary>Upper bound for events examined by one query, which matters for message text filters.</summary>
    public int MaxScannedEvents { get; set; } = 20_000;

    public void Validate()
    {
        if (MaxEvents < 1 || MaxScannedEvents < MaxEvents)
        {
            throw new InvalidOperationException(
                "Vestigo:EventLog:MaxEvents must be positive and MaxScannedEvents must not be smaller.");
        }
    }
}
