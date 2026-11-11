using System.Text.Json;

using Vestigo.Configuration;
using Vestigo.Processes;

namespace Vestigo.Desktop;

/// <summary>
///     Creates and tracks named test desktops in the interactive window station. The original input desktop is recorded
///     before any explicit activation. Lease expiry, cancellation, service stop, and the next startup restore that
///     input desktop, then stop only processes this session launched, then release this session's handles.
///     A <c>fail-if-running</c> close refuses before changing the input desktop.
/// </summary>
public sealed class ManagedDesktopSessionManager : IHostedService, IManagedDesktopDirectory, IDisposable
{
    private const int CheckpointVersion = 1;

    private const string RetentionMessage = "Windows kept the desktop because another handle or thread still uses it.";

    private DateTimeOffset _lastPresenceCheck;

    private readonly IDesktopHostClient _host;
    private readonly IOwnedProcessCleanup _cleanup;
    private readonly VestigoOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ManagedDesktopSessionManager>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ManagedDesktopSession> _sessions = new(StringComparer.Ordinal);
    private readonly object _sweeps = new();
    private readonly List<Task> _sweepTasks = [];
    private readonly CancellationTokenSource _sweepCancellation = new();
    private bool _checkpointUnsupported;
    private Timer? _timer;
    private int _lifetime;

    public ManagedDesktopSessionManager(
        IDesktopHostClient host,
        IOwnedProcessCleanup cleanup,
        VestigoOptions options,
        TimeProvider time,
        ILogger<ManagedDesktopSessionManager>? logger = null)
    {
        _host = host;
        _cleanup = cleanup;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Load();
        if (!_checkpointUnsupported)
        {
            foreach (ManagedDesktopSession session in _sessions.Values.Where(session => session.State is "open" or "active" or "recovery-failed").ToList())
            {
                await RecoverStoredAsync(session, CancellationToken.None).ConfigureAwait(false);
            }
        }

        _timer = new Timer(_ => QueueSweep(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    internal void QueueSweep()
    {
        lock (_sweeps)
        {
            ObserveCompletedSweeps();
            _sweepTasks.Add(SweepAsync());
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!TryOwnLifetime())
        {
            return;
        }

        try
        {
            if (_timer is not null)
            {
                await _timer.DisposeAsync().ConfigureAwait(false);
                _timer = null;
            }

            _sweepCancellation.Cancel();
            try
            {
                await Task.WhenAll(ActiveSweeps()).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger?.LogWarning(ex, "A desktop presence sweep did not finish cleanly.");
            }

            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                foreach (string id in _sessions.Values.Where(session => session.State is "open" or "active").Select(session => session.Id).ToList())
                {
                    await CloseCoreAsync(id, DesktopSessionPolicy.TerminateOwned, automatic: true, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }

            await _host.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Dispose();
            _sweepCancellation.Dispose();
        }
    }

    public void Dispose()
    {
        if (!TryOwnLifetime())
        {
            return;
        }

        _sweepCancellation.Cancel();
        _timer?.Dispose();
        _gate.Dispose();
        _sweepCancellation.Dispose();
    }

    private bool TryOwnLifetime() => Interlocked.CompareExchange(ref _lifetime, 1, 0) == 0;

    public async Task<OperationResult<ManagedDesktopSession>> CreateAsync(string? cleanupPolicy, CancellationToken cancellationToken)
    {
        if (!_options.Desktop.AllowDesktopSessions)
        {
            return Disabled();
        }

        string? policy = DesktopSessionPolicy.NormalizeCleanup(cleanupPolicy);
        if (policy is null)
        {
            return OperationResult<ManagedDesktopSession>.Fail(
                "invalid_request",
                "cleanupPolicy must be 'terminate-owned' or 'fail-if-running'.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? createdName = null;
        bool held = true;
        try
        {
            if (_checkpointUnsupported)
            {
                return OperationResult<ManagedDesktopSession>.Fail(
                    "desktop_checkpoint_unsupported",
                    "owned-desktops.json is from a newer Vestigo and was left untouched. New test desktops are refused until it is removed by that version.");
            }

            if (_sessions.Values.Count(session => session.State is "open" or "active") >= _options.Desktop.MaxDesktopSessions)
            {
                return OperationResult<ManagedDesktopSession>.Fail(
                    "desktop_session_limit",
                    $"At most {_options.Desktop.MaxDesktopSessions} managed desktops can be open.");
            }

            string id = Guid.NewGuid().ToString("N")[..12];
            createdName = DesktopSessionPolicy.Prefix + id;
            OperationResult<DesktopCommandResult> created = await _host.SendAsync(
                new DesktopCommand(id, DesktopHostOperations.Create, id, createdName, policy),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!created.Success || created.Value is not { Success: true } value)
            {
                createdName = null;
                return created.Success
                    ? OperationResult<ManagedDesktopSession>.Fail(created.Value?.ErrorCode ?? "desktop_unavailable", created.Value?.Error ?? "The desktop host rejected the desktop.")
                    : created.FailAs<ManagedDesktopSession>();
            }

            ManagedDesktopSession session = new(
                id,
                value.DesktopName ?? createdName,
                value.WindowStation ?? DesktopSessionPolicy.WindowStation,
                value.WindowsSessionId,
                value.OriginalInputDesktop,
                "open",
                policy,
                _time.GetUtcNow(),
                _time.GetUtcNow() + _options.Desktop.DesktopSessionLease,
                false,
                true,
                false,
                [],
                null);
            _sessions[id] = session;
            Save();
            createdName = null;
            return OperationResult<ManagedDesktopSession>.Ok(session);
        }
        catch (OperationCanceledException)
        {
            if (createdName is not null)
            {
                await _host.SendAsync(new DesktopCommand(Guid.NewGuid().ToString("N"), DesktopHostOperations.Close, null, createdName), CancellationToken.None).ConfigureAwait(false);
            }

            return OperationResult<ManagedDesktopSession>.Fail("cancelled", "Desktop creation was cancelled. Any desktop created for this call was closed.");
        }
        finally
        {
            if (held)
            {
                _gate.Release();
            }
        }
    }

    public async Task<OperationResult<IReadOnlyList<ManagedDesktopSession>>> ListAsync(CancellationToken cancellationToken = default)
    {
        await ReconcilePresenceAsync(cancellationToken, throttle: false).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return OperationResult<IReadOnlyList<ManagedDesktopSession>>.Ok(_sessions.Values.OrderBy(session => session.CreatedAt).ToList());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult<ManagedDesktopSession>> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await ReconcilePresenceAsync(cancellationToken, throttle: false).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _sessions.TryGetValue(id, out ManagedDesktopSession? session)
                ? OperationResult<ManagedDesktopSession>.Ok(session)
                : OperationResult<ManagedDesktopSession>.Fail("desktop_session_not_found", "Managed desktop session was not found.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult<WindowStationDesktopList>> EnumerateAsync(CancellationToken cancellationToken)
    {
        OperationResult<DesktopCommandResult> listed = await _host.SendAsync(
            new DesktopCommand(Guid.NewGuid().ToString("N"), DesktopHostOperations.Enumerate),
            cancellationToken).ConfigureAwait(false);
        if (!listed.Success || listed.Value is not { Success: true } value)
        {
            return listed.Success
                ? OperationResult<WindowStationDesktopList>.Fail(listed.Value?.ErrorCode ?? "desktop_unavailable", listed.Value?.Error ?? "The desktop host could not list desktops.")
                : listed.FailAs<WindowStationDesktopList>();
        }

        return OperationResult<WindowStationDesktopList>.Ok(new WindowStationDesktopList(
            value.WindowStation ?? DesktopSessionPolicy.WindowStation,
            value.InputDesktop,
            value.Desktops ?? [],
            value.WindowsSessionId));
    }

    public Task<OperationResult<ManagedDesktopSession>> ActivateAsync(string id, CancellationToken cancellationToken) =>
        SwitchAsync(id, DesktopHostOperations.Activate, cancellationToken);

    public Task<OperationResult<ManagedDesktopSession>> RestoreAsync(string id, CancellationToken cancellationToken) =>
        SwitchAsync(id, DesktopHostOperations.Restore, cancellationToken);

    public async Task<OperationResult<ManagedDesktopSession>> CloseAsync(string id, string? cleanupPolicy, CancellationToken cancellationToken)
    {
        if (!_options.Desktop.AllowDesktopSessions)
        {
            return Disabled();
        }

        string? policy = string.IsNullOrWhiteSpace(cleanupPolicy) ? null : DesktopSessionPolicy.NormalizeCleanup(cleanupPolicy);
        if (cleanupPolicy is not null && policy is null)
        {
            return OperationResult<ManagedDesktopSession>.Fail(
                "invalid_request",
                "cleanupPolicy must be 'terminate-owned' or 'fail-if-running'.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(id, out ManagedDesktopSession? session))
            {
                return OperationResult<ManagedDesktopSession>.Fail("desktop_session_not_found", "Managed desktop session was not found.");
            }

            return await CloseCoreAsync(id, policy ?? session.CleanupPolicy, automatic: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult<DesktopHelperTarget>> DescribeAsync(string sessionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(sessionId, out ManagedDesktopSession? session) || session.State is not ("open" or "active"))
            {
                return OperationResult<DesktopHelperTarget>.Fail("desktop_session_not_found", "Managed desktop session was not found or is no longer open.");
            }

            return OperationResult<DesktopHelperTarget>.Ok(new DesktopHelperTarget(
                session.Id,
                DesktopSessionPolicy.DesktopPath(session.DesktopName),
                session.DesktopName,
                session.InputSwitched));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult<string?>> InputDesktopPathAsync(CancellationToken cancellationToken)
    {
        // Hold the session lock across the host query so an activate or restore cannot switch desktops between the
        // answer and the match. The host's live answer decides which desktop is active; the cached InputSwitched flag can be stale.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            OperationResult<WindowStationDesktopList> listed = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
            if (!listed.Success)
            {
                return listed.FailAs<string?>();
            }

            string? inputName = listed.Value?.InputDesktop;
            if (string.IsNullOrWhiteSpace(inputName))
            {
                return OperationResult<string?>.Ok(null);
            }

            ManagedDesktopSession? input = _sessions.Values.FirstOrDefault(session =>
                session.State is "open" or "active" or "recovery-failed"
                && string.Equals(session.DesktopName, inputName, StringComparison.OrdinalIgnoreCase));
            return OperationResult<string?>.Ok(input is null ? null : DesktopSessionPolicy.DesktopPath(input.DesktopName));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult<ManagedDesktopLease>> LeaseForLaunchAsync(string sessionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!_sessions.TryGetValue(sessionId, out ManagedDesktopSession? session) || session.State is not ("open" or "active"))
        {
            _gate.Release();
            return OperationResult<ManagedDesktopLease>.Fail(
                "desktop_session_not_found",
                "Managed desktop session was not found or is no longer open.");
        }

        ManagedDesktopLease lease = new(
            session.Id,
            DesktopSessionPolicy.DesktopPath(session.DesktopName),
            process => Attach(session.Id, process),
            () => _gate.Release());
        return OperationResult<ManagedDesktopLease>.Ok(lease);
    }

    internal async Task SweepAsync()
    {
        CancellationToken cancellationToken = _sweepCancellation.Token;
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            DateTimeOffset now = _time.GetUtcNow();
            foreach (string id in _sessions.Values.Where(session => session.State is "open" or "active" && session.LeaseExpiresAt <= now).Select(session => session.Id).ToList())
            {
                await CloseCoreAsync(id, DesktopSessionPolicy.TerminateOwned, automatic: true, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        await ReconcilePresenceAsync(cancellationToken, throttle: true).ConfigureAwait(false);
    }

    private async Task ReconcilePresenceAsync(CancellationToken cancellationToken, bool throttle)
    {
        if (!_options.Desktop.AllowDesktopSessions)
        {
            return;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            string stamp;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                bool pending = _sessions.Values.Any(session => NeedsPresenceCheck(session) || session.State is "open" or "active");
                if (!pending || (throttle && _time.GetUtcNow() - _lastPresenceCheck < TimeSpan.FromSeconds(2)))
                {
                    return;
                }

                stamp = PresenceStamp();
            }
            finally
            {
                _gate.Release();
            }

            OperationResult<WindowStationDesktopList> listed = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!string.Equals(stamp, PresenceStamp(), StringComparison.Ordinal))
                {
                    continue;
                }

                _lastPresenceCheck = _time.GetUtcNow();
                if (!listed.Success || listed.Value is null)
                {
                    bool hostExited = listed.ErrorCode == "desktop_host_lost";
                    foreach (ManagedDesktopSession session in _sessions.Values.ToList())
                    {
                        bool open = session.State is "open" or "active";
                        if (!NeedsPresenceCheck(session) && !(hostExited && open))
                        {
                            continue;
                        }

                        bool keepOpenForCleanup = open && session.Processes.Count > 0;
                        _sessions[session.Id] = hostExited
                            ? session with
                            {
                                HandleHeld = false,
                                State = open && !keepOpenForCleanup ? "recovery-failed" : session.State,
                                DesktopPresence = "enumeration-unknown"
                            }
                            : session with { DesktopPresence = "enumeration-unknown" };
                    }

                    Save();
                    return;
                }

                HashSet<string> names = listed.Value.Desktops.Select(desktop => desktop.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (ManagedDesktopSession session in _sessions.Values.Where(NeedsPresenceCheck).ToList())
                {
                    if (listed.Value.WindowsSessionId is null
                        || session.WindowsSessionId is null
                        || session.WindowsSessionId != listed.Value.WindowsSessionId)
                    {
                        continue;
                    }

                    if (names.Contains(session.DesktopName))
                    {
                        _sessions[session.Id] = session with
                        {
                            DesktopRetained = true,
                            DesktopPresence = session.DesktopPresence == "close-time-retained" ? "cleanup-pending" : "confirmed-present"
                        };
                        continue;
                    }

                    if (session.State is "open" or "active" && !session.HandleHeld)
                    {
                        _sessions[session.Id] = session with
                        {
                            State = "recovery-failed",
                            DesktopPresence = "confirmed-absent",
                            LastError = "The managed desktop was not in the window station enumeration."
                        };
                        continue;
                    }

                    _sessions[session.Id] = session with
                    {
                        DesktopRetained = false,
                        DesktopPresence = "confirmed-absent",
                        LastError = session.LastError == RetentionMessage ? null : session.LastError
                    };
                }

                Save();
                return;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private Task[] ActiveSweeps()
    {
        lock (_sweeps)
        {
            ObserveCompletedSweeps();
            return _sweepTasks.ToArray();
        }
    }

    private void ObserveCompletedSweeps()
    {
        for (int index = _sweepTasks.Count - 1; index >= 0; index--)
        {
            Task task = _sweepTasks[index];
            if (!task.IsCompleted)
            {
                continue;
            }

            _ = task.Exception;
            _sweepTasks.RemoveAt(index);
        }
    }

    private string PresenceStamp() =>
        string.Join('\n', _sessions.Values.OrderBy(session => session.Id, StringComparer.Ordinal).Select(session =>
            string.Join('|', session.Id, session.State, session.DesktopRetained, session.DesktopPresence, session.HandleHeld, session.WindowsSessionId, session.LastError)));

    private static bool NeedsPresenceCheck(ManagedDesktopSession session) =>
        session.DesktopRetained
        || session.DesktopPresence is "close-time-retained" or "cleanup-pending" or "confirmed-present" or "enumeration-unknown"
        || (session.State is "open" or "active" && !session.HandleHeld);

    private async Task<OperationResult<ManagedDesktopSession>> SwitchAsync(string id, string operation, CancellationToken cancellationToken)
    {
        if (!_options.Desktop.AllowDesktopSessions)
        {
            return Disabled();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool switched = false;
        try
        {
            if (!_sessions.TryGetValue(id, out ManagedDesktopSession? session) || session.State is not ("open" or "active"))
            {
                return OperationResult<ManagedDesktopSession>.Fail("desktop_session_not_found", "Managed desktop session was not found or is no longer open.");
            }

            if (operation == DesktopHostOperations.Activate)
            {
                session = session with { InputSwitched = true };
                _sessions[id] = session;
                Save();
            }

            OperationResult<DesktopCommandResult> result = await _host.SendAsync(
                new DesktopCommand(Guid.NewGuid().ToString("N"), operation, session.Id, session.DesktopName, session.CleanupPolicy, session.OriginalInputDesktop),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Success || result.Value is not { Success: true } value)
            {
                return result.Success
                    ? OperationResult<ManagedDesktopSession>.Fail(result.Value?.ErrorCode ?? "desktop_unavailable", result.Value?.Error ?? "The desktop host rejected the switch.")
                    : result.FailAs<ManagedDesktopSession>();
            }

            switched = operation == DesktopHostOperations.Activate && value.Activated;
            if (switched)
            {
                foreach (ManagedDesktopSession other in _sessions.Values.Where(item => item.InputSwitched && item.Id != id).ToList())
                {
                    _sessions[other.Id] = other with { InputSwitched = false, State = other.State == "active" ? "open" : other.State };
                }
            }

            ManagedDesktopSession updated = session with
            {
                State = switched ? "active" : operation == DesktopHostOperations.Restore ? "open" : session.State,
                InputSwitched = switched,
                OriginalInputDesktop = value.OriginalInputDesktop ?? session.OriginalInputDesktop,
                WindowsSessionId = value.WindowsSessionId ?? session.WindowsSessionId,
                LastError = null
            };
            _sessions[id] = updated;
            Save();
            return OperationResult<ManagedDesktopSession>.Ok(updated);
        }
        catch (OperationCanceledException)
        {
            if (switched || operation == DesktopHostOperations.Activate)
            {
                await RestoreAfterCancelAsync(id).ConfigureAwait(false);
            }

            return OperationResult<ManagedDesktopSession>.Fail("cancelled", "The desktop switch was cancelled. Restore was attempted.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RestoreAfterCancelAsync(string id)
    {
        if (!_sessions.TryGetValue(id, out ManagedDesktopSession? session))
        {
            return;
        }

        OperationResult<DesktopCommandResult> restored = await _host.SendAsync(
            new DesktopCommand(Guid.NewGuid().ToString("N"), DesktopHostOperations.Restore, session.Id, session.DesktopName, null, session.OriginalInputDesktop),
            CancellationToken.None).ConfigureAwait(false);
        if (Accepted(restored))
        {
            _sessions[id] = session with { State = "open", InputSwitched = false };
            Save();
        }
    }

    private async Task<OperationResult<ManagedDesktopSession>> CloseCoreAsync(string id, string policy, bool automatic, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(id, out ManagedDesktopSession? session) || session.State is "closed")
        {
            return OperationResult<ManagedDesktopSession>.Fail("desktop_session_not_found", "Managed desktop session was not found or is already closed.");
        }

        string chosen = automatic ? DesktopSessionPolicy.TerminateOwned : policy;
        if (chosen == DesktopSessionPolicy.FailIfRunning)
        {
            IReadOnlyList<int> running = _cleanup.StillRunning(session.Processes);
            if (running.Count > 0)
            {
                return OperationResult<ManagedDesktopSession>.Fail(
                    "processes_still_running",
                    "Owned processes are still running: " + string.Join(", ", running) + ". The input desktop was not changed.");
            }
        }

        if (session.InputSwitched)
        {
            OperationResult<DesktopCommandResult> restored = await _host.SendAsync(
                new DesktopCommand(Guid.NewGuid().ToString("N"), DesktopHostOperations.Restore, session.Id, session.DesktopName, null, session.OriginalInputDesktop),
                cancellationToken).ConfigureAwait(false);
            bool moved = restored.Value?.ErrorCode == "input_desktop_changed" || restored.ErrorCode == "input_desktop_changed";
            if (!Accepted(restored) && !moved)
            {
                bool lost = restored.ErrorCode is "desktop_host_lost" or "desktop_session_not_found"
                    || restored.Value?.ErrorCode is "desktop_host_lost" or "desktop_session_not_found";
                if (automatic || lost)
                {
                    OperationResult<DesktopCommandResult> recovered = await _host.RecoverAsync(
                        new DesktopCommand(Guid.NewGuid().ToString("N"), DesktopHostOperations.RestorePhase, session.Id, session.DesktopName, null, session.OriginalInputDesktop),
                        cancellationToken).ConfigureAwait(false);
                    bool recoveredMove = recovered.Value?.ErrorCode == "input_desktop_changed" || recovered.ErrorCode == "input_desktop_changed";
                    if (!Accepted(recovered) && !recoveredMove)
                    {
                        ManagedDesktopSession failed = session with
                        {
                            State = "recovery-failed",
                            InputSwitched = true,
                            LastError = recovered.Value?.Error ?? recovered.Error ?? restored.Value?.Error ?? restored.Error
                        };
                        _sessions[id] = failed;
                        Save();
                        return HostFailure(recovered.Success ? recovered : restored.Success ? restored : recovered);
                    }
                }
                else
                {
                    ManagedDesktopSession failed = session with { LastError = restored.Value?.Error ?? restored.Error };
                    _sessions[id] = failed;
                    Save();
                    return HostFailure(restored);
                }
            }
        }

        if (chosen == DesktopSessionPolicy.TerminateOwned)
        {
            await _cleanup.TerminateAsync(session.Processes, cancellationToken).ConfigureAwait(false);
        }

        OperationResult<DesktopCommandResult> closed = await _host.SendAsync(
            new DesktopCommand(Guid.NewGuid().ToString("N"), DesktopHostOperations.Close, session.Id, session.DesktopName, chosen, session.OriginalInputDesktop),
            cancellationToken).ConfigureAwait(false);
        bool accepted = Accepted(closed);
        bool retained = closed.Value?.DesktopRetained == true;
        bool released = accepted && closed.Value?.HandleReleased != false;
        ManagedDesktopSession updated = session with
        {
            State = "closed",
            InputSwitched = false,
            HandleHeld = !released,
            DesktopRetained = retained,
            DesktopPresence = !accepted ? "enumeration-unknown" : retained ? "close-time-retained" : "confirmed-absent",
            LastError = accepted ? retained ? RetentionMessage : null : closed.Value?.Error ?? closed.Error
        };
        _sessions[id] = updated;
        Save();
        if (!accepted)
        {
            return OperationResult<ManagedDesktopSession>.Fail(closed.Value?.ErrorCode ?? closed.ErrorCode ?? "desktop_unavailable", closed.Value?.Error ?? closed.Error ?? "The desktop host could not close the session.");
        }

        if (_sessions.Values.All(item => item.State == "closed"))
        {
            await _host.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return OperationResult<ManagedDesktopSession>.Ok(updated);
    }

    private async Task RecoverStoredAsync(ManagedDesktopSession session, CancellationToken cancellationToken)
    {
        try
        {
            if (session.InputSwitched)
            {
                OperationResult<DesktopCommandResult> restored = await _host.RecoverAsync(
                    new DesktopCommand(session.Id + "-restore", DesktopHostOperations.RestorePhase, session.Id, session.DesktopName, null, session.OriginalInputDesktop),
                    cancellationToken).ConfigureAwait(false);
                bool moved = restored.Value?.ErrorCode == "input_desktop_changed" || restored.ErrorCode == "input_desktop_changed";
                if (!Accepted(restored) && !moved)
                {
                    _sessions[session.Id] = session with
                    {
                        State = "recovery-failed",
                        InputSwitched = true,
                        LastError = restored.Value?.Error ?? restored.Error
                    };
                    Save();
                    return;
                }
            }

            await _cleanup.TerminateAsync(session.Processes, cancellationToken).ConfigureAwait(false);
            OperationResult<DesktopCommandResult> closed = await _host.RecoverAsync(
                new DesktopCommand(session.Id + "-close", DesktopHostOperations.ClosePhase, session.Id, session.DesktopName, null, session.OriginalInputDesktop),
                cancellationToken).ConfigureAwait(false);
            _sessions[session.Id] = session with
            {
                State = Accepted(closed) ? "closed" : "recovery-failed",
                InputSwitched = false,
                HandleHeld = false,
                DesktopRetained = closed.Value?.DesktopRetained == true,
                DesktopPresence = !Accepted(closed)
                    ? "enumeration-unknown"
                    : closed.Value?.DesktopRetained == true ? "close-time-retained" : "confirmed-absent",
                LastError = Accepted(closed)
                    ? closed.Value?.DesktopRetained == true ? RetentionMessage : null
                    : closed.Value?.Error ?? closed.Error
            };
            Save();
        }
        catch (Exception ex) when (ex is IOException or ProcessLaunchException or InvalidOperationException)
        {
            _logger?.LogWarning(ex, "Could not recover managed desktop session {SessionId}.", session.Id);
            _sessions[session.Id] = session with { State = "recovery-failed", LastError = ex.Message };
            Save();
        }
    }

    private void Attach(string sessionId, OwnedDesktopProcess process)
    {
        if (!_sessions.TryGetValue(sessionId, out ManagedDesktopSession? session))
        {
            return;
        }

        if (session.Processes.Any(item => item.ProcessId == process.ProcessId && item.JobId == process.JobId))
        {
            return;
        }

        _sessions[sessionId] = session with { Processes = [..session.Processes, process] };
        Save();
    }

    private static bool Accepted(OperationResult<DesktopCommandResult> result) =>
        result.Success && result.Value is { Success: true };

    private static OperationResult<ManagedDesktopSession> HostFailure(OperationResult<DesktopCommandResult> result)
    {
        if (result.Value is { Success: false } value)
        {
            return OperationResult<ManagedDesktopSession>.Fail(
                value.ErrorCode ?? "desktop_unavailable",
                value.Error ?? "The desktop host rejected the command.");
        }

        return result.FailAs<ManagedDesktopSession>();
    }

    private OperationResult<ManagedDesktopSession> Disabled() =>
        OperationResult<ManagedDesktopSession>.Fail(
            "desktop_sessions_disabled",
            "Managed desktops are disabled. Set Vestigo:Desktop:AllowDesktopSessions to true.");

    private void Load()
    {
        if (!File.Exists(VestigoPaths.OwnedDesktopsPath))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(VestigoPaths.OwnedDesktopsPath));
            if (!document.RootElement.TryGetProperty("version", out JsonElement version) || version.GetInt32() != CheckpointVersion)
            {
                _checkpointUnsupported = true;
                _logger?.LogWarning("Leaving {Path} in place because its version is not supported.", VestigoPaths.OwnedDesktopsPath);
                return;
            }

            StoredDesktops? stored = document.Deserialize<StoredDesktops>(SessionHelper.Json);
            if (stored?.Sessions is null)
            {
                return;
            }

            foreach (ManagedDesktopSession session in stored.Sessions)
            {
                _sessions[session.Id] = session;
            }
        }
        catch (JsonException ex)
        {
            _checkpointUnsupported = true;
            _logger?.LogWarning(ex, "Leaving {Path} in place because it is not valid JSON.", VestigoPaths.OwnedDesktopsPath);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(VestigoPaths.SettingsDirectory);
        StoredDesktops stored = new(CheckpointVersion, _sessions.Values.OrderBy(session => session.Id).ToList());
        string temp = VestigoPaths.OwnedDesktopsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(stored, SessionHelper.Json));
        if (File.Exists(VestigoPaths.OwnedDesktopsPath))
        {
            File.Replace(temp, VestigoPaths.OwnedDesktopsPath, null);
        }
        else
        {
            File.Move(temp, VestigoPaths.OwnedDesktopsPath);
        }
    }

    private sealed record StoredDesktops(int Version, IReadOnlyList<ManagedDesktopSession> Sessions);
}

/// <summary>Desktops on the interactive window station, classified without opening a secure desktop.</summary>
public sealed record WindowStationDesktopList(
    string WindowStation,
    string? InputDesktop,
    IReadOnlyList<EnumeratedDesktop> Desktops,
    int? WindowsSessionId = null);
