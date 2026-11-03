namespace Vestigo.Diagnostics;

/// <summary>One loaded module before path disclosure is applied.</summary>
public sealed record LoadedModuleRecord(
    string Name,
    ulong? BaseAddress,
    int? Size,
    string? Architecture,
    string? FileVersion,
    string? ProductVersion,
    string? FullPath);

/// <summary>Platform result for one process. Errors do not include file paths.</summary>
public sealed record ProcessModuleQuery(
    bool Success,
    string? ErrorCode,
    string? Error,
    DateTimeOffset? StartTime,
    bool Partial,
    bool Truncated,
    int Skipped,
    IReadOnlyList<LoadedModuleRecord> Modules)
{
    public static ProcessModuleQuery Fail(string code, string error) =>
        new(false, code, error, null, false, false, 0, []);

    public static ProcessModuleQuery Ok(
        DateTimeOffset? startTime,
        bool partial,
        bool truncated,
        int skipped,
        IReadOnlyList<LoadedModuleRecord> modules) =>
        new(true, null, null, startTime, partial, truncated, skipped, modules);
}

/// <summary>Enumerates modules loaded in one process. The snapshot is best-effort.</summary>
public interface IProcessModuleSource
{
    ProcessModuleQuery Query(int pid, int limit);
}

/// <summary>One module returned to a client. <see cref="Path" /> is a root alias or null.</summary>
public sealed record ProcessModuleInfo(
    string Name,
    string? BaseAddress,
    int? Size,
    string? Architecture,
    string? FileVersion,
    string? ProductVersion,
    string? Path,
    bool PathOutsideRoots);

/// <summary>Best-effort module list. It is not an atomic identity with a later file inspection.</summary>
public sealed record ProcessModuleList(
    int Pid,
    DateTimeOffset? StartTime,
    DateTimeOffset ObservedAt,
    bool Partial,
    bool Truncated,
    int Skipped,
    IReadOnlyList<ProcessModuleInfo> Modules);
