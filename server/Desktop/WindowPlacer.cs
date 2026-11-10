namespace Vestigo.Desktop;

public sealed record WindowPlacementRequest(
    string Handle,
    int? ExpectedPid,
    DateTimeOffset? ExpectedStartTime,
    int X,
    int Y,
    int Width,
    int Height,
    string CoordinateSpace,
    bool SavePlacement);

public sealed record PlacementSample(bool DpiEstablished, DesktopRect? Raw, DesktopRect? Client, string? ErrorCode, string? Error);

public sealed record PlacementMove(bool ApiSucceeded, int? NativeError, string? Error);

/// <summary>Win32 <c>WINDOWPLACEMENT</c> captured before a move. Restoration rechecks the process identity.</summary>
public sealed record SavedWindowPlacement(
    string Handle,
    int ProcessId,
    DateTimeOffset? ProcessStartTime,
    bool StartTimeReadable,
    int ShowCommand,
    int MinX,
    int MinY,
    int MaxX,
    int MaxY,
    int NormalX,
    int NormalY,
    int NormalWidth,
    int NormalHeight,
    uint Flags);

public sealed record WindowPlacementAttempt(
    int Number,
    DesktopRect RequestedOuter,
    bool ApiSucceeded,
    int? NativeError,
    string? Error,
    DesktopRect? MeasuredClient);

public sealed record WindowPlacementResult(
    bool Achieved,
    string CoordinateSpace,
    DesktopRect RequestedClient,
    DesktopRect? MeasuredClient,
    DesktopRect? MeasuredRaw,
    bool ActivationPreserved,
    string? ForegroundBefore,
    string? ForegroundAfter,
    IReadOnlyList<WindowPlacementAttempt> Attempts,
    string? SavedPlacementToken,
    string? StopCode,
    string? StopError,
    SavedWindowPlacement? CapturedPlacement = null);

public interface IWindowPlacementSource
{
    OperationResult<PlacementSample> Measure(string handle, int? expectedPid, DateTimeOffset? expectedStartTime);

    OperationResult<PlacementMove> MoveOuter(string handle, int? expectedPid, DateTimeOffset? expectedStartTime, DesktopRect outer);

    OperationResult<SavedWindowPlacement> ReadPlacement(string handle, int? expectedPid, DateTimeOffset? expectedStartTime);

    OperationResult<PlacementMove> ApplyPlacement(SavedWindowPlacement placement);

    string? Foreground();
}

/// <summary>
///     Moves a window so its client area matches a physical-pixel rectangle. Windows or the application may reject
///     the move; <see cref="WindowPlacementResult.Achieved" /> is true only when a later measurement matches.
/// </summary>
public sealed class WindowPlacer(IWindowPlacementSource source, WindowPlacementStore store)
{
    public const int MaxAttempts = 4;

    public OperationResult<WindowPlacementResult> Place(WindowPlacementRequest request)
    {
        if (!string.Equals(request.CoordinateSpace, DesktopCoordinates.PhysicalScreenPixels, StringComparison.Ordinal))
        {
            return OperationResult<WindowPlacementResult>.Fail(
                "invalid_request",
                "coordinateSpace must be physical-screen-pixels.");
        }

        if (request.Width is <= 0 or > 16_384 || request.Height is <= 0 or > 16_384)
        {
            return OperationResult<WindowPlacementResult>.Fail("invalid_request", "width and height must be from 1 to 16384.");
        }

        OperationResult<PlacementSample> measured = source.Measure(request.Handle, request.ExpectedPid, request.ExpectedStartTime);
        if (!measured.Success)
        {
            return measured.FailAs<WindowPlacementResult>();
        }

        if (!measured.Value!.DpiEstablished || measured.Value.Raw is null || measured.Value.Client is null)
        {
            return OperationResult<WindowPlacementResult>.Fail(
                measured.Value.ErrorCode ?? "dpi_context_unavailable",
                measured.Value.Error ?? "The physical DPI context could not be established.");
        }

        string? token = null;
        SavedWindowPlacement? captured = null;
        if (request.SavePlacement)
        {
            OperationResult<SavedWindowPlacement> saved = source.ReadPlacement(request.Handle, request.ExpectedPid, request.ExpectedStartTime);
            if (!saved.Success)
            {
                return saved.FailAs<WindowPlacementResult>();
            }

            captured = saved.Value;
            OperationResult<string> stored = store.Save(saved.Value!);
            if (!stored.Success)
            {
                return stored.FailAs<WindowPlacementResult>();
            }

            token = stored.Value;
        }

        DesktopRect requested = new(request.X, request.Y, request.Width, request.Height);
        return OperationResult<WindowPlacementResult>.Ok(Move(request, requested, measured.Value, token, captured));
    }

    public OperationResult<WindowPlacementResult> Restore(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return OperationResult<WindowPlacementResult>.Fail("invalid_request", "token is required.");
        }

        OperationResult<SavedWindowPlacement> saved = store.Peek(token);
        if (!saved.Success)
        {
            return saved.FailAs<WindowPlacementResult>();
        }

        string? before = source.Foreground();
        OperationResult<PlacementMove> applied = source.ApplyPlacement(saved.Value!);
        if (!applied.Success)
        {
            return applied.FailAs<WindowPlacementResult>();
        }

        store.Remove(token);
        OperationResult<PlacementSample> measured = source.Measure(
            saved.Value!.Handle,
            saved.Value.ProcessId,
            saved.Value.StartTimeReadable ? saved.Value.ProcessStartTime : null);
        OperationResult<SavedWindowPlacement> reread = source.ReadPlacement(
            saved.Value.Handle,
            saved.Value.ProcessId,
            saved.Value.StartTimeReadable ? saved.Value.ProcessStartTime : null);
        DesktopRect requested = new(saved.Value.NormalX, saved.Value.NormalY, saved.Value.NormalWidth, saved.Value.NormalHeight);
        bool achieved = applied.Value!.ApiSucceeded && reread.Success && SameRestoredPlacement(saved.Value, reread.Value!);
        string? after = source.Foreground();
        return OperationResult<WindowPlacementResult>.Ok(new WindowPlacementResult(
            achieved,
            DesktopCoordinates.PhysicalScreenPixels,
            requested,
            measured.Success ? measured.Value!.Client : null,
            measured.Success ? measured.Value!.Raw : null,
            string.Equals(before, after, StringComparison.OrdinalIgnoreCase),
            before,
            after,
            [new WindowPlacementAttempt(1, requested, applied.Value.ApiSucceeded, applied.Value.NativeError, applied.Value.Error, measured.Success ? measured.Value!.Raw : null)],
            null,
            achieved ? null : "placement_not_achieved",
            achieved ? null : "Restoring the saved placement did not reproduce its normal rectangle.",
            null));
    }

    private WindowPlacementResult Move(
        WindowPlacementRequest request,
        DesktopRect requested,
        PlacementSample initial,
        string? token,
        SavedWindowPlacement? captured)
    {
        List<WindowPlacementAttempt> attempts = [];
        string? before = source.Foreground();
        DesktopRect? client = initial.Client;
        DesktopRect? raw = initial.Raw;
        WindowInsets insets = WindowGeometryMath.Insets(raw!, client!);
        bool achieved = client == requested;
        string? stopCode = null;
        string? stopError = null;
        for (int number = 1; number <= MaxAttempts && !achieved; number++)
        {
            DesktopRect outer = WindowGeometryMath.Expand(requested, insets);
            OperationResult<PlacementMove> moved = source.MoveOuter(request.Handle, request.ExpectedPid, request.ExpectedStartTime, outer);
            if (!moved.Success)
            {
                stopCode = moved.ErrorCode;
                stopError = moved.Error;
                attempts.Add(new WindowPlacementAttempt(number, outer, false, null, moved.Error, client));
                break;
            }

            OperationResult<PlacementSample> again = source.Measure(request.Handle, request.ExpectedPid, request.ExpectedStartTime);
            DesktopRect? measuredClient = again.Success ? again.Value!.Client : null;
            attempts.Add(new WindowPlacementAttempt(number, outer, moved.Value!.ApiSucceeded, moved.Value.NativeError, moved.Value.Error, measuredClient));
            if (!moved.Value.ApiSucceeded || !again.Success || again.Value!.Raw is null || measuredClient is null)
            {
                stopCode = again.ErrorCode ?? "placement_not_achieved";
                stopError = again.Error ?? moved.Value.Error ?? "The window could not be measured after SetWindowPos.";
                break;
            }

            if (measuredClient == client && WindowGeometryMath.Insets(again.Value.Raw, measuredClient) == insets)
            {
                client = measuredClient;
                raw = again.Value.Raw;
                stopCode = "placement_not_achieved";
                stopError = "The window did not accept the requested client rectangle.";
                break;
            }

            client = measuredClient;
            raw = again.Value.Raw;
            insets = WindowGeometryMath.Insets(raw, client);
            achieved = client == requested;
        }

        string? after = source.Foreground();
        return new WindowPlacementResult(
            achieved,
            DesktopCoordinates.PhysicalScreenPixels,
            requested,
            client,
            raw,
            string.Equals(before, after, StringComparison.OrdinalIgnoreCase),
            before,
            after,
            attempts,
            token,
            achieved ? null : stopCode ?? "placement_not_achieved",
            achieved ? null : stopError ?? "The measured client rectangle does not match the request.",
            captured);
    }

    internal static bool SameRestoredPlacement(SavedWindowPlacement saved, SavedWindowPlacement actual) =>
        saved.ShowCommand == actual.ShowCommand
        && saved.NormalX == actual.NormalX
        && saved.NormalY == actual.NormalY
        && saved.NormalWidth == actual.NormalWidth
        && saved.NormalHeight == actual.NormalHeight;
}

/// <summary>Short-lived placement tokens. They are not kept across a service restart.</summary>
public sealed class WindowPlacementStore(TimeProvider time)
{
    public const int Capacity = 32;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, Entry> _items = new(StringComparer.Ordinal);

    public OperationResult<string> Save(SavedWindowPlacement placement)
    {
        lock (_items)
        {
            Expire();
            if (_items.Count >= Capacity)
            {
                return OperationResult<string>.Fail("placement_save_full", "Too many saved window placements are waiting for restore.");
            }

            string token = Guid.NewGuid().ToString("N")[..12];
            _items[token] = new Entry(placement, time.GetUtcNow() + Lifetime);
            return OperationResult<string>.Ok(token);
        }
    }

    public OperationResult<SavedWindowPlacement> Peek(string token)
    {
        lock (_items)
        {
            Expire();
            return _items.TryGetValue(token, out Entry? entry)
                ? OperationResult<SavedWindowPlacement>.Ok(entry.Placement)
                : OperationResult<SavedWindowPlacement>.Fail("placement_not_found", "The saved placement is unknown or expired.");
        }
    }

    public void Remove(string token)
    {
        lock (_items)
        {
            _items.Remove(token);
        }
    }

    private void Expire()
    {
        DateTimeOffset now = time.GetUtcNow();
        foreach (string token in _items.Where(item => item.Value.Expires <= now).Select(item => item.Key).ToArray())
        {
            _items.Remove(token);
        }
    }

    private sealed record Entry(SavedWindowPlacement Placement, DateTimeOffset Expires);
}
