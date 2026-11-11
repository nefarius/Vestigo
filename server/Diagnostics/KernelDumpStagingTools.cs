using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Files;
using Vestigo.Jobs;
using Vestigo.Security;

namespace Vestigo.Diagnostics;

/// <summary>Copies one inventory-selected kernel dump into a write root and issues a one-time download ticket.</summary>
[McpServerToolType]
public sealed class KernelDumpStagingTools(
    IKernelDumpInventory inventory,
    IVolumeSpace volumes,
    RootPolicy roots,
    JobManager jobs,
    VestigoOptions options,
    ILogger<KernelDumpStagingTools> logger,
    FileLinkIssuer? links = null)
{
    private const int TailBytes = 16 * 1024;

    [McpServerTool(Name = "stage_kernel_dump")]
    [Description(
        "Copies one kernel dump selected by the id from list_kernel_dumps into a new .dmp file in a root with Write access. "
        + "Requires Vestigo:Diagnostics:AllowKernelDumpStaging. The caller does not supply the source path. "
        + "The source must be no larger than Vestigo:Diagnostics:MaxDumpBytes, and the output volume must keep "
        + "Vestigo:Diagnostics:MinDumpFreeBytes free after the copy. "
        + "The job output is the byte count and SHA-256, never dump bytes. "
        + "When the job has finished, the result includes a one-time download ticket from the existing file transfer path when the output root also has Read access. "
        + "wait > 0 waits that many seconds.")]
    public async Task<OperationResult<KernelDumpStageResult>> StageKernelDump(
        [Description("memory, dedicated, or minidump/<file>.dmp from list_kernel_dumps.")] string id,
        [Description("New .dmp path in a root with Write access.")] string output,
        [Description("Seconds to wait (0 to 300) before returning. 0 returns the job immediately.")] int wait = 0,
        CancellationToken cancellationToken = default)
    {
        if (!options.Diagnostics.AllowKernelDumpStaging)
        {
            return Fail("kernel_dump_staging_disabled", "Kernel dump staging is disabled. Set Vestigo:Diagnostics:AllowKernelDumpStaging to true.");
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

        if (!WindowsKernelDumpInventory.IsInventoryId(id))
        {
            return Fail("invalid_request", "id must be memory, dedicated, or minidump/<file>.dmp from list_kernel_dumps.");
        }

        OperationResult<KernelDumpLocation> source = inventory.Resolve(id);
        if (!source.Success)
        {
            return Fail(source.ErrorCode!, source.Error!);
        }

        KernelDumpLocation dump = source.Value!;
        if (dump.Bytes > options.Diagnostics.MaxDumpBytes)
        {
            return Fail("dump_too_large", "The dump is larger than Vestigo:Diagnostics:MaxDumpBytes.");
        }

        string? space = SpaceProblem(directory, dump.Bytes);
        if (space is not null)
        {
            return Fail(space, space == "disk_space_unknown"
                ? "Free space on the output volume could not be read."
                : "The output volume does not have enough free space for the dump and the configured reserve.");
        }

        StageOutcome outcome = new();
        OperationResult<JobInfo> started = jobs.Start(
            "kernel-dump",
            id,
            (job, token) => RunAsync(dump, full, directory, job, outcome, token));
        if (!started.Success)
        {
            return Fail(started.ErrorCode!, started.Error!);
        }

        logger.LogInformation("stage_kernel_dump id {Id} job {JobId}", dump.Id, started.Value!.Id);
        int seconds = Math.Clamp(wait, 0, 300);
        if (seconds == 0)
        {
            return OperationResult<KernelDumpStageResult>.Ok(new KernelDumpStageResult(started.Value!, dump.Id, output, null, null, null, null, true));
        }

        JobInfo info = (await jobs.WaitAsync(started.Value!.Id, TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false)).Value!;
        long offset = Math.Max(0, info.OutputBytes - TailBytes);
        OperationResult<JobOutputPage> page = jobs.ReadOutput(info.Id, offset, TailBytes);
        FileLink? download = info.State == JobState.Succeeded ? links?.TryCreateDownload(full) : null;
        return OperationResult<KernelDumpStageResult>.Ok(new KernelDumpStageResult(
            info,
            dump.Id,
            output,
            info.State == JobState.Succeeded ? outcome.Bytes : null,
            info.State == JobState.Succeeded ? outcome.Sha256 : null,
            download,
            page.Value?.Text,
            info.State == JobState.Running));
    }

    private async Task<int> RunAsync(
        KernelDumpLocation dump,
        string destination,
        string directory,
        JobContext output,
        StageOutcome outcome,
        CancellationToken cancellationToken)
    {
        string? space = SpaceProblem(directory, dump.Bytes);
        if (space is not null)
        {
            output.WriteLine(space);
            return 1;
        }

        OperationResult<KernelDumpCopied> copied = await KernelDumpCopy.CopyAsync(
            dump.Path,
            destination,
            dump.Bytes,
            options.Diagnostics.MaxDumpBytes,
            cancellationToken).ConfigureAwait(false);
        if (!copied.Success)
        {
            output.WriteLine(copied.ErrorCode!);
            if (copied.Error?.Contains("cleanup_failed", StringComparison.Ordinal) == true)
            {
                output.WriteLine("cleanup_failed");
            }

            return 1;
        }

        outcome.Bytes = copied.Value!.Bytes;
        outcome.Sha256 = copied.Value.Sha256;
        output.WriteLine("bytes " + copied.Value.Bytes);
        output.WriteLine("sha256 " + copied.Value.Sha256);
        return 0;
    }

    private string? SpaceProblem(string directory, long bytes)
    {
        long? free = volumes.FreeBytes(directory);
        if (free is null)
        {
            return "disk_space_unknown";
        }

        long reserve = options.Diagnostics.MinDumpFreeBytes;
        return free.Value >= bytes && free.Value - bytes >= reserve ? null : "insufficient_disk_space";
    }

    private static OperationResult<KernelDumpStageResult> Fail(string code, string error) =>
        OperationResult<KernelDumpStageResult>.Fail(code, error);

    private sealed class StageOutcome
    {
        public long? Bytes { get; set; }

        public string? Sha256 { get; set; }
    }
}

/// <summary>Job status for a staged kernel dump. Dump bytes are not included.</summary>
public sealed record KernelDumpStageResult(
    JobInfo Job,
    string Id,
    string Output,
    long? Bytes,
    string? Sha256,
    FileLink? Download,
    string? OutputTail,
    bool Running);
