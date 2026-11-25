using Vestigo.Desktop;

namespace Vestigo.Input;

/// <summary>
///     Serializes <c>send_input</c> helper runs that share an input desktop. The gate is held from helper start
///     through helper exit, which includes cleanup releases, and is released on cancellation and timeout.
/// </summary>
public sealed class InputSubmissionGate
{
    private readonly object _sync = new();

    private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    private int _waiters;

    /// <summary>Calls waiting because another run holds the same desktop. Tests use this to observe the queue.</summary>
    internal int Waiters => Volatile.Read(ref _waiters);

    public async Task<T> RunAsync<T>(string? desktopPath, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = GateFor(desktopPath);
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _waiters);
            try
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _waiters);
            }
        }

        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim GateFor(string? desktopPath)
    {
        string key = string.IsNullOrWhiteSpace(desktopPath) ? DesktopSessionPolicy.DefaultPath : desktopPath.Trim();
        lock (_sync)
        {
            if (!_gates.TryGetValue(key, out SemaphoreSlim? gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _gates[key] = gate;
            }

            return gate;
        }
    }
}
