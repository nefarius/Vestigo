using System.Net.NetworkInformation;
using System.Net.Sockets;

using Vestigo.Diagnostics;

namespace Vestigo.Performance;

/// <summary>BCL interface/MIB counters plus IP Helper TCP state and ephemeral-port utilization.</summary>
public sealed class WindowsNetworkStackStats : INetworkStackStatsSource
{
    public OperationResult<NetworkStackStats> Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<NetworkStackStats>.Fail("windows_required", "Network stack statistics require Windows.");
        }

        try
        {
            List<InterfaceCounters> interfaces = [];
            bool partial = false;
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    IPInterfaceStatistics stats = nic.GetIPStatistics();
                    int? index = null;
                    try
                    {
                        index = nic.GetIPProperties().GetIPv4Properties().Index;
                    }
                    catch (NetworkInformationException)
                    {
                    }

                    interfaces.Add(new InterfaceCounters(
                        nic.Name,
                        index,
                        stats.BytesReceived,
                        stats.BytesSent,
                        stats.IncomingPacketsDiscarded,
                        stats.IncomingPacketsWithErrors,
                        stats.OutgoingPacketsDiscarded,
                        stats.OutgoingPacketsWithErrors));
                }
                catch (NetworkInformationException)
                {
                    partial = true;
                }
            }

            IPGlobalProperties global = IPGlobalProperties.GetIPGlobalProperties();
            List<ProtocolCounters> tcp = [];
            List<ProtocolCounters> udp = [];
            List<IcmpCounters> icmp = [];
            TryAddTcp(tcp, "ipv4", () => global.GetTcpIPv4Statistics());
            TryAddTcp(tcp, "ipv6", () => global.GetTcpIPv6Statistics(), ref partial);
            TryAddUdp(udp, "ipv4", () => global.GetUdpIPv4Statistics());
            TryAddUdp(udp, "ipv6", () => global.GetUdpIPv6Statistics(), ref partial);
            TryAddIcmp(icmp, "ipv4", () => global.GetIcmpV4Statistics(), ref partial);
            TryAddIcmp(icmp, "ipv6", () => global.GetIcmpV6Statistics(), ref partial);

            TableRead table = IpHelperConnections.ReadAll(16_384);
            IReadOnlyList<TcpStateCount> states = [];
            int udpBindings = 0;
            EphemeralPortUse? ephemeral = null;
            if (table.Success)
            {
                states = NetworkConnectionProjection.Histogram(table.Rows)
                    .Select(item => new TcpStateCount(item.State, item.Count))
                    .ToList();
                udpBindings = NetworkConnectionProjection.UdpCount(table.Rows);
                ephemeral = NetworkConnectionProjection.Ephemeral(table.Rows);
                partial |= table.Partial;
            }
            else
            {
                partial = true;
            }

            return OperationResult<NetworkStackStats>.Ok(new NetworkStackStats(
                interfaces,
                tcp,
                udp,
                icmp,
                states,
                udpBindings,
                ephemeral,
                partial,
                partial ? "One or more protocol families or connection tables were unavailable." : null));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OperationResult<NetworkStackStats>.Fail("network_stack_stats_failed", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryAddTcp(List<ProtocolCounters> list, string family, Func<TcpStatistics> read, ref bool partial)
    {
        try
        {
            TryAddTcp(list, family, read);
        }
        catch (NetworkInformationException)
        {
            partial = true;
        }
        catch (SocketException)
        {
            partial = true;
        }
    }

    private static void TryAddTcp(List<ProtocolCounters> list, string family, Func<TcpStatistics> read)
    {
        TcpStatistics stats = read();
        list.Add(new ProtocolCounters(
            family,
            stats.SegmentsReceived,
            stats.SegmentsSent,
            null,
            stats.ErrorsReceived,
            stats.ConnectionsAccepted,
            stats.ConnectionsInitiated,
            stats.FailedConnectionAttempts,
            stats.ResetConnections,
            stats.SegmentsResent,
            stats.CurrentConnections,
            null,
            null,
            null,
            null));
    }

    private static void TryAddUdp(List<ProtocolCounters> list, string family, Func<UdpStatistics> read, ref bool partial)
    {
        try
        {
            TryAddUdp(list, family, read);
        }
        catch (NetworkInformationException)
        {
            partial = true;
        }
        catch (SocketException)
        {
            partial = true;
        }
    }

    private static void TryAddUdp(List<ProtocolCounters> list, string family, Func<UdpStatistics> read)
    {
        UdpStatistics stats = read();
        list.Add(new ProtocolCounters(
            family,
            stats.DatagramsReceived,
            stats.DatagramsSent,
            stats.IncomingDatagramsDiscarded,
            stats.IncomingDatagramsWithErrors,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null));
    }

    private static void TryAddIcmp(List<IcmpCounters> list, string family, Func<IcmpV4Statistics> read, ref bool partial)
    {
        try
        {
            IcmpV4Statistics stats = read();
            list.Add(new IcmpCounters(
                family,
                stats.EchoRequestsSent,
                stats.EchoRepliesReceived,
                stats.DestinationUnreachableMessagesReceived,
                stats.TimeExceededMessagesReceived));
        }
        catch (NetworkInformationException)
        {
            partial = true;
        }
        catch (SocketException)
        {
            partial = true;
        }
    }

    private static void TryAddIcmp(List<IcmpCounters> list, string family, Func<IcmpV6Statistics> read, ref bool partial)
    {
        try
        {
            IcmpV6Statistics stats = read();
            list.Add(new IcmpCounters(
                family,
                stats.EchoRequestsSent,
                stats.EchoRepliesReceived,
                stats.DestinationUnreachableMessagesReceived,
                stats.TimeExceededMessagesReceived));
        }
        catch (NetworkInformationException)
        {
            partial = true;
        }
        catch (SocketException)
        {
            partial = true;
        }
    }
}
