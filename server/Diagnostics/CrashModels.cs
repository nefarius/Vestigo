using Vestigo.Jobs;

namespace Vestigo.Diagnostics;

/// <summary>One Windows Error Reporting report. Metadata only: dump bytes are never included.</summary>
public sealed record CrashReport(
    string Scope,
    string Store,
    string? Profile,
    string Folder,
    string? EventType,
    DateTimeOffset? EventTime,
    string? AppName,
    string? AppPath,
    string? ReportId,
    bool ReportTruncated,
    IReadOnlyList<CrashSignature> Signatures,
    IReadOnlyList<CrashDumpFile> DumpFiles);

/// <summary>One signature name and value copied from <c>Report.wer</c>.</summary>
public sealed record CrashSignature(string Name, string Value);

/// <summary>A dump file that sits beside a report. Only the file name and length are returned.</summary>
public sealed record CrashDumpFile(string Name, long Bytes);

/// <summary>A page of crash-report metadata.</summary>
public sealed record CrashReportList(int Count, int Returned, bool Truncated, bool Partial, int Skipped, IReadOnlyList<CrashReport> Reports);

/// <summary>Reads the fixed WER ReportArchive and ReportQueue directories.</summary>
public interface ICrashReportSource
{
    OperationResult<CrashReportList> List(string scope, int limit, DateTimeOffset? since);
}

/// <summary>A process that might be dumped. <see cref="Protected" /> means the dump must be refused.</summary>
public sealed record ProcessDumpTarget(int Pid, string Name, DateTimeOffset? StartTime, bool Protected);

/// <summary>Arguments for one dump write. <see cref="RootPath" /> is the Write root the file must stay inside.</summary>
public sealed record ProcessDumpRequest(
    int Pid,
    DateTimeOffset ExpectedStart,
    string FullPath,
    string DumpType,
    long MaxBytes,
    string RootPath);

/// <summary>Writes a minidump. Tests replace it. The implementation never uploads the file.</summary>
public interface IProcessDumpWriter
{
    OperationResult<ProcessDumpTarget> Inspect(int pid);

    Task<OperationResult<long>> WriteAsync(ProcessDumpRequest request, CancellationToken cancellationToken);
}

/// <summary>Free bytes on the volume that will hold a dump.</summary>
public interface IVolumeSpace
{
    long? FreeBytes(string directory);
}

/// <summary>A started dump job. The output tail is status text, never dump bytes.</summary>
public sealed record ProcessDumpResult(JobInfo Job, string Output, string DumpType, long? Bytes, string? OutputTail, bool StillRunning);
