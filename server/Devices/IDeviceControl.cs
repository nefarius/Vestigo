namespace Vestigo.Devices;

/// <summary>A device that exposes a given device interface.</summary>
public sealed record DeviceInterfaceMatch(string InstanceId, string DevicePath);

/// <summary>
///     A device operation failed. <see cref="Code" /> is a stable machine-readable error code.
/// </summary>
public sealed class DeviceControlException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public const string DeviceNotFound = "device_not_found";
    public const string ConfigManagerFailed = "config_manager_failed";
    public const string Win32Failed = "win32_failed";
    public const string WindowsRequired = "windows_required";
    public const string DeviceOperationFailed = "device_operation_failed";

    public string Code { get; } = code;
}

/// <summary>
///     Device state changes and queries backed by Nefarius.Utilities.DeviceManagement. Implementations throw
///     <see cref="DeviceControlException" /> for every expected failure so callers need no library types.
/// </summary>
public interface IDeviceControl
{
    void Restart(string instanceId);

    void Enable(string instanceId);

    void Disable(string instanceId);

    /// <summary>Devices exposing <paramref name="interfaceGuid" />, at most <paramref name="maxResults" />.</summary>
    IReadOnlyList<DeviceInterfaceMatch> FindByInterface(Guid interfaceGuid, bool presentOnly, int maxResults);

    /// <summary>Instance IDs in the device class whose hardware IDs match <paramref name="hardwareId" />.</summary>
    IReadOnlyList<string> FindByHardwareId(Guid classGuid, string hardwareId, bool presentOnly, bool allowPartial);

    /// <summary>Every device node (optionally only present ones) with a lightweight summary.</summary>
    IReadOnlyList<DeviceEntry> EnumerateDevices(bool presentOnly);

    /// <summary>Detailed state of one device node.</summary>
    DeviceDetails GetDevice(string instanceId);

    /// <summary>Upper and lower filter driver service names registered for a device setup class.</summary>
    ClassFilterSet GetClassFilters(Guid classGuid);

    /// <summary>Driver packages staged in the driver store.</summary>
    IReadOnlyList<DriverStoreEntry> GetDriverStore(bool includeInbox);
}

/// <summary>Summary of one device node.</summary>
public sealed record DeviceEntry(
    string InstanceId,
    string? Description,
    Guid ClassGuid,
    string? ClassName,
    IReadOnlyList<string> HardwareIds,
    string? Service,
    string? DriverInfPath,
    bool IsPresent);

/// <summary>Detailed state of a device node. Properties the device does not have are null.</summary>
public sealed record DeviceDetails(
    string InstanceId,
    string? DeviceId,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> CompatibleIds,
    string? Description,
    string? ClassName,
    Guid? ClassGuid,
    string? Service,
    bool? IsPresent,
    uint? ProblemCode,
    uint? DevNodeStatus,
    string? DriverKey,
    string? DriverDescription,
    string? DriverProvider,
    string? DriverVersion,
    DateTimeOffset? DriverDate,
    string? DriverInfPath);

/// <summary>Upper and lower filter driver service names of a device class.</summary>
public sealed record ClassFilterSet(IReadOnlyList<string> Upper, IReadOnlyList<string> Lower);

/// <summary>A driver package staged in the driver store.</summary>
public sealed record DriverStoreEntry(
    string PublishedName,
    string OriginalInfName,
    string? Provider,
    string? ClassName,
    Guid ClassGuid,
    string? DriverVersion,
    DateTime? DriverDate,
    string? Architecture,
    bool IsInbox,
    string StoreInfPath,
    IReadOnlyList<string> BoundDeviceInstanceIds);
