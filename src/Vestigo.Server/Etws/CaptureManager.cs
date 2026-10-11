using System.Collections.Concurrent;
using System.Text.Json;

using Nefarius.Utilities.ETW.Deserializer.WPP;
using Nefarius.Utilities.ETW.Symbols;

using Vestigo.Configuration;

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
    string? Error);

/// <summary>One page of spooled NDJSON events.</summary>
public sealed record CapturePage(string Id, long NextSequence, IReadOnlyList<string> Events);

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

    private readonly VestigoOptions _options;
    private readonly SymbolCatalogService _symbols;
    private readonly IRealtimeTraceSessionFactory _sessions;
    private readonly ILogger<CaptureManager> _logger;
    private readonly ConcurrentDictionary<string, CaptureJob> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
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
        Directory.CreateDirectory(VestigoPaths.SpoolDirectory);
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
        if (!Enum.TryParse(request.Level, ignoreCase: true, out TraceLevelSelection level))
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

    public OperationResult<CapturePage> Read(string id, long afterSequence, int? limit)
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
        List<string> events = [];
        long sequence = 0;
        if (File.Exists(job.SpoolPath))
        {
            using FileStream stream = new(job.SpoolPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream);
            while (reader.ReadLine() is string line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                sequence++;
                if (sequence <= afterSequence)
                {
                    continue;
                }

                events.Add(line);
                if (events.Count >= batch)
                {
                    break;
                }
            }
        }

        return OperationResult<CapturePage>.Ok(new CapturePage(id, afterSequence + events.Count, events));
    }

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
                _logger.LogInformation("Stopped orphan ETW session {SessionName}", sessionName);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Could not stop orphan ETW session {SessionName}", sessionName);
            }
        }

        SaveSessionNames([]);
    }

    private async Task ReadLoopAsync(CaptureJob job, DecodingSourceSet sources, TimeSpan duration)
    {
        using CancellationTokenSource durationSource = new(duration);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, durationSource.Token);
        try
        {
            await using FileStream stream = new(job.SpoolPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using StreamWriter writer = new(stream);
            await foreach (ReadOnlyMemory<byte> item in job.Session!.ReadEventsAsync(
                               sources.Context,
                               sources.CanRewriteProviderNames,
                               () => Interlocked.Increment(ref job.MissingFormatCount),
                               linked.Token))
            {
                if (job.EventCount >= _options.MaxEvents || job.ByteCount >= _options.MaxOutputBytes)
                {
                    job.StopReason = "limit_reached";
                    break;
                }

                string text = System.Text.Encoding.UTF8.GetString(item.Span);
                await writer.WriteLineAsync(text);
                await writer.FlushAsync();
                job.EventCount++;
                job.ByteCount += item.Length + 1;
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
        SaveSessionNames(_jobs.Values.Where(job => job.State is "Starting" or "Running").Select(job => job.SessionName));

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

    private static void SaveSessionNames(IEnumerable<string> names)
    {
        Directory.CreateDirectory(VestigoPaths.SettingsDirectory);
        File.WriteAllText(
            VestigoPaths.OwnedSessionsPath,
            JsonSerializer.Serialize(names.Where(name => name.StartsWith(SessionPrefix, StringComparison.Ordinal)).Distinct().ToArray()));
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

    private sealed class CaptureJob(string id, string sessionName, string spoolPath)
    {
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
            Error);
    }
}
