using Nefarius.Utilities.DeviceManagement.Drivers;
using Nefarius.Utilities.DeviceManagement.Exceptions;
using Nefarius.Utilities.DeviceManagement.PnP;

namespace Vestigo.Devices;

/// <summary>
///     <see cref="IDeviceControl" /> over Nefarius.Utilities.DeviceManagement 6.2.0. Restart, enable and
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

    public IReadOnlyList<DeviceEntry> EnumerateDevices(bool presentOnly) =>
        Query(() => (IReadOnlyList<DeviceEntry>)Devcon.EnumerateDevices(presentOnly)
            .Select(d => new DeviceEntry(d.InstanceId, d.Description, d.ClassGuid, d.ClassName, d.HardwareIds ?? [], d.Service, d.DriverInfPath, d.IsPresent))
            .ToList());

    public DeviceDetails GetDevice(string instanceId) => Query(() =>
    {
        PnPDevice device = PnPDevice.GetDeviceByInstanceId(instanceId);
        DeviceEntry? summary = Devcon.EnumerateDevices(presentOnly: false)
            .Where(d => string.Equals(d.InstanceId, device.InstanceId, StringComparison.OrdinalIgnoreCase))
            .Select(d => new DeviceEntry(d.InstanceId, d.Description, d.ClassGuid, d.ClassName, d.HardwareIds ?? [], d.Service, d.DriverInfPath, d.IsPresent))
            .FirstOrDefault();

        return new DeviceDetails(
            device.InstanceId,
            device.DeviceId,
            device.HardwareIds?.ToList() ?? [],
            device.CompatibleIds?.ToList() ?? [],
            summary?.Description,
            summary?.ClassName,
            summary?.ClassGuid,
            summary?.Service ?? Optional<string>(device, DevicePropertyKey.Device_Service),
            OptionalStruct<bool>(device, DevicePropertyKey.Device_IsPresent) is { } present ? present : summary?.IsPresent,
            OptionalStruct<uint>(device, DevicePropertyKey.Device_ProblemCode),
            OptionalStruct<uint>(device, DevicePropertyKey.Device_DevNodeStatus),
            Optional<string>(device, DevicePropertyKey.Device_Driver),
            Optional<string>(device, DevicePropertyKey.Device_DriverDesc),
            Optional<string>(device, DevicePropertyKey.Device_DriverProvider),
            Optional<string>(device, DevicePropertyKey.Device_DriverVersion),
            OptionalStruct<DateTimeOffset>(device, DevicePropertyKey.Device_DriverDate),
            Optional<string>(device, DevicePropertyKey.Device_DriverInfPath) ?? summary?.DriverInfPath);
    });

    public ClassFilterSet GetClassFilters(Guid classGuid) => Query(() => new ClassFilterSet(
        DeviceClassFilters.GetUpper(classGuid)?.ToList() ?? [],
        DeviceClassFilters.GetLower(classGuid)?.ToList() ?? []));

    public IReadOnlyList<DriverStoreEntry> GetDriverStore(bool includeInbox) =>
        Query(() => (IReadOnlyList<DriverStoreEntry>)DriverStore.GetPackages(includeInbox)
            .Select(p => new DriverStoreEntry(
                p.PublishedName,
                p.OriginalInfName,
                p.Provider,
                p.ClassName,
                p.ClassGuid,
                p.DriverVersion?.ToString(),
                p.DriverDate,
                p.Architecture,
                p.IsInbox,
                p.StoreInfPath,
                p.BoundDeviceInstanceIds ?? []))
            .ToList());

    /// <summary>Reads one device property; a missing or unreadable property yields null instead of failing the query.</summary>
    private static T? Optional<T>(PnPDevice device, DevicePropertyKey key)
        where T : class
    {
        try
        {
            return device.GetProperty<T>(key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static T? OptionalStruct<T>(PnPDevice device, DevicePropertyKey key)
        where T : struct
    {
        try
        {
            return device.GetProperty<T>(key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static T Query<T>(Func<T> query)
    {
        try
        {
            return query();
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
