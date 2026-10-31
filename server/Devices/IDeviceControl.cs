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
}
