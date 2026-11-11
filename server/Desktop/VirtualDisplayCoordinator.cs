using System.Text.Json;

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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, VirtualDisplaySession> _sessions = new(StringComparer.Ordinal);
    private bool _checkpointUnsupported;
    private Timer? _timer;

    public VirtualDisplayCoordinator(
        IVirtualDisplayBackend backend,
        IDisplayLayoutClient displays,
        VestigoOptions options,
        TimeProvider time)
    {
        _backend = backend;
        _displays = displays;
        _options = options;
        _time = time;
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
        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
            _timer = null;
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            foreach (string id in _sessions.Values.Where(item => item.State is "open").Select(item => item.Id).ToList())
            {
                await CloseCoreAsync(id, automatic: true, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    public Task<OperationResult<VirtualDisplayBackendStatus>> StatusAsync(CancellationToken cancellationToken) =>
        _backend.StatusAsync(cancellationToken);

    public async Task<OperationResult<VirtualDisplaySession>> CreateAsync(
        VirtualDisplayRequest request,
        CancellationToken cancellationToken)
    {
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
                await _backend.RemoveAsync(owned.Id, CancellationToken.None).ConfigureAwait(false);
                return OperationResult<VirtualDisplaySession>.Fail(
                    "virtual_display_identity_conflict",
                    "The backend reported a display that was already in the topology. It was removed and the checkpoint was not changed.");
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
        foreach (string owned in session.OwnedDisplayIds)
        {
            if (session.ForeignDisplayIds.Contains(owned, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            await _backend.RemoveAsync(owned, CancellationToken.None).ConfigureAwait(false);
        }

        VirtualDisplaySession closed = session with
        {
            State = restored.Success ? "closed" : "recovery-failed",
            LastError = restored.Success ? null : restored.Error
        };
        _sessions[id] = closed;
        Save();
        return restored.Success || automatic
            ? OperationResult<VirtualDisplaySession>.Ok(closed)
            : restored.FailAs<VirtualDisplaySession>();
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
            if (!document.RootElement.TryGetProperty("version", out JsonElement version) || version.GetInt32() != CheckpointVersion)
            {
                _checkpointUnsupported = true;
                return;
            }

            StoredVirtualDisplays? stored = document.Deserialize<StoredVirtualDisplays>(SessionHelper.Json);
            foreach (VirtualDisplaySession session in stored?.Sessions ?? [])
            {
                _sessions[session.Id] = session;
            }
        }
        catch (JsonException)
        {
            _checkpointUnsupported = true;
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
