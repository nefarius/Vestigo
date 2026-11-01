namespace Vestigo.DebugOutput;

/// <summary>One stdout or stderr line from DebugViewConsole.</summary>
public sealed record DebugViewLine(string Text, bool IsError);

/// <summary>
///     Spawns DebugViewConsole. Abstracted so tests can verify argument lists and feed lines without running it.
/// </summary>
public interface IDebugViewProcess
{
    /// <summary>
    ///     Runs <paramref name="executable" /> with a discrete argument list (no shell) until cancelled or the
    ///     process exits. <paramref name="onStarted" /> receives the PID as soon as the process is created.
    ///     Cancellation asks for a graceful Ctrl+C stop, then kills the process tree.
    /// </summary>
    IAsyncEnumerable<DebugViewLine> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<int>? onStarted,
        CancellationToken cancellationToken);
}
