using System.Net.NetworkInformation;
using System.Net.Sockets;

using WmiLight;

namespace Vestigo.Performance;

/// <summary>Combines BCL adapter inventory with ROOT\StandardCimv2 offload and TCP-template facts.</summary>
public sealed class WindowsNetworkTopology : INetworkTopologySource
{
    private const string Scope = @"\\.\ROOT\StandardCimv2";

    public OperationResult<NetworkTopology> Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<NetworkTopology>.Fail("windows_required", "Network topology requires Windows.");
        }

        try
        {
            Dictionary<string, WmiObject> adapters = Index(WmiQuery.Query(Scope, "SELECT Name, InterfaceDescription, InterfaceIndex, InterfaceGuid, PermanentAddress, Speed, ReceiveLinkSpeed, TransmitLinkSpeed, FullDuplex, MtuSize, VlanID, DriverName, DriverVersionString, DriverDescription, DeviceID FROM MSFT_NetAdapter"), "Name", "InterfaceDescription");
            Dictionary<string, WmiObject> rss = Index(WmiQuery.Query(Scope, "SELECT Name, Enabled, NumberOfReceiveQueues, BaseProcessorNumber, MaxProcessorNumber, Profile FROM MSFT_NetAdapterRssSettingData"), "Name");
            Dictionary<string, WmiObject> rsc = Index(WmiQuery.Query(Scope, "SELECT Name, IPv4Enabled, IPv6Enabled FROM MSFT_NetAdapterRscSettingData"), "Name");
            Dictionary<string, WmiObject> lso = Index(WmiQuery.Query(Scope, "SELECT Name, V2IPv4Enabled, V2IPv6Enabled FROM MSFT_NetAdapterLsoSettingData"), "Name");
            Dictionary<string, WmiObject> checksum = Index(WmiQuery.Query(Scope, "SELECT Name, IpIPv4Enabled, TcpIPv4Enabled, TcpIPv6Enabled, UdpIPv4Enabled, UdpIPv6Enabled FROM MSFT_NetAdapterChecksumOffloadSettingData"), "Name");
            Dictionary<string, List<AdapterAdvancedProperty>> advanced = AdvancedByName();
            List<TcpStackTuning> tcp = TcpSettings();
            List<NetworkAdapter> list = [];
            bool partial = adapters.Count == 0;
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                list.Add(Map(nic, adapters, rss, rsc, lso, checksum, advanced));
            }

            return OperationResult<NetworkTopology>.Ok(new NetworkTopology(list, tcp, partial, partial ? "StandardCimv2 adapter metadata was unavailable." : null));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OperationResult<NetworkTopology>.Fail("network_topology_failed", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static NetworkAdapter Map(
        NetworkInterface nic,
        IReadOnlyDictionary<string, WmiObject> adapters,
        IReadOnlyDictionary<string, WmiObject> rss,
        IReadOnlyDictionary<string, WmiObject> rsc,
        IReadOnlyDictionary<string, WmiObject> lso,
        IReadOnlyDictionary<string, WmiObject> checksum,
        IReadOnlyDictionary<string, List<AdapterAdvancedProperty>> advanced)
    {
        IPInterfaceProperties properties = nic.GetIPProperties();
        IPv4InterfaceProperties? v4 = null;
        IPv6InterfaceProperties? v6 = null;
        try
        {
            v4 = properties.GetIPv4Properties();
        }
        catch (NetworkInformationException)
        {
        }

        try
        {
            v6 = properties.GetIPv6Properties();
        }
        catch (NetworkInformationException)
        {
        }

        AdapterCounters? counters = null;
        string? error = null;
        try
        {
            IPInterfaceStatistics stats = nic.GetIPStatistics();
            counters = new AdapterCounters(
                stats.BytesReceived,
                stats.BytesSent,
                stats.UnicastPacketsReceived,
                stats.UnicastPacketsSent,
                stats.IncomingPacketsDiscarded,
                stats.IncomingPacketsWithErrors,
                stats.OutgoingPacketsDiscarded,
                stats.OutgoingPacketsWithErrors,
                stats.OutputQueueLength);
        }
        catch (NetworkInformationException ex)
        {
            error = ex.Message;
        }

        WmiObject? row = Match(nic, adapters);
        string? name = nic.Name;
        rss.TryGetValue(name, out WmiObject? rssRow);
        if (rssRow is null && row is not null)
        {
            rss.TryGetValue(WmiQuery.String(row, "Name") ?? string.Empty, out rssRow);
        }

        rsc.TryGetValue(name, out WmiObject? rscRow);
        lso.TryGetValue(name, out WmiObject? lsoRow);
        checksum.TryGetValue(name, out WmiObject? checksumRow);
        advanced.TryGetValue(name, out List<AdapterAdvancedProperty>? props);
        AdapterOffloads offloads = new(
            WmiQuery.Boolean(rssRow, "Enabled"),
            rssRow is null ? null : WmiQuery.Int32(rssRow, "NumberOfReceiveQueues"),
            rssRow is null ? null : WmiQuery.Int32(rssRow, "BaseProcessorNumber"),
            rssRow is null ? null : WmiQuery.Int32(rssRow, "MaxProcessorNumber"),
            WmiQuery.String(rssRow, "Profile"),
            WmiQuery.Boolean(rscRow, "IPv4Enabled"),
            WmiQuery.Boolean(rscRow, "IPv6Enabled"),
            WmiQuery.Boolean(lsoRow, "V2IPv4Enabled"),
            WmiQuery.Boolean(lsoRow, "V2IPv6Enabled"),
            Enabled(checksumRow, "IpIPv4Enabled"),
            Enabled(checksumRow, "TcpIPv4Enabled"),
            Enabled(checksumRow, "TcpIPv6Enabled"),
            Enabled(checksumRow, "UdpIPv4Enabled"),
            Enabled(checksumRow, "UdpIPv6Enabled"));

        return new NetworkAdapter(
            v4?.Index ?? v6?.Index ?? (row is null ? null : WmiQuery.Int32(row, "InterfaceIndex")),
            nic.Id,
            nic.Name,
            nic.Description,
            nic.NetworkInterfaceType.ToString(),
            nic.OperationalStatus.ToString(),
            nic.Speed >= 0 ? nic.Speed : row is null ? null : WmiQuery.Int64(row, "ReceiveLinkSpeed") ?? WmiQuery.Int64(row, "TransmitLinkSpeed") ?? WmiQuery.Int64(row, "Speed"),
            v4?.Mtu ?? v6?.Mtu ?? (row is null ? null : WmiQuery.Int32(row, "MtuSize")),
            row is null ? null : WmiQuery.Int32(row, "VlanID"),
            WmiQuery.Boolean(row, "FullDuplex"),
            v4?.IsDhcpEnabled,
            FormatMac(nic.GetPhysicalAddress()) ?? WmiQuery.String(row, "PermanentAddress"),
            WmiQuery.String(row, "DriverDescription") ?? WmiQuery.String(row, "DriverName"),
            WmiQuery.String(row, "DriverVersionString"),
            WmiQuery.String(row, "DriverName"),
            Addresses(properties),
            properties.GatewayAddresses.Select(item => item.Address.ToString()).Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            properties.DnsAddresses.Select(item => item.ToString()).ToList(),
            counters,
            offloads,
            props ?? [],
            error);
    }

    private static IReadOnlyList<AdapterAddress> Addresses(IPInterfaceProperties properties)
    {
        List<AdapterAddress> list = [];
        foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
        {
            string family = address.Address.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4";
            int? prefix = null;
            try
            {
                prefix = address.PrefixLength;
            }
            catch (PlatformNotSupportedException)
            {
            }

            list.Add(new AdapterAddress(family, address.Address.ToString(), prefix));
        }

        return list;
    }

    private static List<TcpStackTuning> TcpSettings()
    {
        List<TcpStackTuning> list = [];
        foreach (WmiObject row in WmiQuery.Query(Scope, "SELECT SettingName, AutoTuningLevelLocal, CongestionProvider, EcnCapability, Timestamps, InitialCongestionWindow, DelayedAckTimeout, MinRto FROM MSFT_NetTCPSetting"))
        {
            list.Add(new TcpStackTuning(
                WmiQuery.String(row, "SettingName"),
                WmiQuery.String(row, "AutoTuningLevelLocal"),
                WmiQuery.String(row, "CongestionProvider"),
                WmiQuery.String(row, "EcnCapability"),
                WmiQuery.Boolean(row, "Timestamps"),
                WmiQuery.Int32(row, "InitialCongestionWindow"),
                WmiQuery.Int32(row, "DelayedAckTimeout"),
                WmiQuery.Int32(row, "MinRto")));
        }

        return list;
    }

    private static Dictionary<string, List<AdapterAdvancedProperty>> AdvancedByName()
    {
        Dictionary<string, List<AdapterAdvancedProperty>> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (WmiObject row in WmiQuery.Query(Scope, "SELECT Name, DisplayName, DisplayValue, RegistryKeyword FROM MSFT_NetAdapterAdvancedPropertySettingData"))
        {
            string? name = WmiQuery.String(row, "Name");
            if (name is null)
            {
                continue;
            }

            if (!map.TryGetValue(name, out List<AdapterAdvancedProperty>? list))
            {
                list = [];
                map[name] = list;
            }

            if (list.Count >= 32)
            {
                continue;
            }

            string? display = WmiQuery.String(row, "DisplayName");
            string? keyword = WmiQuery.String(row, "RegistryKeyword");
            if (display is null && keyword is null)
            {
                continue;
            }

            list.Add(new AdapterAdvancedProperty(display, WmiQuery.String(row, "DisplayValue"), keyword));
        }

        return map;
    }

    private static Dictionary<string, WmiObject> Index(IReadOnlyList<WmiObject> rows, params string[] keys)
    {
        Dictionary<string, WmiObject> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (WmiObject row in rows)
        {
            foreach (string key in keys)
            {
                string? value = WmiQuery.String(row, key);
                if (value is not null)
                {
                    map[value] = row;
                }
            }
        }

        return map;
    }

    private static WmiObject? Match(NetworkInterface nic, IReadOnlyDictionary<string, WmiObject> adapters)
    {
        if (adapters.TryGetValue(nic.Name, out WmiObject? row) || adapters.TryGetValue(nic.Description, out row))
        {
            return row;
        }

        string id = nic.Id.Trim('{', '}');
        foreach (WmiObject candidate in adapters.Values)
        {
            string? guid = WmiQuery.String(candidate, "InterfaceGuid")?.Trim('{', '}');
            if (guid is not null && guid.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool? Enabled(WmiObject? row, string name)
    {
        if (row is null)
        {
            return null;
        }

        bool? flag = WmiQuery.Boolean(row, name);
        if (flag is not null)
        {
            return flag;
        }

        int? number = WmiQuery.Int32(row, name);
        return number is null ? null : number != 0;
    }

    private static string? FormatMac(PhysicalAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? null : BitConverter.ToString(bytes);
    }
}
