using Nefarius.Utilities.DeviceManagement.Exceptions;
using Nefarius.Utilities.DeviceManagement.PnP;

namespace Vestigo.Devices;

/// <summary>
///     <see cref="IDeviceControl" /> over Nefarius.Utilities.DeviceManagement 6.1.0. Restart, enable and
///     disable need an elevated service account.
/// </summary>
internal sealed class DeviceControl : IDeviceControl
{
    // PnPDevice.Restart is marked obsolete in 6.1.0 because it removes and re-enumerates the node, which is
    // exactly what a driver developer wants after changing filters, so the warning is acknowledged here.
#pragma warning disable CS0618
    public void Restart(string instanceId) => Run(instanceId, device => device.Restart());
#pragma warning restore CS0618

    public void Enable(string instanceId) => Run(instanceId, device => device.Enable());

    public void Disable(string instanceId) => Run(instanceId, device => device.Disable());

    public IReadOnlyList<DeviceInterfaceMatch> FindByInterface(Guid interfaceGuid, bool presentOnly, int maxResults)
    {
        List<DeviceInterfaceMatch> matches = [];
        try
        {
            for (int index = 0; index < maxResults; index++)
            {
                if (!Devcon.FindByInterfaceGuid(interfaceGuid, out string path, out string instanceId, index, presentOnly))
                {
                    break;
                }

                matches.Add(new DeviceInterfaceMatch(instanceId, path));
            }
        }
        catch (Exception ex) when (Map(ex) is { } mapped)
        {
            throw mapped;
        }

        return matches;
    }

    public IReadOnlyList<string> FindByHardwareId(Guid classGuid, string hardwareId, bool presentOnly, bool allowPartial)
    {
        try
        {
            return Devcon.FindInDeviceClassByHardwareId(classGuid, hardwareId, out IEnumerable<string> instanceIds, presentOnly, allowPartial)
                ? instanceIds.ToList()
                : [];
        }
        catch (Exception ex) when (Map(ex) is { } mapped)
        {
            throw mapped;
        }
    }

    private static void Run(string instanceId, Action<PnPDevice> action)
    {
        try
        {
            action(PnPDevice.GetDeviceByInstanceId(instanceId));
        }
        catch (Exception ex) when (Map(ex) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>Maps library and platform exceptions to stable codes; returns null for exceptions that must propagate.</summary>
    internal static DeviceControlException? Map(Exception ex) => ex switch
    {
        DeviceControlException known => known,
        PnPDeviceNotFoundException => new DeviceControlException(DeviceControlException.DeviceNotFound, ex.Message, ex),
        ConfigManagerException cm => new DeviceControlException(DeviceControlException.ConfigManagerFailed, $"{cm.Message} (CONFIGRET {cm.Value}).", ex),
        Nefarius.Utilities.DeviceManagement.Exceptions.Win32Exception => new DeviceControlException(DeviceControlException.Win32Failed, ex.Message, ex),
        PlatformNotSupportedException or DllNotFoundException => new DeviceControlException(DeviceControlException.WindowsRequired, ex.Message, ex),
        InvalidOperationException or System.ComponentModel.Win32Exception =>
            new DeviceControlException(DeviceControlException.DeviceOperationFailed, $"{ex.GetType().Name}: {ex.Message}", ex),
        _ => null
    };
}
