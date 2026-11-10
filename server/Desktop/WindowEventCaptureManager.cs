using System.Collections.Concurrent;
using System.Text.Json;

using Vestigo.Configuration;
using Vestigo.Processes;
using Vestigo.Security;
using Vestigo.Spooling;

namespace Vestigo.Desktop;

public interface IWindowEventSession : IAsyncDisposable
{
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken);
}

public interface IWindowEventSessionFactory
{
    Task<OperationResult<IWindowEventSession>> StartAsync(WindowEventStart start, CancellationToken cancellationToken);
}

/// <summary>Owns window-event captures, their spools, and helper-process cleanup.</summary>
public sealed class WindowEventCaptureManager : IHostedService, IDisposable
{
    private readonly VestigoOptions _options;
    private readonly IWindowEventSessionFactory _sessions;
    private readonly TimeProvider _time;
    private readonly string _directory;
    private readonly ILogger<WindowEventCaptureManager>? _logger;
    private readonly ConcurrentDictionary<string, CaptureJob> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _retention;

    public WindowEventCaptureManager(
        VestigoOptions options,
        IWindowEventSessionFactory sessions,
        TimeProvider time,
        ILogger<WindowEventCaptureManager>? logger = null)
        : this(options, sessions, time, Path.Combine(VestigoPaths.SpoolDirectory, "window-events"), logger)
    {
    }

    internal WindowEventCaptureManager(
        VestigoOptions options,
        IWindowEventSessionFactory sessions,
        TimeProvider time,
        string directory,
        ILogger<WindowEventCaptureManager>? logger = null)
    {
        _options = options;
        _sessions = sessions;
        _time = time;
        _directory = directory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        if (OperatingSystem.IsWindows())
        {
            WindowEventProcessCleanup.Recover();
        }

        CleanupExpired();
        _retention = new Timer(_ => CleanupExpired(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_retention is not null)
        {
            await _retention.DisposeAsync().ConfigureAwait(false);
        }

        foreach (string id in _jobs.Keys.ToArray())
        {
            await StopAsync(id, "service_shutdown").ConfigureAwait(false);
        }
    }

    public async Task<OperationResult<WindowEventCaptureStatus>> StartAsync(
        WindowEventStart start,
        CancellationToken cancellationToken)
    {
        if (start.ProcessId is null && string.IsNullOrWhiteSpace(start.Handle))
        {
            return OperationResult<WindowEventCaptureStatus>.Fail("invalid_request", "Provide a process id, a window handle, or both.");
        }

        if (start.ProcessId is <= 0)
        {
            return OperationResult<WindowEventCaptureStatus>.Fail("invalid_request", "pid must be positive.");
        }

        if (start.Handle is not null)
        {
            if (!WindowEventLines.TryParseHandle(start.Handle, out string canonical))
            {
                return OperationResult<WindowEventCaptureStatus>.Fail("invalid_request", "windowHandle must be a non-zero hex handle such as 0x1A2B.");
            }

            start = start with { Handle = canonical };
        }

        if (start.Duration <= TimeSpan.Zero || start.Duration > _options.Desktop.MaxWindowEventDuration)
        {
            return OperationResult<WindowEventCaptureStatus>.Fail("invalid_request", "duration must be positive and within Vestigo:Desktop:MaxWindowEventDuration.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_jobs.Values.Count(job => job.State is "Starting" or "Running") >= _options.Desktop.MaxWindowEventCaptures)
            {
                return OperationResult<WindowEventCaptureStatus>.Fail("too_many_captures", "The window-event capture limit has been reached.");
            }

            string id = Guid.NewGuid().ToString("N")[..12];
            CaptureJob job = new(id, Path.Combine(_directory, id + ".ndjson"), start, new WindowEventPipeline(start), _time.GetUtcNow());
            _jobs[id] = job;
            OperationResult<IWindowEventSession> session = await _sessions.StartAsync(start, job.Cancellation.Token).ConfigureAwait(false);
            if (!session.Success)
            {
                job.State = "Failed";
                job.Error = session.Error;
                job.StoppedAt = _time.GetUtcNow();
                return session.FailAs<WindowEventCaptureStatus>();
            }

            job.Session = session.Value;
            job.Reader = Task.Run(() => ReadLoopAsync(job));
            try
            {
                await job.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await StopAsync(id, "ready_timeout").ConfigureAwait(false);
                return OperationResult<WindowEventCaptureStatus>.Fail("helper_timeout", "The window-event capture did not become ready.");
            }

            if (job.Pipeline.StartErrorCode is not null)
            {
                await StopAsync(id, "failed").ConfigureAwait(false);
                return OperationResult<WindowEventCaptureStatus>.Fail(job.Pipeline.StartErrorCode, job.Pipeline.StartError!);
            }

            if (job.State == "Starting")
            {
                job.State = "Running";
            }

            WindowEventProcessCleanup.Remember(job.Pipeline.HelperPid, job.Pipeline.HelperStartTime);
            return OperationResult<WindowEventCaptureStatus>.Ok(job.Status());
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<WindowEventCaptureStatus> List() =>
        _jobs.Values.Select(job => job.Status()).OrderBy(job => job.StartedAt).ToArray();

    public OperationResult<WindowEventCaptureStatus> Get(string id) =>
        _jobs.TryGetValue(id, out CaptureJob? job)
            ? OperationResult<WindowEventCaptureStatus>.Ok(job.Status())
            : OperationResult<WindowEventCaptureStatus>.Fail("not_found", "Window-event capture was not found.");

    public async Task<OperationResult<WindowEventPage>> ReadAsync(
        string id,
        long afterSequence,
        int? limit,
        TimeSpan wait,
        CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(id, out CaptureJob? job))
        {
            return OperationResult<WindowEventPage>.Fail("not_found", "Window-event capture was not found.");
        }

        if (afterSequence < 0)
        {
            return OperationResult<WindowEventPage>.Fail("invalid_request", "afterSequence cannot be negative.");
        }

        int batch = Math.Clamp(limit ?? _options.MaxReadBatch, 1, _options.MaxReadBatch);
        TimeSpan effective = wait < TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Min(wait.Ticks, _options.MaxReadWait.Ticks));
        NdjsonPage page = await job.Spool.ReadAsync(afterSequence, batch, () => job.State, effective, cancellationToken).ConfigureAwait(false);
        List<WindowEventRecord> events = [];
        foreach (string line in page.Events)
        {
            try
            {
                WindowEventRecord? record = JsonSerializer.Deserialize<WindowEventRecord>(line, SessionHelper.Json);
                if (record is not null)
                {
                    events.Add(record);
                }
            }
            catch (JsonException)
            {
                events.Add(new WindowEventRecord(
                    page.NextSequence, _time.GetUtcNow(), null, "collection-error", null, "observed",
                    "A stored capture line could not be read.", null, null, null, null, null, null, null, null, null,
                    ["untrusted_event"], null, null, null, null, false));
            }
        }

        return OperationResult<WindowEventPage>.Ok(new WindowEventPage(
            job.Id, page.NextSequence, events, page.State, page.Completed, job.Pipeline.Dropped, job.Truncated));
    }

    public Task<OperationResult<WindowEventCaptureStatus>> StopAsync(string id) => StopAsync(id, "stopped");

    public void Dispose()
    {
        _retention?.Dispose();
        _gate.Dispose();
    }

    internal void CleanupExpired()
    {
        DateTimeOffset cutoff = _time.GetUtcNow() - _options.Desktop.WindowEventRetention;
        foreach (CaptureJob job in _jobs.Values.ToArray())
        {
            if (job.StoppedAt is DateTimeOffset stopped && stopped < cutoff && job.State is "Stopped" or "Failed")
            {
                TryDelete(job.Spool.Path);
                _jobs.TryRemove(job.Id, out _);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task ReadLoopAsync(CaptureJob job)
    {
        try
        {
            job.Spool.OpenWrite();
            await foreach (string line in job.Session!.ReadLinesAsync(job.Cancellation.Token).ConfigureAwait(false))
            {
                if (job.Spool.EventCount >= _options.Desktop.MaxWindowEvents
                    || Interlocked.Read(ref job.Spool.ByteCount) >= _options.Desktop.MaxWindowEventBytes)
                {
                    job.Truncated = true;
                    job.StopReason = "limit_reached";
                    break;
                }

                foreach (WindowEventRecord record in job.Pipeline.Accept(line, _time.GetUtcNow()))
                {
                    job.HelperPid ??= job.Pipeline.HelperPid;
                    await AppendAsync(job, record).ConfigureAwait(false);
                    if (record.Kind is "ready" or "failed")
                    {
                        job.Ready.TrySetResult(true);
                    }
                }

                if (job.Pipeline.StartErrorCode is not null || job.Pipeline.SawCompletion)
                {
                    break;
                }
            }

            job.StopReason ??= job.Pipeline.CompletionReason
                ?? (job.Cancellation.IsCancellationRequested ? "stopped" : "completed");
            job.State = job.Pipeline.StartErrorCode is null ? "Stopped" : "Failed";
        }
        catch (OperationCanceledException)
        {
            job.StopReason ??= "stopped";
            job.State = "Stopped";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            job.State = "Failed";
            job.Error = ex.GetType().Name;
            job.StopReason = "failed";
            _logger?.LogWarning("Window-event capture {CaptureId} failed: {ErrorType}", job.Id, ex.GetType().Name);
        }
        finally
        {
            if (!job.Pipeline.SawCompletion && job.Spool.EventCount < _options.Desktop.MaxWindowEvents)
            {
                try
                {
                    await AppendAsync(job, job.Pipeline.Completed(job.StopReason ?? "stopped", job.Truncated, _time.GetUtcNow())).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    job.Error ??= "completion_not_stored";
                }
            }

            job.StoppedAt ??= _time.GetUtcNow();
            job.Ready.TrySetResult(true);
            if (job.Session is not null)
            {
                await job.Session.DisposeAsync().ConfigureAwait(false);
            }

            await job.Spool.DisposeAsync().ConfigureAwait(false);
            job.Spool.Notify();
            WindowEventProcessCleanup.Clear(job.HelperPid);
        }
    }

    private static async Task AppendAsync(CaptureJob job, WindowEventRecord record)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, SessionHelper.Json);
        await job.Spool.AppendAsync(bytes).ConfigureAwait(false);
    }

    private async Task<OperationResult<WindowEventCaptureStatus>> StopAsync(string id, string reason)
    {
        if (!_jobs.TryGetValue(id, out CaptureJob? job))
        {
            return OperationResult<WindowEventCaptureStatus>.Fail("not_found", "Window-event capture was not found.");
        }

        job.StopReason ??= reason;
        await job.Cancellation.CancelAsync().ConfigureAwait(false);
        if (job.Reader is not null)
        {
            try
            {
                await job.Reader.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                job.Error ??= "stop_timeout";
            }
        }

        return OperationResult<WindowEventCaptureStatus>.Ok(job.Status());
    }

    private sealed class CaptureJob(string id, string path, WindowEventStart start, WindowEventPipeline pipeline, DateTimeOffset startedAt)
    {
        public string Id { get; } = id;

        public NdjsonSpool Spool { get; } = new(path);

        public WindowEventPipeline Pipeline { get; } = pipeline;

        public string State { get; set; } = "Starting";

        public bool Truncated { get; set; }

        public DateTimeOffset StartedAt { get; } = startedAt;

        public DateTimeOffset? StoppedAt { get; set; }

        public string? StopReason { get; set; }

        public string? Error { get; set; }

        public int? HelperPid { get; set; }

        public IWindowEventSession? Session { get; set; }

        public Task? Reader { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();

        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WindowEventCaptureStatus Status() => new(
            Id,
            State,
            Spool.EventCount,
            Spool.ByteCount,
            Pipeline.Dropped,
            Pipeline.CollectionErrors,
            Truncated,
            start.IncludesNewWindows,
            false,
            StartedAt,
            StoppedAt,
            StopReason,
            Error);
    }
}
