using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

using Vestigo.Configuration;
using Vestigo.Security;
using Vestigo.Spooling;

namespace Vestigo.DebugOutput;

/// <summary>Status of one managed debug-output capture.</summary>
public sealed record DebugCaptureStatus(
    string Id,
    string State,
    long MessageCount,
    long ByteCount,
    DateTimeOffset StartedAt,
    DateTimeOffset? StoppedAt,
    string? StopReason,
    string? Error,
    int? ProcessId);

/// <summary>One page of spooled NDJSON debug messages. <c>Completed</c> means no more events will ever follow.</summary>
public sealed record DebugCapturePage(
    string Id,
    long NextSequence,
    IReadOnlyList<string> Events,
    string State,
    bool Completed);

/// <summary>Arguments for starting a debug-output capture.</summary>
public sealed record StartDebugCaptureRequest(
    IReadOnlyList<string> IncludeMessages,
    IReadOnlyList<string> ExcludeMessages,
    IReadOnlyList<string> IncludeProcesses,
    IReadOnlyList<string> ExcludeProcesses,
    TimeSpan? Duration);

/// <summary>Read-time filters applied while paging a capture spool.</summary>
public sealed record DebugCaptureReadFilter(
    string? MessageRegex,
    string? ExcludeRegex,
    IReadOnlyList<int>? ProcessIds,
    IReadOnlyList<string>? ProcessNames);

/// <summary>One spooled OutputDebugString event.</summary>
public sealed record DebugOutputEvent(
    DateTimeOffset ReceivedAt,
    string Time,
    int Pid,
    string ProcessName,
    string Message);

/// <summary>
///     Owns the single DebugViewConsole capture, its spool, and the recorded orphan process.
/// </summary>
public sealed class DebugOutputManager : IHostedService, IDisposable
{
    internal const int ScanMultiplier = 16;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly VestigoOptions _options;
    private readonly IDebugViewProcess _process;
    private readonly ILogger<DebugOutputManager> _logger;
    private readonly ConcurrentDictionary<string, DebugCaptureJob> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _ownedFileLock = new();
    private Timer? _retentionTimer;

    public DebugOutputManager(
        VestigoOptions options,
        IDebugViewProcess process,
        ILogger<DebugOutputManager> logger)
    {
        _options = options;
        _process = process;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        StateDirectories.EnsureSubdirectory(VestigoPaths.SpoolDirectory);
        RecoverOwnedProcess();
        CleanupExpired();
        _retentionTimer = new Timer(_ => CleanupExpired(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        await Task.CompletedTask.ConfigureAwait(false);
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

    public async Task<OperationResult<DebugCaptureStatus>> StartCaptureAsync(
        StartDebugCaptureRequest request,
        CancellationToken cancellationToken)
    {
        OperationResult<IReadOnlyList<string>> arguments = DebugViewArguments.Build(
            _options.DebugOutput,
            request.IncludeMessages,
            request.ExcludeMessages,
            request.IncludeProcesses,
            request.ExcludeProcesses);
        if (!arguments.Success)
        {
            return OperationResult<DebugCaptureStatus>.Fail(arguments.ErrorCode!, arguments.Error!);
        }

        TimeSpan duration = request.Duration is null
            ? _options.DebugOutput.MaxDuration
            : TimeSpan.FromTicks(Math.Min(request.Duration.Value.Ticks, _options.DebugOutput.MaxDuration.Ticks));
        if (duration <= TimeSpan.Zero)
        {
            return OperationResult<DebugCaptureStatus>.Fail("invalid_request", "Capture duration must be positive.");
        }

        string executable = ResolveExecutablePath();
        if (!File.Exists(executable))
        {
            return OperationResult<DebugCaptureStatus>.Fail(
                "executable_missing",
                $"DebugViewConsole was not found at '{executable}'. Reinstall Vestigo or set Vestigo:DebugOutput:ExecutablePath.");
        }

        if (!TryVerifyHash(executable, out string? hashError, out string? hashCode))
        {
            return OperationResult<DebugCaptureStatus>.Fail(hashCode!, hashError!);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_jobs.Values.Any(candidate => candidate.State is "Running" or "Starting"))
            {
                return OperationResult<DebugCaptureStatus>.Fail(
                    "capture_busy",
                    "A debug-output capture is already running. Stop it before starting another.");
            }

            string id = Guid.NewGuid().ToString("N")[..12];
            DebugCaptureJob job = new(id, Path.Combine(VestigoPaths.SpoolDirectory, "dbg-" + id + ".ndjson"));
            _jobs[id] = job;
            job.Reader = Task.Run(
                () => ReadLoopAsync(job, executable, arguments.Value!, duration),
                CancellationToken.None);
            try
            {
                await job.Started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await CleanupFailedStartAsync(job).ConfigureAwait(false);
                if (ex is OperationCanceledException)
                {
                    throw;
                }

                return OperationResult<DebugCaptureStatus>.Fail(
                    job.ErrorCode ?? "capture_failed",
                    job.Error ?? ex.GetBaseException().Message);
            }

            job.State = "Running";
            _logger.LogInformation("Started debug-output capture {CaptureId}", job.Id);
            return OperationResult<DebugCaptureStatus>.Ok(job.ToStatus());
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<DebugCaptureStatus> List() =>
        _jobs.Values.Select(job => job.ToStatus()).OrderBy(job => job.StartedAt).ToArray();

    public OperationResult<DebugCaptureStatus> Get(string id)
    {
        if (!_jobs.TryGetValue(id, out DebugCaptureJob? job))
        {
            return OperationResult<DebugCaptureStatus>.Fail("not_found", "Capture was not found.");
        }

        return OperationResult<DebugCaptureStatus>.Ok(job.ToStatus());
    }

    public async Task<OperationResult<DebugCapturePage>> ReadAsync(
        string id,
        long afterSequence,
        int? limit,
        TimeSpan wait,
        DebugCaptureReadFilter? filter,
        CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(id, out DebugCaptureJob? job))
        {
            return OperationResult<DebugCapturePage>.Fail("not_found", "Capture was not found.");
        }

        if (afterSequence < 0)
        {
            return OperationResult<DebugCapturePage>.Fail("invalid_request", "afterSequence cannot be negative.");
        }

        if (!TryCreatePredicate(filter, out Func<string, bool>? include, out string? filterError))
        {
            return OperationResult<DebugCapturePage>.Fail("invalid_request", filterError!);
        }

        int batch = Math.Clamp(limit ?? _options.MaxReadBatch, 1, _options.MaxReadBatch);
        TimeSpan effectiveWait = wait < TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(Math.Min(wait.Ticks, _options.MaxReadWait.Ticks));
        int scanLimit = include is null ? batch : batch * ScanMultiplier;
        NdjsonPage page = await job.Spool.ReadAsync(
            afterSequence,
            batch,
            () => job.State,
            effectiveWait,
            cancellationToken,
            include,
            scanLimit).ConfigureAwait(false);
        return OperationResult<DebugCapturePage>.Ok(
            new DebugCapturePage(job.Id, page.NextSequence, page.Events, page.State, page.Completed));
    }

    public Task<OperationResult<DebugCaptureStatus>> StopAsync(string id) => StopAsync(id, "stopped");

    public void CleanupExpired()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - _options.SpoolRetention;
        foreach (DebugCaptureJob job in _jobs.Values.ToArray())
        {
            if (job.StoppedAt is DateTimeOffset stopped && stopped < cutoff && job.State is "Stopped" or "Failed")
            {
                TryDelete(job.Spool.Path);
                _jobs.TryRemove(job.Id, out _);
            }
        }

        if (Directory.Exists(VestigoPaths.SpoolDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(VestigoPaths.SpoolDirectory, "dbg-*.ndjson"))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                if (id.StartsWith("dbg-", StringComparison.Ordinal))
                {
                    id = id["dbg-".Length..];
                }

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

    internal string ResolveExecutablePath() =>
        string.IsNullOrWhiteSpace(_options.DebugOutput.ExecutablePath)
            ? Path.Combine(AppContext.BaseDirectory, @"tools\DebugViewConsole.exe")
            : _options.DebugOutput.ExecutablePath;

    internal bool TryVerifyHash(string executable, out string? error, out string? code)
    {
        error = null;
        code = null;
        string? expected = _options.DebugOutput.ExpectedSha256;
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        string actual;
        try
        {
            using FileStream stream = File.OpenRead(executable);
            actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            code = "hash_mismatch";
            return false;
        }

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            error = "DebugViewConsole SHA-256 does not match Vestigo:DebugOutput:ExpectedSha256.";
            code = "hash_mismatch";
            return false;
        }

        return true;
    }

    private async Task ReadLoopAsync(
        DebugCaptureJob job,
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan duration)
    {
        using CancellationTokenSource durationSource = new(duration);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            job.Cancellation.Token,
            durationSource.Token);
        string? dbwinError = null;
        try
        {
            job.Spool.OpenWrite();
            await foreach (DebugViewLine line in _process.RunAsync(
                               executable,
                               arguments,
                               pid =>
                               {
                                   job.ProcessId = pid;
                                   SaveOwnedProcess(new OwnedDebugViewProcess(pid, job.StartedAt, executable));
                                   job.Started.TrySetResult();
                               },
                               linked.Token))
            {
                if (line.IsError)
                {
                    if (DebugViewLineParser.IsDbWinInUse(line.Text))
                    {
                        dbwinError = line.Text;
                        break;
                    }

                    if (DebugViewLineParser.IsGlobalCaptureWarning(line.Text))
                    {
                        _logger.LogWarning("{GlobalCaptureWarning}", line.Text);
                    }

                    continue;
                }

                if (!DebugViewLineParser.TryParse(line.Text, out DebugViewMessage? message) || message is null)
                {
                    continue;
                }

                DebugOutputEvent payload = new(
                    DateTimeOffset.UtcNow,
                    message.Time,
                    message.Pid,
                    message.ProcessName,
                    message.Message);
                byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
                long projected = utf8.Length + 1;
                if (Interlocked.Read(ref job.Spool.ByteCount) + projected > _options.DebugOutput.MaxOutputBytes)
                {
                    job.StopReason = "limit_reached";
                    break;
                }

                long events = await job.Spool.AppendAsync(utf8).ConfigureAwait(false);
                if (events >= _options.DebugOutput.MaxMessages ||
                    Interlocked.Read(ref job.Spool.ByteCount) >= _options.DebugOutput.MaxOutputBytes)
                {
                    job.StopReason = "limit_reached";
                    break;
                }
            }

            if (dbwinError is not null)
            {
                job.State = "Failed";
                job.Error = dbwinError;
                job.StopReason = "dbwin_in_use";
                job.ErrorCode = "dbwin_in_use";
            }
            else
            {
                job.StopReason ??= linked.IsCancellationRequested && !job.Cancellation.IsCancellationRequested
                    ? "duration_elapsed"
                    : "completed";
                job.State = "Stopped";
            }
        }
        catch (OperationCanceledException)
        {
            job.StopReason ??= durationSource.IsCancellationRequested && !job.Cancellation.IsCancellationRequested
                ? "duration_elapsed"
                : "stopped";
            job.State = "Stopped";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            job.State = "Failed";
            job.Error = ex.GetType().Name + ": " + ex.Message;
            job.StopReason = "failed";
            job.ErrorCode = "capture_failed";
            _logger.LogWarning(ex, "Debug-output capture {CaptureId} failed", job.Id);
        }
        finally
        {
            if (!job.Started.Task.IsCompleted)
            {
                job.Started.TrySetException(
                    new InvalidOperationException(job.Error ?? "DebugViewConsole did not start."));
            }

            job.StoppedAt = DateTimeOffset.UtcNow;
            await job.Spool.DisposeAsync().ConfigureAwait(false);
            ClearOwnedProcess();
            job.Spool.Notify();
        }
    }

    private async Task<OperationResult<DebugCaptureStatus>> StopAsync(string id, string reason)
    {
        if (!_jobs.TryGetValue(id, out DebugCaptureJob? job))
        {
            return OperationResult<DebugCaptureStatus>.Fail("not_found", "Capture was not found.");
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
                job.Error ??= "Timed out waiting for the debug-output reader to stop.";
                return OperationResult<DebugCaptureStatus>.Fail("stop_timeout", job.Error);
            }
        }

        return OperationResult<DebugCaptureStatus>.Ok(job.ToStatus());
    }

    private async Task CleanupFailedStartAsync(DebugCaptureJob job)
    {
        await job.Cancellation.CancelAsync().ConfigureAwait(false);
        if (job.Reader is not null)
        {
            try
            {
                await job.Reader.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                job.Error ??= "Timed out waiting for the debug-output reader to stop.";
            }
        }

        TryDelete(job.Spool.Path);
        _jobs.TryRemove(job.Id, out _);
    }

    private bool TryCreatePredicate(
        DebugCaptureReadFilter? filter,
        out Func<string, bool>? include,
        out string? error)
    {
        include = null;
        error = null;
        if (filter is null ||
            (string.IsNullOrWhiteSpace(filter.MessageRegex) &&
             string.IsNullOrWhiteSpace(filter.ExcludeRegex) &&
             (filter.ProcessIds is null || filter.ProcessIds.Count == 0) &&
             (filter.ProcessNames is null || filter.ProcessNames.Count == 0)))
        {
            return true;
        }

        Regex? message = null;
        Regex? exclude = null;
        if (!string.IsNullOrWhiteSpace(filter.MessageRegex) &&
            !TryCompile(filter.MessageRegex, out message, out error))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.ExcludeRegex) &&
            !TryCompile(filter.ExcludeRegex, out exclude, out error))
        {
            return false;
        }

        HashSet<int>? pids = filter.ProcessIds is { Count: > 0 } ? [..filter.ProcessIds] : null;
        IReadOnlyList<string>? names = filter.ProcessNames is { Count: > 0 } ? filter.ProcessNames : null;
        include = line =>
        {
            DebugOutputEvent? item;
            try
            {
                item = JsonSerializer.Deserialize<DebugOutputEvent>(line, JsonOptions);
            }
            catch (JsonException)
            {
                return false;
            }

            if (item is null)
            {
                return false;
            }

            if (message is not null && !message.IsMatch(item.Message))
            {
                return false;
            }

            if (exclude is not null && exclude.IsMatch(item.Message))
            {
                return false;
            }

            if (pids is not null && !pids.Contains(item.Pid))
            {
                return false;
            }

            if (names is not null &&
                !names.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, item.ProcessName, ignoreCase: true)))
            {
                return false;
            }

            return true;
        };
        return true;
    }

    private static bool TryCompile(string pattern, out Regex? regex, out string? error)
    {
        regex = null;
        error = null;
        try
        {
            regex = new Regex(
                pattern,
                RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void RecoverOwnedProcess()
    {
        OwnedDebugViewProcess? owned = LoadOwnedProcess();
        if (owned is null)
        {
            return;
        }

        try
        {
            if (TryGetImagePath(owned.ProcessId, out string? image) &&
                PathsMatch(image, owned.ImagePath))
            {
                using Process process = Process.GetProcessById(owned.ProcessId);
                process.Kill(entireProcessTree: true);
                _logger.LogInformation("Stopped orphan DebugViewConsole process {ProcessId}", owned.ProcessId);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Win32Exception or NotSupportedException)
        {
            _logger.LogWarning(ex, "Could not stop orphan DebugViewConsole process {ProcessId}", owned.ProcessId);
            return;
        }

        ClearOwnedProcess();
    }

    private void SaveOwnedProcess(OwnedDebugViewProcess owned)
    {
        lock (_ownedFileLock)
        {
            string tempPath = Path.Combine(
                VestigoPaths.SettingsDirectory,
                $".owned-debugview.{Guid.NewGuid():N}.tmp");
            try
            {
                StateDirectories.EnsureSubdirectory(VestigoPaths.SettingsDirectory);
                File.WriteAllText(tempPath, JsonSerializer.Serialize(owned, JsonOptions));
                if (File.Exists(VestigoPaths.OwnedDebugViewPath))
                {
                    File.Replace(tempPath, VestigoPaths.OwnedDebugViewPath, null);
                }
                else
                {
                    File.Move(tempPath, VestigoPaths.OwnedDebugViewPath);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Could not save owned DebugViewConsole process");
            }
            finally
            {
                TryDelete(tempPath);
            }
        }
    }

    private void ClearOwnedProcess()
    {
        lock (_ownedFileLock)
        {
            TryDelete(VestigoPaths.OwnedDebugViewPath);
        }
    }

    private static OwnedDebugViewProcess? LoadOwnedProcess()
    {
        if (!File.Exists(VestigoPaths.OwnedDebugViewPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OwnedDebugViewProcess>(
                File.ReadAllText(VestigoPaths.OwnedDebugViewPath),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetImagePath(int processId, out string? path)
    {
        path = null;
        try
        {
            using Process process = Process.GetProcessById(processId);
            path = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static bool PathsMatch(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
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

    private sealed record OwnedDebugViewProcess(int ProcessId, DateTimeOffset StartedAt, string ImagePath);

    private sealed class DebugCaptureJob(string id, string spoolPath)
    {
        public string Id { get; } = id;

        public NdjsonSpool Spool { get; } = new(spoolPath);

        public string State { get; set; } = "Starting";

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public DateTimeOffset? StoppedAt { get; set; }

        public string? StopReason { get; set; }

        public string? Error { get; set; }

        public string? ErrorCode { get; set; }

        public int? ProcessId { get; set; }

        public Task? Reader { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenSource Cancellation { get; } = new();

        public DebugCaptureStatus ToStatus() => new(
            Id,
            State,
            Spool.EventCount,
            Spool.ByteCount,
            StartedAt,
            StoppedAt,
            StopReason,
            Error,
            ProcessId);
    }
}
