namespace Vestigo.Etws;

/// <summary>Published ETW providers that can carry presentation evidence.</summary>
public static class PresentationProviders
{
    public static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");

    public static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");

    public static readonly Guid DwmCore = new("9E9BBA3C-2E38-40CB-99F4-9E8281425164");

    public static readonly IReadOnlyList<Guid> All = [Dxgi, DxgKrnl, DwmCore];

    public const string Composed = "composed";

    public const string IndependentFlip = "independent-flip";

    public const string Exclusive = "exclusive";

    public const string Unknown = "unknown";
}

/// <summary>One decoded presentation event. A missing field stays null.</summary>
public sealed record PresentationSample(
    string? Provider,
    int? EventId,
    string? Task,
    int? ProcessId,
    int? AttributedProcessId,
    long? WindowHandle,
    string? ExplicitMode,
    bool Dropped);

/// <summary>
///     Read-only presentation observation. <see cref="Mode" /> is <c>unknown</c> unless an explicit
///     DXGI, DWM, or DxgKrnl event supports one classification for the target.
/// </summary>
public sealed record PresentationObservation(
    string Mode,
    string? Reason,
    string EvidenceSource,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int ProcessId,
    long? WindowHandle,
    int CorrelatedEvents,
    int SeenEvents,
    IReadOnlyList<string> ProvidersEnabled,
    IReadOnlyList<string> ProvidersFailed,
    string? SessionName,
    string? Detail);
