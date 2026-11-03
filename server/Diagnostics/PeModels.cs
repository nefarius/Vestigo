namespace Vestigo.Diagnostics;

/// <summary>
///     One PE observation from a single open of the file. Null fields were unavailable.
///     <see cref="PdbName" /> is the CodeView file name only, never a directory.
/// </summary>
public sealed record BinaryInspection(
    string Architecture,
    string Format,
    string? Subsystem,
    IReadOnlyList<string> Characteristics,
    DateTimeOffset? Timestamp,
    bool? Managed,
    string? PdbName,
    string? PdbGuid,
    int? PdbAge,
    string? FileVersion,
    string? ProductVersion,
    long Size,
    string? Sha256,
    DateTimeOffset ObservedAt);

/// <summary>Reads PE metadata from one file handle.</summary>
public interface IPeInspector
{
    OperationResult<BinaryInspection> Inspect(string fullPath, bool computeSha256, long maxBytes);
}
