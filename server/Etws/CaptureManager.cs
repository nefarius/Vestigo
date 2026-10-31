using System.Collections.Concurrent;
using System.Text.Json;

using Nefarius.Utilities.ETW.Deserializer.WPP;
using Nefarius.Utilities.ETW.Symbols;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Etws;

/// <summary>Status of one managed realtime capture.</summary>
public sealed record CaptureStatus(
    string Id,
    string SessionName,
    string State,
    long EventCount,
    long ByteCount,
    int MissingFormatCount,
    DateTimeOffset StartedAt,
    DateTimeOffset? StoppedAt,
    string? StopReason,
    string? Error,
    string Mode = CaptureManager.ManagedMode);

/// <summary>One page of spooled NDJSON events. <c>Completed</c> means no more events will ever follow.</summary>
public sealed record CapturePage(
    string Id,
    long NextSequence,
    IReadOnlyList<string> Events,
    string State,
    bool Completed);

/// <summary>Arguments for attaching to an existing external realtime session.</summary>
public sealed record AttachSessionRequest(string SessionName, IReadOnlyList<string> SymbolPaths, TimeSpan? Duration);

/// <summary>Arguments for starting a managed capture.</summary>
public sealed record StartCaptureRequest(
    IReadOnlyList<string> PdbPaths,
    IReadOnlyList<string> TmfPaths,
    IReadOnlyList<Guid> ProviderIds,
    string Level,
    ulong MatchAnyKeyword,
    ulong MatchAllKeyword,
    TimeSpan? Duration);

/// <summary>
///     Owns realtime capture jobs, their spools, and Vestigo-prefixed ETW sessions.
/// </summary>
public sealed class CaptureManager : IHostedService, IDisposable
{
    public const string SessionPrefix = "Vestigo-";
    public const string ManagedMode = "Managed";
    public const string AttachedMode = "Attached";
    private const int CheckpointInterval = 1024;

    private readonly VestigoOptions _options;
    private readonly SymbolCatalogService _symbols;
    private readonly IRealtimeTraceSessionFactory _sessions;
    private readonly ILogger<CaptureManager> _logger;
    private readonly ConcurrentDictionary<string, CaptureJob> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sessionFileLock = new();
    private readonly ConcurrentDictionary<string, byte> _unstoppedOrphans = new(StringComparer.Ordinal);
    private Timer? _retentionTimer;

    public CaptureManager(
        VestigoOptions options,
        SymbolCatalogService symbols,
        IRealtimeTraceSessionFactory sessions,
        ILogger<CaptureManager> logger)
    {
        _options = options;
        _symbols = symbols;
        _sessions = sessions;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        StateDirectories.EnsureSubdirectory(VestigoPaths.SpoolDirectory);
        await RecoverOwnedSessionsAsync().ConfigureAwait(false);
        CleanupExpired();
        _retentionTimer = new Timer(_ => CleanupExpired(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_retentionTimer is not null)
        {
            await _retentionTimer.DisposeAsync().ConfigureAwait(false);
        }

        foreach (string id in _jobs.Keys.ToArray())
        {
            await StopAsync(id, "service_shutdown").ConfigureAwait(false);
        }
    }

    public async Task<OperationResult<CaptureStatus>> StartAsync(
        StartCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse(request.Level, ignoreCase: true, out TraceLevelSelection level) ||
            !Enum.IsDefined(level))
        {
            return OperationResult<CaptureStatus>.Fail("invalid_request", "Level must be a TraceEventLevel name.");
        }

        List<string> symbolPaths = [..request.PdbPaths, ..request.TmfPaths];
        OperationResult<DecodingSourceSet> loaded = _symbols.LoadExplicit(symbolPaths);
        if (!loaded.Success)
        {
            return OperationResult<CaptureStatus>.Fail(loaded.ErrorCode!, loaded.Error!);
        }

        HashSet<Guid> providers = request.ProviderIds.Where(provider => provider != Guid.Empty).ToHashSet();
        foreach (PdbFileDecodingContextType pdb in loaded.Value!.Sources.OfType<PdbFileDecodingContextType>())
        {
            foreach (Guid provider in pdb.ProviderGuids)
            {
                providers.Add(provider);
            }
        }

        if (providers.Count == 0)
        {
            return OperationResult<CaptureStatus>.Fail(
                "providers_required",
                "Provide provider GUIDs or a PDB that contains WPP control GUIDs.");
        }

        TimeSpan duration = request.Duration is null
            ? _options.MaxCaptureDuration
            : TimeSpan.FromTicks(Math.Min(request.Duration.Value.Ticks, _options.MaxCaptureDuration.Ticks));
        if (duration <= TimeSpan.Zero)
        {
            return OperationResult<CaptureStatus>.Fail("invalid_request", "Capture duration must be positive.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CaptureJob? job = null;
        try
        {
            int active = _jobs.Values.Count(candidate => candidate.State is "Running" or "Starting");
            if (active >= _options.MaxConcurrentCaptures)
            {
                return OperationResult<CaptureStatus>.Fail("too_many_captures", "The concurrent capture limit has been reached.");
            }

            string id = Guid.NewGuid().ToString("N")[..12];
            string sessionName = SessionPrefix + id;
            job = new CaptureJob(id, sessionName, Path.Combine(VestigoPaths.SpoolDirectory, id + ".ndjson"));
            _jobs[id] = job;
            SaveOwnedSessions();

            try
            {
                job.Session = _sessions.Create(sessionName, _options);
                foreach (Guid provider in providers)
                {
                    job.Session.EnableProvider(provider, level, request.MatchAnyKeyword, request.MatchAllKeyword);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                job.State = "Failed";
                job.Error = ex.GetType().Name + ": " + ex.Message;
                job.StoppedAt = DateTimeOffset.UtcNow;
                if (job.Session is not null)
                {
                    await job.Session.DisposeAsync().ConfigureAwait(false);
                }

                SaveOwnedSessions();
                return OperationResult<CaptureStatus>.Fail(ClassifyStartError(ex), job.Error);
            }

            job.State = "Running";
            job.Reader = Task.Run(() => ReadLoopAsync(job, loaded.Value, duration), CancellationToken.None);
            _logger.LogInformation(
                "Started capture {CaptureId} for {ProviderCount} providers",
                job.Id,
                providers.Count);
            return OperationResult<CaptureStatus>.Ok(job.ToStatus());
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<CaptureStatus> List() => _jobs.Values.Select(job => job.ToStatus()).OrderBy(job => job.StartedAt).ToArray();

    public OperationResult<CaptureStatus> Get(string id)
    {
        if (!_jobs.TryGetValue(id, out CaptureJob? job))
        {
            return OperationResult<CaptureStatus>.Fail("not_found", "Capture was not found.");
        }

        return OperationResult<CaptureStatus>.Ok(job.ToStatus());
    }

    public OperationResult<CapturePage> Read(string id, long afterSequence, int? limit) =>
        ReadAsync(id, afterSequence, limit, TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    ///     Reads events after <paramref name="afterSequence" />. When none are available and the capture is still
    ///     running, waits up to <paramref name="wait" /> (clamped to MaxReadWait) for new events.
    /// </summary>
    public async Task<OperationResult<CapturePage>> ReadAsync(
        string id,
        long afterSequence,
        int? limit,
        TimeSpan wait,
        CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(id, out CaptureJob? job))
        {
            return OperationResult<CapturePage>.Fail("not_found", "Capture was not found.");
        }

        if (afterSequence < 0)
        {
            return OperationResult<CapturePage>.Fail("invalid_request", "afterSequence cannot be negative.");
        }

        int batch = Math.Clamp(limit ?? _options.MaxReadBatch, 1, _options.MaxReadBatch);
        TimeSpan effectiveWait = wait < TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(Math.Min(wait.Ticks, _options.MaxReadWait.Ticks));

        // Capture the signal before looking for data so a write between the check and the await is not lost.
        TaskCompletionSource signal = job.Signal;
        CapturePage page = ReadPage(job, afterSequence, batch);
        if (page.Events.Count == 0 && !page.Completed && effectiveWait > TimeSpan.Zero)
        {
            try
            {
                await signal.Task.WaitAsync(effectiveWait, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }

            page = ReadPage(job, afterSequence, batch);
        }

        return OperationResult<CapturePage>.Ok(page);
    }

    private static CapturePage ReadPage(CaptureJob job, long afterSequence, int batch)
    {
        // State first: a terminal state is only set after the last event has been flushed.
        string state = job.State;
        bool terminal = state is "Stopped" or "Failed";
        long committed = Interlocked.Read(ref job.ByteCount);
        List<string> events = [];

        if (committed > 0 && File.Exists(job.SpoolPath))
        {
            (long sequence, long offset) = job.FindCheckpoint(afterSequence);
            using FileStream stream = new(job.SpoolPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(offset, SeekOrigin.Begin);
            using BufferedStream buffered = new(stream, 64 * 1024);
            using MemoryStream line = new();
            long position = offset;
            while (position < committed && events.Count < batch)
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
                        events.Add(System.Text.Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length));
                    }

                    line.SetLength(0);
                }
            }
        }

        long nextSequence = afterSequence + events.Count;
        bool completed = terminal && nextSequence >= Interlocked.Read(ref job.EventCount);
        return new CapturePage(job.Id, nextSequence, events, state, completed);
    }

    public async Task<OperationResult<CaptureStatus>> AttachAsync(
        AttachSessionRequest request,
        CancellationToken cancellationToken)
    {
        string name = request.SessionName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return OperationResult<CaptureStatus>.Fail("invalid_request", "sessionName is required.");
        }

        if (name.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<CaptureStatus>.Fail(
                "invalid_request",
                "Vestigo-owned sessions cannot be attached; use read_capture on the owning capture.");
        }

        if (!IsAttachable(name))
        {
            return OperationResult<CaptureStatus>.Fail(
                "attach_not_allowed",
                "The session is not matched by Vestigo:AttachableSessions.");
        }

        IReadOnlyList<string> existing;
        try
        {
            existing = _sessions.ListSessions();
        }
        catch (PlatformNotSupportedException ex)
        {
            return OperationResult<CaptureStatus>.Fail("windows_required", ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OperationResult<CaptureStatus>.Fail("session_query_failed", $"{ex.GetType().Name}: {ex.Message}");
        }

        string? canonical = existing.FirstOrDefault(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
        {
            return OperationResult<CaptureStatus>.Fail("session_not_found", "No running ETW session has that name.");
        }

        OperationResult<DecodingSourceSet> loaded = _symbols.LoadExplicit(request.SymbolPaths);
        if (!loaded.Success)
        {
            return OperationResult<CaptureStatus>.Fail(loaded.ErrorCode!, loaded.Error!);
        }

        TimeSpan duration = request.Duration is null
            ? _options.MaxCaptureDuration
            : TimeSpan.FromTicks(Math.Min(request.Duration.Value.Ticks, _options.MaxCaptureDuration.Ticks));
        if (duration <= TimeSpan.Zero)
        {
            return OperationResult<CaptureStatus>.Fail("invalid_request", "Capture duration must be positive.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int active = _jobs.Values.Count(candidate => candidate.State is "Running" or "Starting");
            if (active >= _options.MaxConcurrentCaptures)
            {
                return OperationResult<CaptureStatus>.Fail("too_many_captures", "The concurrent capture limit has been reached.");
            }

            string id = Guid.NewGuid().ToString("N")[..12];
            CaptureJob job = new(id, canonical, Path.Combine(VestigoPaths.SpoolDirectory, id + ".ndjson"), AttachedMode);
            try
            {
                job.Session = _sessions.Attach(canonical);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return OperationResult<CaptureStatus>.Fail("attach_failed", $"{ex.GetType().Name}: {ex.Message}");
            }

            _jobs[id] = job;
            job.State = "Running";
            job.Reader = Task.Run(() => ReadLoopAsync(job, loaded.Value!, duration), CancellationToken.None);
            _logger.LogInformation("Attached capture {CaptureId} to external ETW session", job.Id);
            return OperationResult<CaptureStatus>.Ok(job.ToStatus());
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsAttachable(string name) =>
        _options.AttachableSessions.Any(pattern =>
            System.Text.RegularExpressions.Regex.IsMatch(
                name,
                "^" + System.Text.RegularExpressions.Regex.Escape(pattern.Trim()).Replace("\\*", ".*", StringComparison.Ordinal) + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant));

    public Task<OperationResult<CaptureStatus>> StopAsync(string id) => StopAsync(id, "stopped");

    public void CleanupExpired()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - _options.SpoolRetention;
        foreach (CaptureJob job in _jobs.Values.ToArray())
        {
            if (job.StoppedAt is DateTimeOffset stopped && stopped < cutoff && job.State is "Stopped" or "Failed")
            {
                DeleteSpool(job);
                _jobs.TryRemove(job.Id, out _);
            }
        }

        if (Directory.Exists(VestigoPaths.SpoolDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(VestigoPaths.SpoolDirectory, "*.ndjson"))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                if (_jobs.ContainsKey(id))
                {
                    continue;
                }

                if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                {
                    TryDelete(file);
                }
            }
        }
    }

    public void Dispose()
    {
        _retentionTimer?.Dispose();
        _gate.Dispose();
    }

    private async Task RecoverOwnedSessionsAsync()
    {
        foreach (string sessionName in LoadOwnedSessions())
        {
            if (!sessionName.StartsWith(SessionPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                _sessions.StopOrphan(sessionName);
                _unstoppedOrphans.TryRemove(sessionName, out _);
                _logger.LogInformation("Stopped orphan ETW session {SessionName}", sessionName);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _unstoppedOrphans[sessionName] = 0;
                _logger.LogWarning(ex, "Could not stop orphan ETW session {SessionName}", sessionName);
            }
        }

        SaveOwnedSessions();
    }

    private async Task ReadLoopAsync(CaptureJob job, DecodingSourceSet sources, TimeSpan duration)
    {
        using CancellationTokenSource durationSource = new(duration);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, durationSource.Token);
        try
        {
            await using FileStream stream = new(job.SpoolPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await foreach (ReadOnlyMemory<byte> item in job.Session!.ReadEventsAsync(
                               sources.Context,
                               sources.CanRewriteProviderNames,
                               () => Interlocked.Increment(ref job.MissingFormatCount),
                               linked.Token))
            {
                // Write exact bytes plus '\n' so offsets are precise, then publish the counters.
                await stream.WriteAsync(item, CancellationToken.None);
                stream.WriteByte((byte)'\n');
                await stream.FlushAsync(CancellationToken.None);
                long events = Interlocked.Increment(ref job.EventCount);
                long bytes = Interlocked.Add(ref job.ByteCount, item.Length + 1);
                if (events % CheckpointInterval == 0)
                {
                    job.AddCheckpoint(bytes);
                }

                job.Notify();
                if (events >= _options.MaxEvents || bytes >= _options.MaxOutputBytes)
                {
                    job.StopReason = "limit_reached";
                    break;
                }
            }

            job.StopReason ??= linked.IsCancellationRequested && !job.Cancellation.IsCancellationRequested
                ? "duration_elapsed"
                : "completed";
            job.State = "Stopped";
        }
        catch (OperationCanceledException)
        {
            job.StopReason ??= "stopped";
            job.State = "Stopped";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            job.State = "Failed";
            job.Error = ex.GetType().Name + ": " + ex.Message;
            job.StopReason = "failed";
            _logger.LogWarning(ex, "Capture {CaptureId} failed", job.Id);
        }
        finally
        {
            job.StoppedAt = DateTimeOffset.UtcNow;
            if (job.Session is not null)
            {
                await job.Session.DisposeAsync().ConfigureAwait(false);
            }

            SaveOwnedSessions();
            job.Notify();
        }
    }

    private async Task<OperationResult<CaptureStatus>> StopAsync(string id, string reason)
    {
        if (!_jobs.TryGetValue(id, out CaptureJob? job))
        {
            return OperationResult<CaptureStatus>.Fail("not_found", "Capture was not found.");
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
                job.Error ??= "Timed out waiting for the capture reader to stop.";
            }
        }

        return OperationResult<CaptureStatus>.Ok(job.ToStatus());
    }

    private void SaveOwnedSessions() =>
        SaveSessionNames(_jobs.Values
            .Where(job => job.Mode == ManagedMode && job.State is "Starting" or "Running")
            .Select(job => job.SessionName)
            .Concat(_unstoppedOrphans.Keys));

    private static IReadOnlyList<string> LoadOwnedSessions()
    {
        if (!File.Exists(VestigoPaths.OwnedSessionsPath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(VestigoPaths.OwnedSessionsPath)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void SaveSessionNames(IEnumerable<string> names)
    {
        lock (_sessionFileLock)
        {
            string[] payload = names
                .Where(name => name.StartsWith(SessionPrefix, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            string tempPath = Path.Combine(
                VestigoPaths.SettingsDirectory,
                $".owned-sessions.{Guid.NewGuid():N}.tmp");
            try
            {
                StateDirectories.EnsureSubdirectory(VestigoPaths.SettingsDirectory);
                File.WriteAllText(tempPath, JsonSerializer.Serialize(payload));
                if (File.Exists(VestigoPaths.OwnedSessionsPath))
                {
                    File.Replace(tempPath, VestigoPaths.OwnedSessionsPath, null);
                }
                else
                {
                    File.Move(tempPath, VestigoPaths.OwnedSessionsPath);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Could not save owned ETW sessions");
            }
            finally
            {
                TryDelete(tempPath);
            }
        }
    }

    private static void DeleteSpool(CaptureJob job) => TryDelete(job.SpoolPath);

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

    private static string ClassifyStartError(Exception exception)
    {
        string name = exception.GetType().Name;
        if (name.Contains("StartTrace", StringComparison.Ordinal) ||
            exception.Message.Contains("ACCESS_DENIED", StringComparison.OrdinalIgnoreCase))
        {
            return "elevation_required";
        }

        if (exception.Message.Contains("ALREADY_EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            return "session_exists";
        }

        return "capture_failed";
    }

    private sealed class CaptureJob(string id, string sessionName, string spoolPath, string mode = ManagedMode)
    {
        private readonly object _checkpointLock = new();
        private readonly List<long> _checkpoints = [];
        private TaskCompletionSource _signal = NewSignal();

        public string Mode { get; } = mode;

        /// <summary>Completes when new events are written or the job ends.</summary>
        public TaskCompletionSource Signal => Volatile.Read(ref _signal);

        public void Notify() => Interlocked.Exchange(ref _signal, NewSignal()).TrySetResult();

        /// <summary>Records the byte offset that follows event number (n+1)*CheckpointInterval.</summary>
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

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id { get; } = id;

        public string SessionName { get; } = sessionName;

        public string SpoolPath { get; } = spoolPath;

        public string State { get; set; } = "Starting";

        public long EventCount;

        public long ByteCount;

        public int MissingFormatCount;

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public DateTimeOffset? StoppedAt { get; set; }

        public string? StopReason { get; set; }

        public string? Error { get; set; }

        public IRealtimeTraceSession? Session { get; set; }

        public Task? Reader { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();

        public CaptureStatus ToStatus() => new(
            Id,
            SessionName,
            State,
            EventCount,
            ByteCount,
            MissingFormatCount,
            StartedAt,
            StoppedAt,
            StopReason,
            Error,
            Mode);
    }
}
