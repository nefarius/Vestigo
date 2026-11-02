namespace Vestigo.LogFiles;

/// <summary>One stdout or stderr line from ripgrep.</summary>
public sealed record RipgrepLine(string Text, bool IsError);

/// <summary>
///     Spawns ripgrep. Abstracted so tests can verify argument lists and feed JSON lines without running it.
/// </summary>
public interface IRipgrepProcess
{
    /// <summary>
    ///     Runs <paramref name="executable" /> with a discrete argument list (no shell) until it exits or the token
    ///     is cancelled, which kills the process tree. <paramref name="onExited" /> receives the exit code.
    /// </summary>
    IAsyncEnumerable<RipgrepLine> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<int>? onExited,
        CancellationToken cancellationToken);
}
