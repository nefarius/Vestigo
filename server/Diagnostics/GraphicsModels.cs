namespace Vestigo.Diagnostics;

/// <summary>One DXGI output and the adapters it is attached to. Unknown sizes are null.</summary>
public sealed record GraphicsOutput(
    string? DeviceName,
    bool? AttachedToDesktop,
    int? Width,
    int? Height,
    string? Rotation,
    IReadOnlyList<string> AdapterLuids);

/// <summary>One DXGI adapter. Unknown PCI ids, memory sizes, and driver versions are null rather than zero.</summary>
public sealed record GraphicsAdapter(
    string? Description,
    uint? VendorId,
    uint? DeviceId,
    uint? SubsystemId,
    uint? Revision,
    string? Luid,
    ulong? DedicatedVideoMemory,
    ulong? DedicatedSystemMemory,
    ulong? SharedSystemMemory,
    bool Software,
    bool Remote,
    string? DriverVersion,
    string? InstanceId,
    string? MatchConfidence,
    IReadOnlyList<GraphicsOutput> Outputs);

/// <summary>Adapters enumerated in the service process, including software and remote adapters.</summary>
public sealed record GraphicsAdapterList(IReadOnlyList<GraphicsAdapter> Adapters);
