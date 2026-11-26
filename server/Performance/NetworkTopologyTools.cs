using System.ComponentModel;

using ModelContextProtocol.Server;

namespace Vestigo.Performance;

/// <summary>Read-only adapter, offload, NDIS/WFP filter, and stack-stat inventory.</summary>
[McpServerToolType]
public sealed class NetworkTopologyTools(
    INetworkTopologySource topology,
    INetworkFilterSource filters,
    INetworkStackStatsSource stack)
{
    [McpServerTool(Name = "get_network_topology", ReadOnly = true)]
    [Description(
        "Returns per-adapter interface index, link speed, duplex, media state, MTU, VLAN, MAC, IPv4/IPv6, gateways, DNS, DHCP, "
        + "driver name/version, 64-bit octet/packet/discard/error counters, RSS/RSC/LSO/checksum offloads, selected advanced "
        + "properties, and global TCP template tuning. TCP Timestamps is disabled, enabled, or allowed. partial is true when a "
        + "StandardCimv2 query fails or advanced properties are truncated. Combines System.Net.NetworkInformation with "
        + "ROOT\\StandardCimv2. No packet payloads are captured.")]
    public OperationResult<NetworkTopology> GetNetworkTopology() => topology.Read();

    [McpServerTool(Name = "list_network_filters", ReadOnly = true)]
    [Description(
        "Lists NDIS Lightweight Filter bindings per adapter and WFP callouts, sublayers, and providers. "
        + "Use this to identify AV, EDR, or VPN drivers in the network path, parallel to list_filesystem_minifilters. "
        + "partial is true when WFP returns ACCESS_DENIED. No filter configuration is changed.")]
    public OperationResult<NetworkFilterList> ListNetworkFilters() => filters.List();

    [McpServerTool(Name = "get_network_stack_stats", ReadOnly = true)]
    [Description(
        "Returns 64-bit per-interface octet/packet/error/discard counters plus IPv4/IPv6 TCP, UDP, IP, and ICMP MIB statistics, "
        + "a TCP state histogram, UDP binding count, and ephemeral-port pool utilization. This is a snapshot, not a capture.")]
    public OperationResult<NetworkStackStats> GetNetworkStackStats() => stack.Read();
}
