namespace Vestigo.Diagnostics;

/// <summary>Header fields shared by the 32-bit and 64-bit kernel dump prefixes. Bugcheck data is not included.</summary>
public sealed record KernelDumpHeader(
    string? Signature,
    string? ValidDump,
    int? MajorVersion,
    int? MinorVersion,
    int? MachineImageType,
    string? MachineImageTypeName,
    int? NumberOfProcessors,
    string? Note);

/// <summary>A WER report whose dump file name matches this kernel dump.</summary>
public sealed record KernelDumpWerMatch(string Store, string Folder, string? ReportId);

/// <summary>One discovered kernel dump. Bytes of the dump are not included.</summary>
public sealed record KernelDump(
    string Id,
    string Location,
    string FileName,
    long? Bytes,
    DateTimeOffset? LastWriteTimeUtc,
    string FileKind,
    KernelDumpHeader? Header,
    IReadOnlyList<KernelDumpWerMatch> WerMatches);

/// <summary>Configured crash-dump mode and the dumps that were found.</summary>
public sealed record KernelDumpList(
    int? CrashDumpEnabled,
    string? CrashDumpEnabledName,
    string? CrashDumpNote,
    string? WerNote,
    int Returned,
    bool Truncated,
    IReadOnlyList<KernelDump> Dumps);

/// <summary>Finds the fixed kernel-dump locations and reads only the proven header prefix.</summary>
public interface IKernelDumpInventory
{
    OperationResult<KernelDumpList> List(int limit);
}
