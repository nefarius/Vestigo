using System.Collections.Concurrent;
using System.Text;

using Vestigo.Configuration;

namespace Vestigo.Jobs;

/// <summary>Lifecycle state of a job.</summary>
public enum JobState
{
    Running,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>Snapshot of one job returned to clients.</summary>
public sealed record JobInfo(
    string Id,
    string Kind,
    string Description,
    JobState State,
    int? ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long OutputBytes,
    string? Error);

/// <summary>One page of job output. Pass <see cref="NextOffset" /> to the next read.</summary>
public sealed record JobOutputPage(string Text, long NextOffset, bool Completed);

/// <summary>
///     Handed to job work so it can append output or redirect a child process to <see cref="OutputPath" />.
/// </summary>
public sealed class JobContext
{
    private readonly object _gate = new();
    private readonly long _limit;
    private bool _truncated;

    internal JobContext(string id, string outputPath, long limit)
    {
        JobId = id;
        OutputPath = outputPath;
        _limit = limit;
    }

    public string JobId { get; }

    /// <summary>Spool file for this job. Child processes may append to it directly.</summary>
    public string OutputPath { get; }

    public long OutputBytes
    {
        get
        {
            try
            {
                return File.Exists(OutputPath) ? new FileInfo(OutputPath).Length : 0;
            }
            catch (IOException)
            {
                return 0;
            }
        }
    }

    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_gate)
        {
            if (_truncated)
            {
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            long current = OutputBytes;
            if (current + bytes.Length > _limit)
            {
                bytes = Encoding.UTF8.GetBytes(Environment.NewLine + "[output truncated by MaxJobOutputBytes]" + Environment.NewLine);
                _truncated = true;
            }

            using FileStream stream = new(OutputPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.Write(bytes);
        }
    }

    public void WriteLine(string text) => Write(text + Environment.NewLine);

    /// <summary>True once the spool, including bytes a child wrote directly, has passed the output limit.</summary>
    public bool LimitExceeded => OutputBytes > _limit;

    /// <summary>Appends the truncation marker once; later writes are dropped.</summary>
    public void WriteTruncationMarker()
    {
        lock (_gate)
        {
            if (_truncated)
            {
                return;
            }

            _truncated = true;
            byte[] bytes = Encoding.UTF8.GetBytes(Environment.NewLine + "[output truncated by MaxJobOutputBytes]" + Environment.NewLine);
            using FileStream stream = new(OutputPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.Write(bytes);
        }
    }
}

/// <summary>
///     Runs bounded, cancellable background jobs with a paged text output spool. Used by long operations
///     such as process runs and installers so MCP calls stay short.
/// </summary>
public sealed class JobManager : BackgroundService
{
    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new(StringComparer.Ordinal);
    private readonly VestigoOptions _options;
    private readonly string _directory;
    private readonly ILogger<JobManager>? _logger;
    private readonly object _startLock = new();

    public JobManager(VestigoOptions options, ILogger<JobManager>? logger = null)
        : this(options, Path.Combine(VestigoPaths.SpoolDirectory, "jobs"), logger)
    {
    }

    internal JobManager(VestigoOptions options, string directory, ILogger<JobManager>? logger = null)
    {
        _options = options;
        _directory = directory;
        _logger = logger;
        Directory.CreateDirectory(_directory);
    }

    public OperationResult<JobInfo> Start(
        string kind,
        string description,
        Func<JobContext, CancellationToken, Task<int>> work)
    {
        string id = Guid.NewGuid().ToString("N")[..12];
        string outputPath = Path.Combine(_directory, id + ".log");
        JobEntry entry;
        lock (_startLock)
        {
            if (_jobs.Values.Count(e => e.State == JobState.Running) >= _options.MaxConcurrentJobs)
            {
                return OperationResult<JobInfo>.Fail("too_many_jobs", $"At most {_options.MaxConcurrentJobs} jobs may run at once.");
            }

            File.WriteAllBytes(outputPath, []);
            entry = new JobEntry(id, kind, description, new JobContext(id, outputPath, _options.MaxJobOutputBytes));
            _jobs[id] = entry;
            entry.Task = Task.Run(() => RunAsync(entry, work));
        }

        return OperationResult<JobInfo>.Ok(entry.Snapshot());
    }

    public IReadOnlyList<JobInfo> List() =>
        _jobs.Values.OrderByDescending(entry => entry.StartedAt).Select(entry => entry.Snapshot()).ToList();

    public OperationResult<JobInfo> Get(string id) =>
        _jobs.TryGetValue(id, out JobEntry? entry)
            ? OperationResult<JobInfo>.Ok(entry.Snapshot())
            : OperationResult<JobInfo>.Fail("job_not_found", $"Job '{id}' does not exist.");

    public OperationResult<JobInfo> Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out JobEntry? entry))
        {
            return OperationResult<JobInfo>.Fail("job_not_found", $"Job '{id}' does not exist.");
        }

        entry.Cancellation.Cancel();
        return OperationResult<JobInfo>.Ok(entry.Snapshot());
    }

    /// <summary>Waits for a job to finish, up to <paramref name="timeout" />.</summary>
    public async Task<OperationResult<JobInfo>> WaitAsync(string id, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(id, out JobEntry? entry))
        {
            return OperationResult<JobInfo>.Fail("job_not_found", $"Job '{id}' does not exist.");
        }

        if (entry.Task is { } task)
        {
            await Task.WhenAny(task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        }

        return OperationResult<JobInfo>.Ok(entry.Snapshot());
    }

    public OperationResult<JobOutputPage> ReadOutput(string id, long offset, int? limit)
    {
        if (!_jobs.TryGetValue(id, out JobEntry? entry))
        {
            return OperationResult<JobOutputPage>.Fail("job_not_found", $"Job '{id}' does not exist.");
        }

        if (offset < 0)
        {
            return OperationResult<JobOutputPage>.Fail("invalid_request", "offset cannot be negative.");
        }

        bool finished = entry.State != JobState.Running;
        int count = Math.Clamp(limit ?? _options.MaxJobReadBytes, 1, _options.MaxJobReadBytes);
        byte[] buffer = new byte[count];
        int read;
        bool atEnd;
        using (FileStream stream = new(entry.Context.OutputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (offset > stream.Length)
            {
                offset = stream.Length;
            }

            stream.Seek(offset, SeekOrigin.Begin);
            read = stream.Read(buffer, 0, count);
            atEnd = offset + read >= stream.Length;
        }

        int safe = Utf8SafeLength(buffer, read, endOfData: finished && atEnd);
        if (safe == 0 && read > 0)
        {
            // Never stall on a window smaller than one character; decode with replacement characters.
            safe = read;
        }

        string text = Encoding.UTF8.GetString(buffer, 0, safe);
        bool completed = finished && offset + safe >= entry.Context.OutputBytes;
        return OperationResult<JobOutputPage>.Ok(new JobOutputPage(text, offset + safe, completed));
    }

    /// <summary>Removes finished jobs older than the retention period. Returns the number removed.</summary>
    internal int Purge(DateTimeOffset now)
    {
        int removed = 0;
        foreach (JobEntry entry in _jobs.Values)
        {
            if (entry.EndedAt is { } ended && now - ended > _options.JobRetention && _jobs.TryRemove(entry.Id, out _))
            {
                TryDelete(entry.Context.OutputPath);
                entry.Cancellation.Dispose();
                removed++;
            }
        }

        return removed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Purge(DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException)
        {
            // Host is stopping.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (JobEntry entry in _jobs.Values)
        {
            entry.Cancellation.Cancel();
        }

        Task[] running = _jobs.Values.Select(entry => entry.Task).OfType<Task>().ToArray();
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)).ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static int Utf8SafeLength(byte[] buffer, int length, bool endOfData)
    {
        if (endOfData || length == 0)
        {
            return length;
        }

        int index = length - 1;
        int continuation = 0;
        while (index >= 0 && (buffer[index] & 0xC0) == 0x80 && continuation < 3)
        {
            index--;
            continuation++;
        }

        if (index < 0)
        {
            return length;
        }

        byte lead = buffer[index];
        int expected = lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : lead >= 0xC0 ? 2 : 1;
        return continuation + 1 < expected ? index : length;
    }

    private async Task RunAsync(JobEntry entry, Func<JobContext, CancellationToken, Task<int>> work)
    {
        try
        {
            int exitCode = await work(entry.Context, entry.Cancellation.Token).ConfigureAwait(false);
            entry.Complete(exitCode == 0 ? JobState.Succeeded : JobState.Failed, exitCode, null);
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        {
            entry.Complete(JobState.Cancelled, null, "Cancelled.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogWarning(ex, "Job {JobId} ({Kind}) failed", entry.Id, entry.Kind);
            entry.Context.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            entry.Complete(JobState.Failed, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retention cleanup is best effort.
        }
    }

    private sealed class JobEntry(string id, string kind, string description, JobContext context)
    {
        private int _state;
        private int? _exitCode;
        private string? _error;
        private long _endedTicks;

        public string Id { get; } = id;

        public string Kind { get; } = kind;

        public string Description { get; } = description;

        public JobContext Context { get; } = context;

        public CancellationTokenSource Cancellation { get; } = new();

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public Task? Task { get; set; }

        public JobState State => (JobState)Volatile.Read(ref _state);

        public DateTimeOffset? EndedAt
        {
            get
            {
                long ticks = Interlocked.Read(ref _endedTicks);
                return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
            }
        }

        public void Complete(JobState state, int? exitCode, string? error)
        {
            _exitCode = exitCode;
            _error = error;
            Interlocked.Exchange(ref _endedTicks, DateTimeOffset.UtcNow.UtcTicks);
            Volatile.Write(ref _state, (int)state);
        }

        public JobInfo Snapshot() =>
            new(Id, Kind, Description, State, _exitCode, StartedAt, EndedAt, Context.OutputBytes, _error);
    }
}
