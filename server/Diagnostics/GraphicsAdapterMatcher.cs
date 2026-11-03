using System.Text.RegularExpressions;

using Vestigo.Devices;

namespace Vestigo.Diagnostics;

/// <summary>Matches a DXGI PCI id to a Display-class device node. A tie leaves the instance unset.</summary>
internal static partial class GraphicsAdapterMatcher
{
    internal const string Exact = "exact";

    internal const string Subsystem = "subsystem";

    internal const string Device = "device";

    internal const string Ambiguous = "ambiguous";

    internal static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");

    public static (string? InstanceId, string? Confidence) Match(
        uint? vendorId,
        uint? deviceId,
        uint? subsystemId,
        uint? revision,
        IReadOnlyList<DeviceEntry> devices)
    {
        if (vendorId is null || deviceId is null)
        {
            return (null, null);
        }

        List<(DeviceEntry Device, int Score)> hits = [];
        foreach (DeviceEntry device in devices)
        {
            if (device.ClassGuid != DisplayClass)
            {
                continue;
            }

            int score = Score(vendorId.Value, deviceId.Value, subsystemId, revision, device.HardwareIds);
            if (score > 0)
            {
                hits.Add((device, score));
            }
        }

        if (hits.Count == 0)
        {
            return (null, null);
        }

        int best = hits.Max(hit => hit.Score);
        List<DeviceEntry> winners = hits.Where(hit => hit.Score == best).Select(hit => hit.Device).ToList();
        List<DeviceEntry> present = winners.Where(device => device.IsPresent).ToList();
        if (present.Count == 1)
        {
            winners = present;
        }

        if (winners.Count != 1)
        {
            return (null, Ambiguous);
        }

        return (winners[0].InstanceId, best switch
        {
            3 => Exact,
            2 => Subsystem,
            _ => Device
        });
    }

    internal static uint? Id(uint value) => value == 0 ? null : value;

    internal static ulong? Bytes(nuint value) => value == 0 ? null : (ulong)value;

    internal static string? Luid(int high, uint low) => high == 0 && low == 0 ? null : $"{high:X8}:{low:X8}";

    internal static string? DriverVersion(long version)
    {
        if (version == 0)
        {
            return null;
        }

        ushort product = (ushort)((version >> 48) & 0xFFFF);
        ushort ver = (ushort)((version >> 32) & 0xFFFF);
        ushort sub = (ushort)((version >> 16) & 0xFFFF);
        ushort build = (ushort)(version & 0xFFFF);
        return $"{product}.{ver}.{sub}.{build}";
    }

    private static int Score(uint vendor, uint device, uint? subsystem, uint? revision, IReadOnlyList<string> hardwareIds)
    {
        int best = 0;
        foreach (string id in hardwareIds)
        {
            Match match = Pci().Match(id);
            if (!match.Success)
            {
                continue;
            }

            if (!Hex(match.Groups[1].Value, out uint ven) || ven != vendor ||
                !Hex(match.Groups[2].Value, out uint dev) || dev != device)
            {
                continue;
            }

            best = Math.Max(best, 1);
            if (subsystem is null || !match.Groups[3].Success || !Hex(match.Groups[3].Value, out uint sub) || sub != subsystem)
            {
                continue;
            }

            best = Math.Max(best, 2);
            if (revision is null || !match.Groups[4].Success || !Hex(match.Groups[4].Value, out uint rev) || rev != (revision & 0xFF))
            {
                continue;
            }

            best = 3;
        }

        return best;
    }

    private static bool Hex(string text, out uint value) =>
        uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out value);

    [GeneratedRegex(@"VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})(?:&SUBSYS_([0-9A-F]{8}))?(?:&REV_([0-9A-F]{2}))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pci();
}
