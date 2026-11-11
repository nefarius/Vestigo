using System.Text.Json;

using Microsoft.Extensions.Logging;

using Vestigo.Configuration;
using Vestigo.Processes;

namespace Vestigo.Desktop;

/// <summary>
///     Owns temporary virtual displays for one session. The topology checkpoint is taken before the backend is called
///     and restored before any owned display is removed. Displays that were already present are never removed.
/// </summary>
public sealed class VirtualDisplayCoordinator : IHostedService, IDisposable
{
    private const int CheckpointVersion = 1;

    private readonly IVirtualDisplayBackend _backend;
    private readonly IDisplayLayoutClient _displays;
    private readonly VestigoOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<VirtualDisplayCoordinator>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, VirtualDisplaySession> _sessions = new(StringComparer.Ordinal);
    private bool _checkpointUnsupported;
    private Timer? _timer;
    private int _lifetime;

    public VirtualDisplayCoordinator(
        IVirtualDisplayBackend backend,
        IDisplayLayoutClient displays,
        VestigoOptions options,
        TimeProvider time,
        ILogger<VirtualDisplayCoordinator>? logger = null)
    {
        _backend = backend;
        _displays = displays;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Load();
        foreach (VirtualDisplaySession session in _sessions.Values.Where(item => item.State is "open").ToList())
        {
            await CloseCoreAsync(session.Id, automatic: true, CancellationToken.None).ConfigureAwait(false);
        }

        _timer = new Timer(_ => _ = SweepAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
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

            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await _gate.WaitAsync(bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                foreach (string id in _sessions.Values.Where(item => item.State is "open").Select(item => item.Id).ToList())
                {
                    await CloseCoreAsync(id, automatic: true, bounded.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _gate.Dispose();
        }
    }

    public void Dispose()
    {
        if (!TryOwnLifetime())
        {
            return;
        }

        _timer?.Dispose();
        _gate.Dispose();
    }

    private bool TryOwnLifetime() => Interlocked.CompareExchange(ref _lifetime, 1, 0) == 0;

    public Task<OperationResult<VirtualDisplayBackendStatus>> StatusAsync(CancellationToken cancellationToken) =>
        _backend.StatusAsync(cancellationToken);

    public async Task<OperationResult<VirtualDisplaySession>> CreateAsync(
        VirtualDisplayRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Width <= 0 || request.Height <= 0 || request.RefreshNumerator == 0 || request.RefreshDenominator == 0)
        {
            return OperationResult<VirtualDisplaySession>.Fail("invalid_request", "Width, height, and refresh must be positive.");
        }

        if (request.Primary)
        {
            return OperationResult<VirtualDisplaySession>.Fail(
                "invalid_request",
                "primary is rejected. The display-layout request cannot select a primary display yet.");
        }

        if (_checkpointUnsupported)
        {
            return OperationResult<VirtualDisplaySession>.Fail(
                "virtual_display_checkpoint_unsupported",
                "owned-virtual-displays.json has an unknown version and was left in place.");
        }

        if (!_backend.CanCreate)
        {
            return OperationResult<VirtualDisplaySession>.Fail(
                UnavailableVirtualDisplayBackend.Code,
                "No released virtual-display backend is selected. An installed indirect-display driver is not an API, and Vestigo does not create a display.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? id = null;
        try
        {
            OperationResult<DisplayLayoutSnapshot> checkpoint = await _displays.SnapshotAsync(false, cancellationToken).ConfigureAwait(false);
            if (!checkpoint.Success)
            {
                return checkpoint.FailAs<VirtualDisplaySession>();
            }

            cancellationToken.ThrowIfCancellationRequested();
            HashSet<string> foreign = checkpoint.Value!.Paths.Select(path => path.Identity.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            OperationResult<OwnedVirtualDisplay> created = await _backend.CreateAsync(request, cancellationToken).ConfigureAwait(false);
            if (!created.Success)
            {
                return created.FailAs<VirtualDisplaySession>();
            }

            OwnedVirtualDisplay owned = created.Value!;
            if (foreign.Contains(owned.IdentityKey))
            {
                // The reported identity was already present. Removing by id could delete that display.
                return OperationResult<VirtualDisplaySession>.Fail(
                    "virtual_display_identity_conflict",
                    "The backend reported a display that was already in the topology. It was not removed and the checkpoint was not changed.");
            }

            id = Guid.NewGuid().ToString("N");
            VirtualDisplaySession session = new(
                id,
                _backend.Name,
                "open",
                [owned.Id],
                [.. foreign],
                _time.GetUtcNow(),
                _time.GetUtcNow() + _options.Desktop.DisplayTransactionLease,
                checkpoint.Value,
                owned.IdentityKey);
            _sessions[id] = session;
            Save();
            cancellationToken.ThrowIfCancellationRequested();

            DisplayTargetChange ownedChange = new(
                owned.IdentityKey,
                null,
                null,
                null,
                request.X,
                request.Y,
                request.Width,
                request.Height,
                request.RefreshNumerator,
                request.RefreshDenominator,
                null);
            OperationResult<DisplayLayoutApplyResult> arranged = await _displays.ApplyAsync(
                new DisplayLayoutRequest([ownedChange], false, false),
                false,
                cancellationToken).ConfigureAwait(false);
            if (!arranged.Success || arranged.Value is not { Applied: true })
            {
                await CloseCoreAsync(id, automatic: true, CancellationToken.None).ConfigureAwait(false);
                return arranged.Success
                    ? OperationResult<VirtualDisplaySession>.Fail(arranged.Value?.ErrorCode ?? "display_config_rejected", arranged.Value?.Error ?? "The owned display could not be arranged.")
                    : arranged.FailAs<VirtualDisplaySession>();
            }

            return OperationResult<VirtualDisplaySession>.Ok(_sessions.TryGetValue(id, out VirtualDisplaySession? current) ? current : session);
        }
        catch (OperationCanceledException)
        {
            if (id is not null)
            {
                await CloseCoreAsync(id, automatic: true, CancellationToken.None).ConfigureAwait(false);
            }

            return OperationResult<VirtualDisplaySession>.Fail("cancelled", "Virtual-display setup was cancelled. The checkpoint was restored and only an owned display was removed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult<VirtualDisplaySession>> CloseAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CloseCoreAsync(id, automatic: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task SweepAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            DateTimeOffset now = _time.GetUtcNow();
            foreach (string id in _sessions.Values.Where(item => item.State is "open" && item.LeaseExpiresAt <= now).Select(item => item.Id).ToList())
            {
                await CloseCoreAsync(id, automatic: true, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<OperationResult<VirtualDisplaySession>> CloseCoreAsync(string id, bool automatic, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(id, out VirtualDisplaySession? session) || session.State is "closed")
        {
            return OperationResult<VirtualDisplaySession>.Fail("virtual_display_session_not_found", "Virtual-display session was not found or is already closed.");
        }

        List<DisplayTargetChange> original = session.Checkpoint.Paths.Select(path => new DisplayTargetChange(
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
        OperationResult<DisplayLayoutApplyResult> restored = await _displays.ApplyAsync(
            new DisplayLayoutRequest(original, true, false),
            false,
            cancellationToken).ConfigureAwait(false);
        bool restoredOk = restored.Success && restored.Value is { Applied: true };
        bool removalFailed = false;
        string? removalError = null;
        if (restoredOk && !session.ForeignDisplayIds.Contains(session.OwnedIdentityKey, StringComparer.OrdinalIgnoreCase))
        {
            foreach (string owned in session.OwnedDisplayIds)
            {
                try
                {
                    OperationResult<bool> removed = await _backend.RemoveAsync(owned, cancellationToken).ConfigureAwait(false);
                    if (!removed.Success)
                    {
                        removalFailed = true;
                        removalError = removed.Error;
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
                {
                    removalFailed = true;
                    removalError = ex.Message;
                }
            }
        }

        bool retry = !restoredOk || removalFailed;
        VirtualDisplaySession closed = session with
        {
            State = retry ? "open" : "closed",
            LeaseExpiresAt = retry ? _time.GetUtcNow() : session.LeaseExpiresAt,
            LastError = retry ? removalError ?? restored.Error ?? restored.Value?.Error : null
        };
        _sessions[id] = closed;
        Save();
        if (!retry || automatic)
        {
            return OperationResult<VirtualDisplaySession>.Ok(closed);
        }

        if (!restored.Success)
        {
            return restored.FailAs<VirtualDisplaySession>();
        }

        return restoredOk
            ? OperationResult<VirtualDisplaySession>.Fail("virtual_display_remove_failed", closed.LastError ?? "The owned display could not be removed.")
            : OperationResult<VirtualDisplaySession>.Fail(restored.Value?.ErrorCode ?? "display_config_rejected", closed.LastError ?? "The checkpoint could not be restored.");
    }

    private void Load()
    {
        if (!File.Exists(VestigoPaths.OwnedVirtualDisplaysPath))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(VestigoPaths.OwnedVirtualDisplaysPath));
            if (!document.RootElement.TryGetProperty("version", out JsonElement version)
                || !version.TryGetInt32(out int parsedVersion)
                || parsedVersion != CheckpointVersion)
            {
                _checkpointUnsupported = true;
                _logger?.LogWarning("Leaving {Path} in place because its version is not supported.", VestigoPaths.OwnedVirtualDisplaysPath);
                return;
            }

            StoredVirtualDisplays? stored = document.Deserialize<StoredVirtualDisplays>(SessionHelper.Json);
            foreach (VirtualDisplaySession session in stored?.Sessions ?? [])
            {
                _sessions[session.Id] = session;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or IOException or UnauthorizedAccessException)
        {
            _checkpointUnsupported = true;
            _logger?.LogWarning(ex, "Leaving {Path} in place because it could not be read.", VestigoPaths.OwnedVirtualDisplaysPath);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(VestigoPaths.SettingsDirectory);
        StoredVirtualDisplays stored = new(CheckpointVersion, [.. _sessions.Values]);
        string json = JsonSerializer.Serialize(stored, SessionHelper.Json);
        string temp = VestigoPaths.OwnedVirtualDisplaysPath + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(VestigoPaths.OwnedVirtualDisplaysPath))
        {
            File.Replace(temp, VestigoPaths.OwnedVirtualDisplaysPath, null);
        }
        else
        {
            File.Move(temp, VestigoPaths.OwnedVirtualDisplaysPath);
        }
    }

    private sealed record StoredVirtualDisplays(int Version, IReadOnlyList<VirtualDisplaySession> Sessions);
}

public sealed record VirtualDisplaySession(
    string Id,
    string Backend,
    string State,
    IReadOnlyList<string> OwnedDisplayIds,
    IReadOnlyList<string> ForeignDisplayIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset LeaseExpiresAt,
    DisplayLayoutSnapshot Checkpoint,
    string OwnedIdentityKey,
    string? LastError = null);
