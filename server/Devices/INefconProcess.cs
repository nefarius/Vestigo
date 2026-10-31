namespace Vestigo.Devices;

/// <summary>Raw result of a spawned nefcon process.</summary>
public sealed record NefconProcessOutput(int ExitCode, string Output, bool TimedOut);

/// <summary>
///     Spawns the nefcon executable. Abstracted so tests can verify argument lists without running it.
/// </summary>
public interface INefconProcess
{
    /// <summary>
    ///     Runs <paramref name="executable" /> with a discrete argument list (no shell, no quoting) and
    ///     kills the process tree after <paramref name="timeout" />.
    /// </summary>
    Task<NefconProcessOutput> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}