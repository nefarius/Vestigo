using Vestigo.Abstractions;
using Vestigo.Tools;

namespace Vestigo.Devices;

/// <summary>Bounds HID capability reads to the interfaces of one device, or to a capped usage scan.</summary>
public sealed class HidService(IDeviceControl devices, IHidPlatform hid)
{
    public const int MaxInterfaces = 16;
    public const int MaxUsageScan = 128;

    public OperationResult<HidInspection> Inspect(string instanceId)
    {
        if (!DeviceTools.IsValidInstanceId(instanceId))
        {
            return Invalid<HidInspection>();
        }

        try
        {
            DeviceDetails details = devices.GetDevice(instanceId);
            IReadOnlyList<DeviceInterfaceMatch> found = devices.FindByInterface(hid.InterfaceGuid, presentOnly: true, DeviceTools.MaxInterfaceResults);
            List<DeviceInterfaceMatch> mine = found
                .Where(match => string.Equals(match.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
                .Take(MaxInterfaces + 1)
                .ToList();
            bool truncated = found.Count >= DeviceTools.MaxInterfaceResults || mine.Count > MaxInterfaces;
            List<HidInterfaceInspection> interfaces = mine
                .Take(MaxInterfaces)
                .Select(match => hid.Inspect(match.DevicePath))
                .ToList();
            return OperationResult<HidInspection>.Ok(new HidInspection(details.InstanceId, interfaces, truncated));
        }
        catch (DeviceControlException ex)
        {
            return OperationResult<HidInspection>.Fail(ex.Code, ex.Message);
        }
    }

    public OperationResult<HidUsageList> ListUsages(int? usagePage)
    {
        if (usagePage is < 0 or > ushort.MaxValue)
        {
            return OperationResult<HidUsageList>.Fail("invalid_request", "usagePage must be from 0 through 65535.");
        }

        try
        {
            IReadOnlyList<DeviceInterfaceMatch> found = devices.FindByInterface(hid.InterfaceGuid, presentOnly: true, MaxUsageScan);
            List<HidUsageMatch> matches = [];
            foreach (DeviceInterfaceMatch match in found)
            {
                HidUsageMatch usage = hid.ReadUsage(match.InstanceId, match.DevicePath);
                if (usagePage is null || usage.UsagePage == usagePage)
                {
                    matches.Add(usage);
                }
            }

            return OperationResult<HidUsageList>.Ok(new HidUsageList(usagePage, matches, found.Count >= MaxUsageScan));
        }
        catch (DeviceControlException ex)
        {
            return OperationResult<HidUsageList>.Fail(ex.Code, ex.Message);
        }
    }

    private static OperationResult<T> Invalid<T>() =>
        OperationResult<T>.Fail(
            "invalid_instance_id",
            $"Instance ID must be 1-{DeviceTools.MaxInstanceIdLength} characters, contain a '\\', and have no control characters or leading '-'.");
}
