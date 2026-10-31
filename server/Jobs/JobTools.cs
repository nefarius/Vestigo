using System.ComponentModel;

using ModelContextProtocol.Server;

namespace Vestigo.Jobs;

/// <summary>MCP tools for inspecting and cancelling long-running jobs started by other tools.</summary>
[McpServerToolType]
public sealed class JobTools(JobManager jobs)
{
    [McpServerTool(Name = "list_jobs")]
    [Description("Lists background jobs owned by this Vestigo process, newest first.")]
    public IReadOnlyList<JobInfo> ListJobs() => jobs.List();

    [McpServerTool(Name = "get_job")]
    [Description("Returns state, exit code, and output size for one job.")]
    public OperationResult<JobInfo> GetJob([Description("Job identifier.")] string jobId) => jobs.Get(jobId);

    [McpServerTool(Name = "read_job_output")]
    [Description("Reads a page of text output from a job. Pass the returned NextOffset to continue.")]
    public OperationResult<JobOutputPage> ReadJobOutput(
        [Description("Job identifier.")] string jobId,
        [Description("Byte offset to read from. Use 0 for the beginning.")] long offset = 0,
        [Description("Maximum bytes to return. Clamped to MaxJobReadBytes.")] int? limit = null) =>
        jobs.ReadOutput(jobId, offset, limit);

    [McpServerTool(Name = "wait_job")]
    [Description("Waits up to the given number of seconds for a job to finish and returns its state.")]
    public Task<OperationResult<JobInfo>> WaitJob(
        [Description("Job identifier.")] string jobId,
        [Description("Maximum seconds to wait, 0 to 300.")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default) =>
        jobs.WaitAsync(jobId, TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 0, 300)), cancellationToken);

    [McpServerTool(Name = "cancel_job")]
    [Description("Requests cancellation of a running job. Process jobs kill their process tree.")]
    public OperationResult<JobInfo> CancelJob([Description("Job identifier.")] string jobId) => jobs.Cancel(jobId);
}
