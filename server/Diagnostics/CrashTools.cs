using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Jobs;
using Vestigo.Security;

namespace Vestigo.Diagnostics;

/// <summary>WER report metadata and a gated minidump job. Dump bytes are never returned or uploaded.</summary>
[McpServerToolType]
public sealed class CrashTools(
    ICrashReportSource reports,
    IProcessDumpWriter dumps,
    IVolumeSpace volumes,
    RootPolicy roots,
    JobManager jobs,
    VestigoOptions options,
    ILogger<CrashTools> logger)
{
    private const int TailBytes = 16 * 1024;

    [McpServerTool(Name = "list_crash_reports")]
    [Description(
        "Lists Windows Error Reporting ReportArchive and ReportQueue metadata from the fixed machine and user stores. "
        + "Each row has the store, profile label, folder name, event type, event time, application name and path, report id, and signature values. "
        + "Dump files beside a report are named with their length only. Dump bytes and raw Report.wer text are not returned. "
        + "scope is machine, user, or all. since drops reports whose event time is missing or earlier. "
        + "This is a metadata snapshot and does not explain the crash.")]
    public OperationResult<CrashReportList> ListCrashReports(
        [Description("machine, user, or all.")] string scope = "all",
        [Description("Drop reports earlier than this time, and reports with no event time.")] DateTimeOffset? since = null,
        [Description("Maximum reports. Clamped to 1..Vestigo:Diagnostics:MaxCrashReports.")] int limit = 50)
    {
        if (!TryScope(scope, out string parsed))
        {
            return OperationResult<CrashReportList>.Fail("invalid_request", "scope must be machine, user, or all.");
        }

        int bounded = Math.Clamp(limit, 1, options.Diagnostics.MaxCrashReports);
        OperationResult<CrashReportList> result = reports.List(parsed, bounded, since);
        logger.LogInformation(
            "list_crash_reports success {Success} code {Code} returned {Returned}",
            result.Success,
            result.ErrorCode,
            result.Value?.Returned);
        return result;
    }

    [McpServerTool(Name = "capture_process_dump")]
    [Description(
        "Writes a minidump of one process as a job. Requires Vestigo:Diagnostics:AllowProcessDumps. "
        + "output is a new .dmp file in a root with Write access. expectedStartTime, from list_processes, is required and is checked again immediately before the write. "
        + "dumpType mini writes a small snapshot. dumpType full writes process memory and can contain credentials that were in the process. "
        + "The dump runs as the Vestigo service. Protected processes and a fixed refusal list are rejected. "
        + "The output volume must keep Vestigo:Diagnostics:MinDumpFreeBytes free after reserving Vestigo:Diagnostics:MaxDumpBytes. "
        + "A dump over that cap is deleted. Vestigo does not upload the file and does not create a download ticket. "
        + "wait > 0 waits that many seconds. The job output is a status line, never dump bytes.")]
    public async Task<OperationResult<ProcessDumpResult>> CaptureProcessDump(
        [Description("Process id.")] int pid,
        [Description("New .dmp path in a root with Write access.")] string output,
        [Description("Start time from list_processes. A different process fails with process_identity_mismatch.")] DateTimeOffset expectedStartTime,
        [Description("mini or full. full includes process memory.")] string dumpType = "mini",
        [Description("Seconds to wait (0 to 300) before returning. 0 returns the job immediately.")] int wait = 0,
        CancellationToken cancellationToken = default)
    {
        if (!options.Diagnostics.AllowProcessDumps)
        {
            return Fail("process_dumps_disabled", "Process dumps are disabled. Set Vestigo:Diagnostics:AllowProcessDumps to true.");
        }

        if (pid <= 0)
        {
            return Fail("invalid_request", "pid must be a positive process id.");
        }

        if (!TryDumpType(dumpType, out string parsedType))
        {
            return Fail("invalid_request", "dumpType must be mini or full.");
        }

        if (string.IsNullOrWhiteSpace(output) || !output.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("invalid_request", "output must be a new .dmp file in a root with Write access.");
        }

        PathCheck located = roots.Resolve(output, RootAccess.Write);
        if (!located.Success)
        {
            return Fail(located.ErrorCode!, located.Error!);
        }

        string full = located.FullPath!;
        string? directory = Path.GetDirectoryName(full);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Fail("invalid_path", "The output directory does not exist.");
        }

        if (File.Exists(full))
        {
            return Fail("already_exists", "The output file already exists.");
        }

        OperationResult<ProcessDumpTarget> inspected = dumps.Inspect(pid);
        if (!inspected.Success)
        {
            return Fail(inspected.ErrorCode!, inspected.Error!);
        }

        ProcessDumpTarget target = inspected.Value!;
        if (target.Protected)
        {
            return Fail("protected_process", "This process is protected or on the refusal list.");
        }

        if (target.StartTime is null || !WindowsProcessModuleSource.StartUnchanged(expectedStartTime, target.StartTime))
        {
            return Fail("process_identity_mismatch", "The process start time does not match expectedStartTime. The pid may have been reused.");
        }

        string? space = SpaceProblem(directory);
        if (space is not null)
        {
            return Fail(space, space == "disk_space_unknown"
                ? "Free space on the output volume could not be read."
                : "The output volume does not have enough free space for the dump and the configured reserve.");
        }

        DumpOutcome outcome = new();
        ProcessDumpRequest request = new(pid, expectedStartTime, full, parsedType, options.Diagnostics.MaxDumpBytes);
        OperationResult<JobInfo> started = jobs.Start(
            "process-dump",
            $"{parsedType} pid {pid}",
            (job, token) => RunAsync(request, directory, job, outcome, token));
        if (!started.Success)
        {
            return Fail(started.ErrorCode!, started.Error!);
        }

        logger.LogInformation("capture_process_dump pid {Pid} type {Type} job {JobId}", pid, parsedType, started.Value!.Id);
        int seconds = Math.Clamp(wait, 0, 300);
        if (seconds == 0)
        {
            return OperationResult<ProcessDumpResult>.Ok(new ProcessDumpResult(started.Value!, output, parsedType, null, null, true));
        }

        JobInfo info = (await jobs.WaitAsync(started.Value!.Id, TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false)).Value!;
        long offset = Math.Max(0, info.OutputBytes - TailBytes);
        OperationResult<JobOutputPage> page = jobs.ReadOutput(info.Id, offset, TailBytes);
        return OperationResult<ProcessDumpResult>.Ok(new ProcessDumpResult(
            info,
            output,
            parsedType,
            info.State == JobState.Succeeded ? outcome.Bytes : null,
            page.Value?.Text,
            info.State == JobState.Running));
    }

    private async Task<int> RunAsync(
        ProcessDumpRequest request,
        string directory,
        JobContext output,
        DumpOutcome outcome,
        CancellationToken cancellationToken)
    {
        string? space = SpaceProblem(directory);
        if (space is not null)
        {
            output.WriteLine(space);
            return 1;
        }

        OperationResult<long> written = await dumps.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!written.Success)
        {
            output.WriteLine(written.ErrorCode!);
            if (written.Error?.Contains(MiniDumpWriter.PartialFileRemains, StringComparison.Ordinal) == true)
            {
                output.WriteLine("cleanup_failed");
            }

            return 1;
        }

        outcome.Bytes = written.Value;
        output.WriteLine("bytes " + written.Value);
        return 0;
    }

    private string? SpaceProblem(string directory)
    {
        long? free = volumes.FreeBytes(directory);
        if (free is null)
        {
            return "disk_space_unknown";
        }

        long reserve = options.Diagnostics.MinDumpFreeBytes;
        long cap = options.Diagnostics.MaxDumpBytes;
        return free.Value >= reserve && free.Value - reserve >= cap ? null : "insufficient_disk_space";
    }

    private static bool TryScope(string scope, out string parsed)
    {
        parsed = scope.Trim().ToLowerInvariant();
        return parsed is "machine" or "user" or "all";
    }

    private static bool TryDumpType(string dumpType, out string parsed)
    {
        parsed = dumpType.Trim().ToLowerInvariant();
        return parsed is "mini" or "full";
    }

    private static OperationResult<ProcessDumpResult> Fail(string code, string error) =>
        OperationResult<ProcessDumpResult>.Fail(code, error);

    private sealed class DumpOutcome
    {
        public long? Bytes { get; set; }
    }
}
