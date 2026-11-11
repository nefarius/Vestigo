using System.Text.Json;

using Vestigo.Configuration;
using Vestigo.Processes;
using Vestigo.Security;

namespace Vestigo.Desktop;

public interface IDisplayLayoutClient
{
    /// <param name="desktopPath">Desktop the helper runs on. Null keeps <c>winsta0\default</c>.</param>
    Task<OperationResult<DisplayLayoutSnapshot>> SnapshotAsync(bool elevated, CancellationToken cancellationToken, string? desktopPath = null);

    Task<OperationResult<DisplayLayoutApplyResult>> ApplyAsync(DisplayLayoutRequest request, bool elevated, CancellationToken cancellationToken, string? desktopPath = null);
}

/// <summary>
///     One display-layout transaction at a time. The original snapshot is stored before a change is applied.
///     Cancellation of the start call and service shutdown attempt a restore. A stateless MCP disconnect does not;
///     the lease does. Startup retries a checkpoint that is still present. External topology changes are reported,
///     not prevented.
/// </summary>
public sealed class DisplayLayoutTransactionManager : IHostedService, IDisposable
{
    private const int CheckpointVersion = 1;
    private const int AccessDenied = 5;

    private readonly IDisplayLayoutClient _client;
    private readonly VestigoOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<DisplayLayoutTransactionManager>? _logger;
    private readonly IManagedDesktopDirectory? _desktops;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DisplayLayoutTransaction? _current;
    private bool _elevated;
    private DateTimeOffset _lastRestoreAttempt = DateTimeOffset.MinValue;
    private Timer? _timer;

    public DisplayLayoutTransactionManager(
        IDisplayLayoutClient client,
        VestigoOptions options,
        TimeProvider time,
        IManagedDesktopDirectory? desktops = null,
        ILogger<DisplayLayoutTransactionManager>? logger = null)
    {
        _desktops = desktops;
        _client = client;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Test seam. Production waits with <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
    internal Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Load();
        if (_current is not null)
        {
            await RestoreAsync(_current.Id, CancellationToken.None).ConfigureAwait(false);
        }

        _timer = new Timer(_ => _ = SweepAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
        }

        if (_current is { State: not "restored" })
        {
            await RestoreAsync(_current.Id, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<OperationResult<DisplayLayoutSnapshot>> SnapshotAsync(
        bool elevated,
        CancellationToken cancellationToken,
        string? desktopSessionId = null)
    {
        OperationResult<DesktopHelperTarget?> targeted = await DesktopHelperTargeting
            .ResolveAsync(_desktops, desktopSessionId, requireInput: false, cancellationToken).ConfigureAwait(false);
        return targeted.Success
            ? await _client.SnapshotAsync(elevated, cancellationToken, targeted.Value?.DesktopPath).ConfigureAwait(false)
            : targeted.FailAs<DisplayLayoutSnapshot>();
    }

    /// <summary>
    ///     Desktop that can change the display now: the managed desktop that is the input desktop, whichever session that is,
    ///     otherwise the default desktop (null). Resolved per call so a closed or switched session cannot leave a stale path.
    ///     Nothing is switched.
    /// </summary>
    private Task<string?> ExecutionPathAsync(CancellationToken cancellationToken) =>
        _desktops is null ? Task.FromResult<string?>(null) : _desktops.InputDesktopPathAsync(cancellationToken);

    public async Task<OperationResult<DisplayLayoutTransaction>> StartAsync(
        IReadOnlyList<DisplayTargetChange> targets,
        bool persistent,
        bool elevated,
        CancellationToken cancellationToken,
        string? desktopSessionId = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? ownedId = null;
        try
        {
            if (_current is { State: not "restored" })
            {
                return OperationResult<DisplayLayoutTransaction>.Fail(
                    "display_transaction_busy",
                    "A display-layout transaction is already open. Restore it before starting another.");
            }

            OperationResult<DesktopHelperTarget?> targeted = await DesktopHelperTargeting
                .ResolveAsync(_desktops, desktopSessionId, requireInput: true, cancellationToken).ConfigureAwait(false);
            if (!targeted.Success)
            {
                return targeted.FailAs<DisplayLayoutTransaction>();
            }

            string? desktopPath = targeted.Value?.DesktopPath;
            OperationResult<DisplayLayoutSnapshot> before = await _client.SnapshotAsync(elevated, cancellationToken, desktopPath).ConfigureAwait(false);
            if (!before.Success)
            {
                return before.FailAs<DisplayLayoutTransaction>();
            }

            DisplayLayoutValidation validation = DisplayLayoutPlanner.Validate(before.Value!, targets);
            if (!validation.Valid)
            {
                return OperationResult<DisplayLayoutTransaction>.Fail("invalid_request", string.Join(' ', validation.Errors));
            }

            DisplayLayoutTransaction transaction = new(
                Guid.NewGuid().ToString("N")[..12],
                "pending",
                _time.GetUtcNow(),
                _time.GetUtcNow() + _options.Desktop.DisplayTransactionLease,
                persistent,
                before.Value!,
                targets,
                null,
                false,
                [],
                null,
                DesktopSessionId: targeted.Value?.SessionId);
            ownedId = transaction.Id;
            _current = transaction;
            _elevated = elevated;
            Save();
            OperationResult<DisplayLayoutApplyResult> applied = await _client.ApplyAsync(
                new DisplayLayoutRequest(targets, persistent, false),
                elevated,
                cancellationToken,
                desktopPath).ConfigureAwait(false);
            if (applied.Success && applied.Value is { Applied: true, Unchanged: true })
            {
                _current = null;
                TryDelete();
                return OperationResult<DisplayLayoutTransaction>.Ok(transaction with
                {
                    State = "unchanged",
                    Current = before.Value,
                    Unchanged = true,
                    RestorationVerified = true
                });
            }

            if (applied.Success && applied.Value is { Applied: true })
            {
                _current = transaction with
                {
                    State = "active",
                    Current = applied.Value.After,
                    Unchanged = applied.Value.Unchanged
                };
                Save();
                return OperationResult<DisplayLayoutTransaction>.Ok(_current);
            }

            if (applied.Value is { Applied: false })
            {
                string message = DescribeRejection(applied.Value);
                if (applied.Value.NativeError == AccessDenied && desktopPath is null)
                {
                    message += " Windows only lets the input desktop change the display configuration. If a managed desktop is active, pass its desktopSessionId; nothing was switched.";
                }

                bool drifted = applied.Value.Before is not null
                    && applied.Value.After is not null
                    && !string.Equals(applied.Value.Before.Fingerprint, applied.Value.After.Fingerprint, StringComparison.OrdinalIgnoreCase);
                bool unknownAfter = applied.Value.Validated && applied.Value.After is null;
                if ((drifted || unknownAfter) && applied.Value.Stage == "apply")
                {
                    await RestoreCoreAsync(CancellationToken.None).ConfigureAwait(false);
                    message += _current is { RestorationVerified: true }
                        ? " Rollback verified the original topology."
                        : " Rollback did not verify the original topology.";
                }
                else if (!(drifted || unknownAfter))
                {
                    _current = null;
                    TryDelete();
                }
                else if (_current is { Id: var pendingId })
                {
                    message += " Transaction " + pendingId
                        + " remains open. Use get_display_layout_transaction and restore_display_layout_transaction to recover the original topology.";
                }

                return OperationResult<DisplayLayoutTransaction>.Fail(
                    applied.Value.ErrorCode ?? "display_config_rejected",
                    message);
            }

            if (!applied.Success && applied.ErrorCode is "helper_timeout" or "helper_failed" or "cancelled")
            {
                await RestoreCoreAsync(CancellationToken.None).ConfigureAwait(false);
                return applied.FailAs<DisplayLayoutTransaction>();
            }

            _current = null;
            TryDelete();
            return applied.FailAs<DisplayLayoutTransaction>();
        }
        catch (OperationCanceledException)
        {
            if (_current is { Id: var id, State: "pending" or "active" } && id == ownedId)
            {
                await RestoreCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return OperationResult<DisplayLayoutTransaction>.Fail(
                "cancelled",
                "The display change was cancelled. Restore was attempted and its result is in get_display_layout_transaction.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public OperationResult<DisplayLayoutTransaction> Get(string? id)
    {
        if (_current is null || (id is not null && !string.Equals(id, _current.Id, StringComparison.Ordinal)))
        {
            return OperationResult<DisplayLayoutTransaction>.Fail("not_found", "Display-layout transaction was not found.");
        }

        return OperationResult<DisplayLayoutTransaction>.Ok(_current);
    }

    public async Task<OperationResult<DisplayLayoutObservation>> ObserveAsync(
        string id,
        DisplayWait wait,
        CancellationToken cancellationToken)
    {
        OperationResult<DisplayLayoutTransaction> existing = Get(id);
        if (!existing.Success)
        {
            return existing.FailAs<DisplayLayoutObservation>();
        }

        if (wait.Timeout <= TimeSpan.Zero || wait.Timeout > TimeSpan.FromMinutes(2) || wait.StableSamples is < 1 or > 10)
        {
            return OperationResult<DisplayLayoutObservation>.Fail("invalid_request", "timeout must be positive and at most 2 minutes, and stableSamples must be from 1 to 10.");
        }

        DateTimeOffset deadline = _time.GetUtcNow() + wait.Timeout;
        int stable = 0;
        string? previous = null;
        DisplayLayoutObservation? last = null;
        while (true)
        {
            string? desktopPath = await ExecutionPathAsync(cancellationToken).ConfigureAwait(false);
            OperationResult<DisplayLayoutSnapshot> snapshot = await _client.SnapshotAsync(_elevated, cancellationToken, desktopPath).ConfigureAwait(false);
            if (!snapshot.Success)
            {
                return snapshot.FailAs<DisplayLayoutObservation>();
            }

            IReadOnlyList<string> differences = wait.Target is null
                ? DisplayLayoutPlanner.Compare(existing.Value!.Original, snapshot.Value!)
                : DisplayLayoutPlanner.Compare(wait.Target, snapshot.Value!);
            string sample = snapshot.Value!.Fingerprint + string.Join('\n', differences);
            stable = sample == previous && differences.Count == 0 ? stable + 1 : differences.Count == 0 ? 1 : 0;
            previous = sample;
            DisplayPathLayout? target = wait.Target is null
                ? null
                : snapshot.Value.Paths.FirstOrDefault(path => DisplayLayoutPlanner.Matches(path.Identity, wait.Target));
            last = new DisplayLayoutObservation(
                id,
                differences.Count == 0,
                stable >= wait.StableSamples,
                stable,
                wait.StableSamples,
                false,
                target,
                differences,
                snapshot.Value);
            if (last.Stable)
            {
                return OperationResult<DisplayLayoutObservation>.Ok(last);
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return OperationResult<DisplayLayoutObservation>.Ok(last with { TimedOut = true });
            }

            await (Delay ?? Task.Delay)(TimeSpan.FromMilliseconds(_options.Desktop.DisplayObservationIntervalMilliseconds), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<OperationResult<DisplayLayoutTransaction>> RestoreAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Get(id) is not { Success: true })
            {
                return OperationResult<DisplayLayoutTransaction>.Fail("not_found", "Display-layout transaction was not found.");
            }

            if (_current!.State == "restored")
            {
                return OperationResult<DisplayLayoutTransaction>.Ok(_current);
            }

            await RestoreCoreAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult<DisplayLayoutTransaction>.Ok(_current!);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    internal async Task SweepAsync()
    {
        if (_current is not { State: "active" or "restore_failed" })
        {
            return;
        }

        bool leaseExpired = _current.LeaseUntil is DateTimeOffset lease && _time.GetUtcNow() >= lease;
        bool retry = _current.State == "restore_failed" && _time.GetUtcNow() - _lastRestoreAttempt >= TimeSpan.FromSeconds(30);
        if (!leaseExpired && !retry)
        {
            return;
        }

        try
        {
            await RestoreAsync(_current.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogWarning("Display-layout restore retry failed: {ErrorType}", ex.GetType().Name);
        }
    }

    private static string DescribeRejection(DisplayLayoutApplyResult result)
    {
        string message = result.Error ?? "SetDisplayConfig did not apply the layout.";
        string stage = result.Stage ?? (result.Validated ? "apply" : "validate");
        if (!message.Contains("Stage=", StringComparison.Ordinal))
        {
            message += $" Stage={stage}.";
        }

        if (result.NativeError is int native && !message.Contains("NativeError=", StringComparison.Ordinal))
        {
            message += $" NativeError={native}.";
        }

        if (!string.IsNullOrWhiteSpace(result.Flags) && !message.Contains("Flags=", StringComparison.Ordinal))
        {
            message += $" Flags={result.Flags}.";
        }

        return message;
    }

    private async Task RestoreCoreAsync(CancellationToken cancellationToken)
    {
        if (_current is null)
        {
            return;
        }

        _lastRestoreAttempt = _time.GetUtcNow();
        string? desktopPath = await ExecutionPathAsync(cancellationToken).ConfigureAwait(false);
        List<DisplayTargetChange> original = _current.Original.Paths.Where(path => path.Active).Select(path => new DisplayTargetChange(
            path.Identity.MonitorDevicePath,
            path.Identity.ConnectorInstance,
            path.Identity.AdapterLuid,
            path.Identity.TargetId,
            path.X,
            path.Y,
            path.Width,
            path.Height,
            path.RefreshNumerator,
            path.RefreshDenominator,
            path.Rotation)).ToList();
        OperationResult<DisplayLayoutApplyResult> applied = await _client.ApplyAsync(
            new DisplayLayoutRequest(original, true, false),
            _elevated,
            cancellationToken,
            desktopPath).ConfigureAwait(false);
        OperationResult<DisplayLayoutSnapshot> after = await _client.SnapshotAsync(_elevated, cancellationToken, desktopPath).ConfigureAwait(false);
        IReadOnlyList<string> differences = after.Success
            ? DisplayLayoutPlanner.Compare(_current.Original, after.Value!)
            : [after.Error ?? "The topology could not be read after restore."];
        bool verified = applied.Success && applied.Value is { Applied: true } && after.Success && differences.Count == 0;
        _current = _current with
        {
            State = verified ? "restored" : "restore_failed",
            Current = after.Success ? after.Value : _current.Current,
            RestorationVerified = verified,
            Differences = differences,
            Error = verified
                ? null
                : applied.Value is { Applied: false } rejection
                    ? DescribeRejection(rejection)
                    : applied.Error ?? (differences.Count == 0 ? "restore was not verified" : string.Join(' ', differences))
        };
        if (verified)
        {
            DisplayLayoutTransaction restored = _current;
            _current = null;
            TryDelete();
            _current = restored;
            return;
        }

        Save();
    }

    private void Load()
    {
        if (!File.Exists(VestigoPaths.OwnedDisplayLayoutPath))
        {
            return;
        }

        try
        {
            StoredCheckpoint? stored = JsonSerializer.Deserialize<StoredCheckpoint>(File.ReadAllText(VestigoPaths.OwnedDisplayLayoutPath), SessionHelper.Json);
            if (stored is not { Version: CheckpointVersion })
            {
                _logger?.LogWarning("Display-layout checkpoint version is unsupported. It was left in place.");
                return;
            }

            _current = stored.Transaction with { State = "restore_failed", Error = "Restoring a layout that was open when the service stopped." };
            _elevated = stored.Elevated;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Display-layout checkpoint could not be read. It was left in place.");
        }
    }

    private void Save()
    {
        if (_current is null)
        {
            return;
        }

        StateDirectories.EnsureSubdirectory(VestigoPaths.SettingsDirectory);
        string temp = VestigoPaths.OwnedDisplayLayoutPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new StoredCheckpoint(CheckpointVersion, _current, _elevated), SessionHelper.Json));
        if (File.Exists(VestigoPaths.OwnedDisplayLayoutPath))
        {
            File.Replace(temp, VestigoPaths.OwnedDisplayLayoutPath, null);
        }
        else
        {
            File.Move(temp, VestigoPaths.OwnedDisplayLayoutPath);
        }
    }

    private static void TryDelete()
    {
        try
        {
            if (File.Exists(VestigoPaths.OwnedDisplayLayoutPath))
            {
                File.Delete(VestigoPaths.OwnedDisplayLayoutPath);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed record StoredCheckpoint(int Version, DisplayLayoutTransaction Transaction, bool Elevated);
}
