namespace Vestigo.Updates;

/// <summary>The locally installed Vestigo version and where it was read from.</summary>
/// <param name="Version">Numeric product version, for example <c>1.2.3.4</c>.</param>
/// <param name="Source"><c>registry</c>, <c>assembly</c>, or <c>unknown</c>.</param>
public sealed record InstalledVersion(string Version, string Source);

/// <summary>The newest enabled release from the signed Buildbot feed.</summary>
public sealed record UpdateFeedRelease(
    string Version,
    long ManifestVersion,
    DateTimeOffset PublishedAt,
    string Summary,
    string DownloadUrl,
    long? DownloadSize);

/// <summary>Persisted fields of the last <c>start_self_update</c> launch.</summary>
public sealed record SelfUpdateRecord(
    DateTimeOffset StartedAt,
    string FromVersion,
    string TargetVersion,
    string LogPath,
    int? ExitCode);

/// <summary>Last attempt plus the inferred outcome and log tail.</summary>
public sealed record SelfUpdateAttempt(
    DateTimeOffset StartedAt,
    string FromVersion,
    string TargetVersion,
    string LogPath,
    int? ExitCode,
    string State,
    bool? Succeeded,
    string? LogTail);

/// <summary>Result of <c>check_for_updates</c>.</summary>
public sealed record UpdateCheck(
    InstalledVersion Installed,
    UpdateFeedRelease? Latest,
    bool UpdateAvailable,
    bool UpdaterInstalled,
    bool SelfUpdateAllowed,
    SelfUpdateAttempt? LastAttempt);

/// <summary>Result of <c>start_self_update</c>.</summary>
public sealed record SelfUpdateResult(
    bool DryRun,
    string State,
    string FromVersion,
    string TargetVersion,
    int? ExitCode,
    string? LogPath,
    string? Note,
    string? LogTail);
