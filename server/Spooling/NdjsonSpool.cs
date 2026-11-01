namespace Vestigo.Spooling;

/// <summary>One page of spooled NDJSON lines. <c>Completed</c> means no more events will ever follow.</summary>
public sealed record NdjsonPage(
    long NextSequence,
    IReadOnlyList<string> Events,
    string State,
    bool Completed);

/// <summary>
///     Append-only NDJSON file with a sparse byte-offset checkpoint every
///     <see cref="CheckpointInterval" /> events and a long-poll signal.
/// </summary>
public sealed class NdjsonSpool : IAsyncDisposable
{
    public const int CheckpointInterval = 1024;

    private readonly object _checkpointLock = new();
    private readonly List<long> _checkpoints = [];
    private TaskCompletionSource _signal = NewSignal();
    private FileStream? _stream;

    public NdjsonSpool(string path) => Path = path;

    public string Path { get; }

    public long EventCount;

    public long ByteCount;

    /// <summary>Completes when new events are written or the producer ends.</summary>
    public TaskCompletionSource Signal => Volatile.Read(ref _signal);

    public void Notify() => Interlocked.Exchange(ref _signal, NewSignal()).TrySetResult();

    public void OpenWrite()
    {
        _stream = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    /// <summary>Writes exact bytes plus <c>'\n'</c>, publishes counters, and signals waiters.</summary>
    public async Task<long> AppendAsync(ReadOnlyMemory<byte> item)
    {
        FileStream stream = _stream ?? throw new InvalidOperationException("The spool is not open for writing.");
        await stream.WriteAsync(item, CancellationToken.None).ConfigureAwait(false);
        stream.WriteByte((byte)'\n');
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        long events = Interlocked.Increment(ref EventCount);
        long bytes = Interlocked.Add(ref ByteCount, item.Length + 1);
        if (events % CheckpointInterval == 0)
        {
            AddCheckpoint(bytes);
        }

        Notify();
        return events;
    }

    /// <summary>Records the byte offset that follows event number (n+1)*<see cref="CheckpointInterval" />.</summary>
    public void AddCheckpoint(long offset)
    {
        lock (_checkpointLock)
        {
            _checkpoints.Add(offset);
        }
    }

    /// <summary>Returns the nearest checkpoint at or before <paramref name="sequence" />.</summary>
    public (long Sequence, long Offset) FindCheckpoint(long sequence)
    {
        lock (_checkpointLock)
        {
            int index = (int)Math.Min(sequence / CheckpointInterval, _checkpoints.Count);
            return index == 0 ? (0, 0) : (index * (long)CheckpointInterval, _checkpoints[index - 1]);
        }
    }

    /// <summary>
    ///     Reads lines after <paramref name="afterSequence" />. When <paramref name="include" /> is set,
    ///     scans at most <paramref name="scanLimit" /> lines (or <paramref name="batch" /> when the limit
    ///     is not positive) and advances <c>NextSequence</c> past every scanned line, matched or not.
    /// </summary>
    public NdjsonPage Read(
        long afterSequence,
        int batch,
        string state,
        Func<string, bool>? include = null,
        int scanLimit = 0)
    {
        string currentState = state;
        bool terminal = currentState is "Stopped" or "Failed";
        long committed = Interlocked.Read(ref ByteCount);
        long eventCount = Interlocked.Read(ref EventCount);
        int maxScan = include is null
            ? batch
            : Math.Max(batch, scanLimit > 0 ? scanLimit : batch);
        List<string> events = [];
        int scanned = 0;

        if (committed > 0 && File.Exists(Path))
        {
            (long sequence, long offset) = FindCheckpoint(afterSequence);
            using FileStream stream = new(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(offset, SeekOrigin.Begin);
            using BufferedStream buffered = new(stream, 64 * 1024);
            using MemoryStream line = new();
            long position = offset;
            while (position < committed && scanned < maxScan && events.Count < batch)
            {
                int next = buffered.ReadByte();
                if (next < 0)
                {
                    break;
                }

                position++;
                if (next != '\n')
                {
                    line.WriteByte((byte)next);
                    continue;
                }

                if (line.Length > 0)
                {
                    sequence++;
                    if (sequence > afterSequence)
                    {
                        scanned++;
                        string text = System.Text.Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
                        if (include is null || include(text))
                        {
                            events.Add(text);
                        }
                    }

                    line.SetLength(0);
                }
            }
        }

        long nextSequence = afterSequence + scanned;
        bool completed = terminal && nextSequence >= eventCount;
        return new NdjsonPage(nextSequence, events, currentState, completed);
    }

    /// <summary>
    ///     Reads after <paramref name="afterSequence" />. When no unread lines remain and the producer is
    ///     still running, waits up to <paramref name="wait" /> for a signal. <paramref name="state" /> is
    ///     invoked on each attempt so a terminal state set while waiting is visible.
    /// </summary>
    public async Task<NdjsonPage> ReadAsync(
        long afterSequence,
        int batch,
        Func<string> state,
        TimeSpan wait,
        CancellationToken cancellationToken,
        Func<string, bool>? include = null,
        int scanLimit = 0)
    {
        TaskCompletionSource signal = Signal;
        NdjsonPage page = Read(afterSequence, batch, state(), include, scanLimit);
        bool noUnread = afterSequence >= Interlocked.Read(ref EventCount);
        if (page.Events.Count == 0 && noUnread && !page.Completed && wait > TimeSpan.Zero)
        {
            try
            {
                await signal.Task.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }

            page = Read(afterSequence, batch, state(), include, scanLimit);
        }

        return page;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
