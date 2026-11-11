using System.Runtime.InteropServices;

using Vestigo.Jobs;

namespace Vestigo.Processes;

/// <summary>Which account and session a process runs in.</summary>
public enum LaunchContext
{
    /// <summary>The Vestigo service account (normally SYSTEM), non-interactive.</summary>
    Service,

    /// <summary>The active console user. The desktop is <c>winsta0\default</c> unless a managed session selects another.</summary>
    Interactive
}

/// <summary>
///     What Windows actually used for a launch. <see cref="WindowStation" /> and <see cref="Desktop" /> are null when
///     Vestigo did not select them and let the service process assign them.
/// </summary>
public sealed record ProcessLaunchReport(
    int ProcessId,
    int? SessionId,
    string? WindowStation,
    string? Desktop,
    bool Elevated,
    string ElevationType,
    DateTimeOffset? StartedAt = null);

/// <summary>Filled by the launcher as soon as the process exists, before it is waited on.</summary>
public sealed class ProcessLaunchReportBox
{
    private readonly TaskCompletionSource<ProcessLaunchReport> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProcessLaunchReport? Value { get; private set; }

    public Task<ProcessLaunchReport> Ready => _ready.Task;

    public void Publish(ProcessLaunchReport report)
    {
        Value = report;
        _ready.TrySetResult(report);
    }
}

/// <summary>A fully resolved process launch. <see cref="Executable" /> is an absolute path.</summary>
public sealed record LaunchRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    LaunchContext Context,
    bool Elevated,
    string? Desktop = null,
    ProcessLaunchReportBox? Report = null,
    SafeHandle? StandardInput = null,
    Action? ReleaseInheritedInput = null);

/// <summary>Raised when a process cannot be started. <see cref="Code" /> is a stable error code.</summary>
/// <param name="reason">Optional stable token, such as why no console user is available.</param>
public sealed class ProcessLaunchException(string code, string message, string? reason = null) : Exception($"{code}: {message}")
{
    public string Code { get; } = code;

    public string? Reason { get; } = reason;
}

/// <summary>Starts processes for jobs and appends their stdout and stderr to the job spool.</summary>
public interface IProcessLauncher
{
    /// <summary>
    ///     Cheap pre-flight check so tools can return <c>no_interactive_session</c> before a job is created.
    /// </summary>
    OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated);

    /// <summary>
    ///     Runs the process to completion and returns its exit code. Cancelling <paramref name="cancellationToken" />
    ///     kills the whole process tree and throws <see cref="OperationCanceledException" />.
    /// </summary>
    Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken);
}
