using System.Net;
using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.Networking.WinSock;
using Windows.Win32.Networking.WinHttp;

using WmiLight;

using Vestigo.Performance;
using Wmi = Vestigo.Performance.WmiQuery;

namespace Vestigo.Diagnostics;

/// <summary>Read-only routes, neighbors, DNS client, and WinHTTP/IE proxy URLs. Never credentials.</summary>
public sealed unsafe class WindowsNetworkConfiguration : INetworkConfigurationSource
{
    private const string Scope = @"\\.\ROOT\StandardCimv2";
    private const uint ErrorAccessDenied = 5;

    public OperationResult<NetworkConfiguration> Read(int maxRoutes, int maxNeighbors)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<NetworkConfiguration>.Fail("windows_required", "Network configuration requires Windows.");
        }

        try
        {
            (IReadOnlyList<RouteEntry> routes, bool routePartial, bool routeTruncated, string? routeError) =
                ReadRoutes(Math.Clamp(maxRoutes, 1, 4096));
            (IReadOnlyList<NeighborEntry> neighbors, bool neighborPartial, bool neighborTruncated, string? neighborError) =
                ReadNeighbors(Math.Clamp(maxNeighbors, 1, 4096));
            DnsClientConfiguration dns = ReadDns();
            ProxyConfiguration proxy = ReadProxy(out bool proxyPartial, out string? proxyError);
            List<string> errors = [];
            if (routeError is not null) errors.Add(routeError);
            if (neighborError is not null) errors.Add(neighborError);
            if (proxyError is not null) errors.Add(proxyError);
            bool partial = routePartial || neighborPartial || proxyPartial;
            bool truncated = routeTruncated || neighborTruncated;
            return OperationResult<NetworkConfiguration>.Ok(new NetworkConfiguration(
                routes,
                neighbors,
                dns,
                proxy,
                partial,
                truncated,
                errors.Count == 0 ? null : string.Join(" ", errors)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OperationResult<NetworkConfiguration>.Fail("network_configuration_failed", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static (IReadOnlyList<RouteEntry> Rows, bool Partial, bool Truncated, string? Error) ReadRoutes(int limit)
    {
        MIB_IPFORWARD_TABLE2* table = null;
        WIN32_ERROR status = PInvoke.GetIpForwardTable2(ADDRESS_FAMILY.AF_UNSPEC, &table);
        if (status != 0 || table is null)
        {
            return ([], status == (WIN32_ERROR)ErrorAccessDenied, false, status == 0 ? "The routing table was empty." : $"GetIpForwardTable2 failed ({(uint)status}).");
        }

        try
        {
            List<RouteEntry> rows = [];
            uint count = table->NumEntries;
            bool truncated = count > (uint)limit;
            int take = (int)Math.Min(count, (uint)limit);
            for (int index = 0; index < take; index++)
            {
                ref MIB_IPFORWARD_ROW2 row = ref table->Table[index];
                rows.Add(new RouteEntry(
                    NativeIpAddress.FamilyName(row.DestinationPrefix.Prefix.si_family),
                    Prefix(row.DestinationPrefix),
                    row.DestinationPrefix.PrefixLength,
                    NativeIpAddress.Format(row.NextHop),
                    (int)row.InterfaceIndex,
                    (int)row.Metric,
                    row.Protocol.ToString(),
                    row.Origin.ToString()));
            }

            return (rows, false, truncated, null);
        }
        finally
        {
            PInvoke.FreeMibTable(table);
        }
    }

    private static (IReadOnlyList<NeighborEntry> Rows, bool Partial, bool Truncated, string? Error) ReadNeighbors(int limit)
    {
        MIB_IPNET_TABLE2* table = null;
        WIN32_ERROR status = PInvoke.GetIpNetTable2(ADDRESS_FAMILY.AF_UNSPEC, &table);
        if (status != 0 || table is null)
        {
            return ([], status == (WIN32_ERROR)ErrorAccessDenied, false, status == 0 ? "The neighbor table was empty." : $"GetIpNetTable2 failed ({(uint)status}).");
        }

        try
        {
            List<NeighborEntry> rows = [];
            uint count = table->NumEntries;
            bool truncated = count > (uint)limit;
            int take = (int)Math.Min(count, (uint)limit);
            for (int index = 0; index < take; index++)
            {
                ref MIB_IPNET_ROW2 row = ref table->Table[index];
                rows.Add(new NeighborEntry(
                    NativeIpAddress.FamilyName(row.Address.si_family),
                    NativeIpAddress.Format(row.Address),
                    (int)row.InterfaceIndex,
                    Physical(row),
                    row.State.ToString(),
                    row.IsRouter,
                    row.IsUnreachable));
            }

            return (rows, false, truncated, null);
        }
        finally
        {
            PInvoke.FreeMibTable(table);
        }
    }

    private static string Prefix(IP_ADDRESS_PREFIX prefix)
    {
        string address = NativeIpAddress.Format(prefix.Prefix);
        return $"{address}/{prefix.PrefixLength}";
    }

    private static string? Physical(in MIB_IPNET_ROW2 row)
    {
        int length = (int)row.PhysicalAddressLength;
        if (length == 0)
        {
            return null;
        }

        ReadOnlySpan<byte> bytes = row.PhysicalAddress.AsSpan();
        int take = Math.Min(length, bytes.Length);
        return BitConverter.ToString(bytes[..take].ToArray());
    }

    private static DnsClientConfiguration ReadDns()
    {
        List<string> servers = [];
        List<string> suffixes = [];
        foreach (WmiObject row in Wmi.Query(Scope, "SELECT ServerAddresses, ConnectionSpecificSuffix FROM MSFT_DNSClientServerAddress"))
        {
            AddAll(servers, Wmi.String(row, "ServerAddresses"));
        }

        foreach (WmiObject row in Wmi.Query(Scope, "SELECT SuffixSearchList, ConnectionSpecificSuffix FROM MSFT_DNSClientGlobalSetting"))
        {
            AddAll(suffixes, Wmi.String(row, "SuffixSearchList"));
            AddAll(suffixes, Wmi.String(row, "ConnectionSpecificSuffix"));
        }

        foreach (WmiObject row in Wmi.Query(Scope, "SELECT ConnectionSpecificSuffix FROM MSFT_DNSClient"))
        {
            AddAll(suffixes, Wmi.String(row, "ConnectionSpecificSuffix"));
        }

        List<string> nrpt = [];
        foreach (WmiObject row in Wmi.Query(Scope, "SELECT Name, Namespace, NameServers FROM MSFT_DnsClientNrptRule"))
        {
            string? name = Wmi.String(row, "Name") ?? Wmi.String(row, "Namespace");
            string? nameServers = Wmi.String(row, "NameServers");
            if (name is null && nameServers is null)
            {
                continue;
            }

            nrpt.Add(nameServers is null ? name! : $"{name}: {nameServers}");
        }

        return new DnsClientConfiguration(Distinct(servers), Distinct(suffixes), nrpt);
    }

    private static ProxyConfiguration ReadProxy(out bool partial, out string? error)
    {
        partial = false;
        error = null;
        bool? autoDetect = null;
        string? autoConfig = null;
        string? proxy = null;
        string? bypass = null;
        WINHTTP_CURRENT_USER_IE_PROXY_CONFIG ie = default;
        if (PInvoke.WinHttpGetIEProxyConfigForCurrentUser(&ie))
        {
            autoDetect = ie.fAutoDetect;
            autoConfig = Take(ie.lpszAutoConfigUrl);
            proxy = Take(ie.lpszProxy);
            bypass = Take(ie.lpszProxyBypass);
        }
        else
        {
            partial = true;
            error = "WinHTTP IE proxy configuration was unavailable.";
        }

        WINHTTP_PROXY_INFO info = default;
        if (PInvoke.WinHttpGetDefaultProxyConfiguration(&info))
        {
            proxy ??= Take(info.lpszProxy);
            bypass ??= Take(info.lpszProxyBypass);
        }
        else
        {
            partial = true;
            error = error is null ? "WinHTTP default proxy configuration was unavailable." : error + " WinHTTP default proxy configuration was unavailable.";
        }

        return new ProxyConfiguration(autoDetect, autoConfig, proxy, bypass);
    }

    private static string? Take(PWSTR value)
    {
        if (value.Value == null)
        {
            return null;
        }

        try
        {
            string? text = value.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        finally
        {
            _ = PInvoke.GlobalFree((HGLOBAL)(nint)value.Value);
        }
    }

    private static void AddAll(List<string> list, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (string part in value.Split([',', ';', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            list.Add(part);
        }
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string> values) =>
        values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
