using Vestigo.Jobs;

namespace Vestigo.Processes;

/// <summary>Which account and session a process runs in.</summary>
public enum LaunchContext
{
    /// <summary>The Vestigo service account (normally SYSTEM), non-interactive.</summary>
    Service,

    /// <summary>The active console user, on the interactive desktop <c>winsta0\default</c>.</summary>
    Interactive
}

/// <summary>A fully resolved process launch. <see cref="Executable" /> is an absolute path.</summary>
public sealed record LaunchRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    LaunchContext Context,
    bool Elevated);

/// <summary>Raised when a process cannot be started. <see cref="Code" /> is a stable error code.</summary>
public sealed class ProcessLaunchException(string code, string message) : Exception($"{code}: {message}")
{
    public string Code { get; } = code;
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
