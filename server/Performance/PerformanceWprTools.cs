using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Files;
using Vestigo.Jobs;
using Vestigo.Security;

namespace Vestigo.Performance;

/// <summary>Gated WPR recording of raw .etl files with optional one-time download tickets.</summary>
[McpServerToolType]
public sealed class PerformanceWprTools(
    IWprRecorder recorder,
    RootPolicy roots,
    JobManager jobs,
    VestigoOptions options,
    ILogger<PerformanceWprTools> logger,
    FileLinkIssuer? links = null)
{
    private const int TailBytes = 16 * 1024;

    [McpServerTool(Name = "record_wpr_trace")]
    [Description(
        "Starts a bounded WPR recording job that writes a .etl file into a root with Write access. "
        + "Requires Vestigo:Performance:AllowRawEtlCapture. Profiles are GeneralProfile, CPU, DiskIO, FileIO, Minifilter, Network, or Heap. "
        + "When the job has finished, the result includes size, SHA-256, and a download link (a one-time ticket when ApiTokens is set) when the output root also has Read access. "
        + "wait > 0 waits that many seconds.")]
    public async Task<OperationResult<WprTraceResult>> RecordWprTrace(
        [Description("New .etl path in a root with Write access.")] string output,
        [Description("Allow-listed WPR profile.")] string profile = "GeneralProfile",
        [Description("Recording window in seconds.")] int durationSeconds = 15,
        [Description("Seconds to wait (0 to 300) before returning. 0 returns the job immediately.")] int wait = 0,
        CancellationToken cancellationToken = default)
    {
        TimeSpan duration = TimeSpan.FromSeconds(durationSeconds);
        OperationResult<string> planned = recorder.PlanStart(profile, output, duration);
        if (!planned.Success)
        {
            return planned.FailAs<WprTraceResult>();
        }

        PathCheck located = roots.Resolve(output, RootAccess.Write);
        if (!located.Success)
        {
            return OperationResult<WprTraceResult>.Fail(located.ErrorCode!, located.Error!);
        }

        string full = located.FullPath!;
        string? directory = Path.GetDirectoryName(full);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return OperationResult<WprTraceResult>.Fail("invalid_path", "The output directory does not exist.");
        }

        if (File.Exists(full))
        {
            return OperationResult<WprTraceResult>.Fail("already_exists", "The output file already exists.");
        }

        StageOutcome outcome = new();
        OperationResult<JobInfo> started = jobs.Start(
            "wpr-trace",
            planned.Value!,
            (job, token) => RunAsync(planned.Value!, full, duration, job, outcome, token));
        if (!started.Success)
        {
            return started.FailAs<WprTraceResult>();
        }

        logger.LogInformation("record_wpr_trace profile {Profile} job {JobId}", planned.Value, started.Value!.Id);
        int seconds = Math.Clamp(wait, 0, 300);
        if (seconds == 0)
        {
            return OperationResult<WprTraceResult>.Ok(new WprTraceResult(started.Value!, output, null, null, null, null, true));
        }

        OperationResult<JobInfo> waited = await jobs.WaitAsync(started.Value!.Id, TimeSpan.FromSeconds(seconds), cancellationToken)
            .ConfigureAwait(false);
        if (!waited.Success)
        {
            return waited.FailAs<WprTraceResult>();
        }

        JobInfo info = waited.Value!;
        long offset = Math.Max(0, info.OutputBytes - TailBytes);
        OperationResult<JobOutputPage> page = jobs.ReadOutput(info.Id, offset, TailBytes);
        FileLink? download = info.State == JobState.Succeeded ? links?.TryCreateDownload(full) : null;
        return OperationResult<WprTraceResult>.Ok(new WprTraceResult(
            info,
            output,
            info.State == JobState.Succeeded ? outcome.Bytes : null,
            info.State == JobState.Succeeded ? outcome.Sha256 : null,
            download,
            page.Value?.Text,
            info.State == JobState.Running));
    }

    [McpServerTool(Name = "stop_wpr_trace")]
    [Description("Stops an active Vestigo-owned WPR recording and flushes the .etl file.")]
    public Task<OperationResult<WprStatus>> StopWprTrace(CancellationToken cancellationToken = default) =>
        recorder.StopRecordingAsync(cancellationToken);

    [McpServerTool(Name = "cancel_wpr_trace")]
    [Description("Cancels an active Vestigo-owned WPR recording without keeping a usable .etl file.")]
    public Task<OperationResult<WprStatus>> CancelWprTrace(CancellationToken cancellationToken = default) =>
        recorder.CancelRecordingAsync(cancellationToken);

    private async Task<int> RunAsync(
        string profile,
        string destination,
        TimeSpan duration,
        JobContext output,
        StageOutcome outcome,
        CancellationToken cancellationToken)
    {
        OperationResult<WprCopyOutcome> recorded = await recorder.RecordAsync(
            profile,
            destination,
            duration,
            options.Performance.MaxRawEtlBytes,
            cancellationToken).ConfigureAwait(false);
        if (!recorded.Success)
        {
            output.WriteLine(recorded.ErrorCode!);
            if (recorded.Error?.Contains("cleanup_failed", StringComparison.Ordinal) == true)
            {
                output.WriteLine("cleanup_failed");
            }

            return 1;
        }

        outcome.Bytes = recorded.Value!.Bytes;
        outcome.Sha256 = recorded.Value.Sha256;
        output.WriteLine("bytes " + recorded.Value.Bytes);
        output.WriteLine("sha256 " + recorded.Value.Sha256);
        if (links?.TryCreateDownload(destination) is { } ticket)
        {
            output.WriteLine("download " + ticket.Url);
            if (ticket.ExpiresUtc is { } expires)
            {
                output.WriteLine("downloadExpires " + expires.ToString("O"));
            }
        }

        return 0;
    }

    private sealed class StageOutcome
    {
        public long? Bytes { get; set; }

        public string? Sha256 { get; set; }
    }
}
