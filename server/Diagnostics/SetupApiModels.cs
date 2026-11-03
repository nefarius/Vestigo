namespace Vestigo.Diagnostics;

/// <summary>One SetupAPI dev-log section whose header token matched an instance ID.</summary>
public sealed record InstallSection(
    string Title,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    string? ExitStatus,
    IReadOnlyList<string> NumericCodes,
    IReadOnlyList<string> InfLines,
    IReadOnlyList<string> DriverVersionLines,
    string SourceFile,
    bool Complete,
    IReadOnlyList<string>? Lines,
    bool RawTruncated);

/// <summary>Install sections for one instance ID, oldest first.</summary>
public sealed record InstallHistory(
    string InstanceId,
    bool Truncated,
    IReadOnlyList<InstallSection> Sections);
