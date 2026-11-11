namespace Vestigo.Desktop;

/// <summary>Requested mode for a display the session owns. Existing monitors are not described here.</summary>
public sealed record VirtualDisplayRequest(
    int Width,
    int Height,
    uint RefreshNumerator,
    uint RefreshDenominator,
    int X,
    int Y,
    bool Primary);

/// <summary>One display created by the backend for this session.</summary>
public sealed record OwnedVirtualDisplay(string Id, string IdentityKey);

/// <summary>Whether a released backend can create a display. <c>CanCreate</c> false means creation is not available.</summary>
public sealed record VirtualDisplayBackendStatus(string Backend, bool CanCreate, string? ErrorCode, string? Error);

/// <summary>
///     Creates and removes only displays it created. The installed Playcast or Parsec driver is not an implementation
///     of this contract.
/// </summary>
public interface IVirtualDisplayBackend
{
    string Name { get; }

    bool CanCreate { get; }

    Task<OperationResult<VirtualDisplayBackendStatus>> StatusAsync(CancellationToken cancellationToken);

    Task<OperationResult<OwnedVirtualDisplay>> CreateAsync(VirtualDisplayRequest request, CancellationToken cancellationToken);

    Task<OperationResult<bool>> RemoveAsync(string displayId, CancellationToken cancellationToken);
}

/// <summary>No released backend is selected. Creation and removal stay unavailable.</summary>
public sealed class UnavailableVirtualDisplayBackend : IVirtualDisplayBackend
{
    public const string Code = "virtual_display_backend_unavailable";

    public string Name => "none";

    public bool CanCreate => false;

    public Task<OperationResult<VirtualDisplayBackendStatus>> StatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult<VirtualDisplayBackendStatus>.Ok(Status()));

    public Task<OperationResult<OwnedVirtualDisplay>> CreateAsync(VirtualDisplayRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult<OwnedVirtualDisplay>.Fail(Code, Message));

    public Task<OperationResult<bool>> RemoveAsync(string displayId, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult<bool>.Fail(Code, Message));

    internal static VirtualDisplayBackendStatus Status() =>
        new("none", false, Code, Message);

    private const string Message =
        "No released virtual-display backend is selected. An installed indirect-display driver is not an API, and Vestigo does not create a display.";
}
