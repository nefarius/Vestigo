namespace Vestigo.Performance;

public sealed record AdapterAddress(string Family, string Address, int? PrefixLength);

public sealed record AdapterCounters(
    long BytesReceived,
    long BytesSent,
    long UnicastPacketsReceived,
    long UnicastPacketsSent,
    long IncomingPacketsDiscarded,
    long IncomingPacketsWithErrors,
    long OutgoingPacketsDiscarded,
    long OutgoingPacketsWithErrors,
    long OutputQueueLength);

public sealed record AdapterOffloads(
    bool? RssEnabled,
    int? RssQueues,
    int? RssBaseProcessor,
    int? RssMaxProcessor,
    string? RssProfile,
    bool? RscIPv4,
    bool? RscIPv6,
    bool? LsoV2IPv4,
    bool? LsoV2IPv6,
    bool? ChecksumIPv4,
    bool? ChecksumTcpIPv4,
    bool? ChecksumTcpIPv6,
    bool? ChecksumUdpIPv4,
    bool? ChecksumUdpIPv6);

public sealed record AdapterAdvancedProperty(string? Name, string? Value, string? Keyword);

public sealed record NetworkAdapter(
    int? Index,
    string? Id,
    string? Name,
    string? Description,
    string? Type,
    string? OperationalStatus,
    long? SpeedBitsPerSecond,
    int? Mtu,
    int? VlanId,
    bool? FullDuplex,
    bool? DhcpEnabled,
    string? MacAddress,
    string? DriverName,
    string? DriverVersion,
    string? DriverFileName,
    IReadOnlyList<AdapterAddress> UnicastAddresses,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers,
    AdapterCounters? Counters,
    AdapterOffloads? Offloads,
    IReadOnlyList<AdapterAdvancedProperty> Advanced,
    string? Error);

public sealed record TcpStackTuning(
    string? SettingName,
    string? AutoTuningLevel,
    string? CongestionProvider,
    string? EcnCapability,
    bool? Timestamps,
    int? InitialCongestionWindow,
    int? DelayedAckTimeoutMs,
    int? MinRtoMs);

public sealed record NetworkTopology(
    IReadOnlyList<NetworkAdapter> Adapters,
    IReadOnlyList<TcpStackTuning> TcpSettings,
    bool Partial,
    string? Error);

public sealed record NdisBinding(
    string? Adapter,
    string? ComponentId,
    string? DisplayName,
    bool? Enabled,
    string Kind);

public sealed record WfpObject(
    string Kind,
    string? Name,
    string? Description,
    string? Key,
    string? ProviderKey);

public sealed record NetworkFilterList(
    IReadOnlyList<NdisBinding> Bindings,
    IReadOnlyList<WfpObject> Wfp,
    bool Partial,
    string? Error);

public sealed record ProtocolCounters(
    string Family,
    long? DatagramsReceived,
    long? DatagramsSent,
    long? IncomingDatagramsDiscarded,
    long? IncomingDatagramsWithErrors,
    long? ConnectionsAccepted,
    long? ConnectionsInitiated,
    long? FailedConnectionAttempts,
    long? ResetConnections,
    long? SegmentsRetransmitted,
    long? CurrentConnections,
    long? MessagesReceived,
    long? MessagesSent,
    long? IncomingMessagesDiscarded,
    long? IncomingMessagesWithErrors);

public sealed record IcmpCounters(string Family, long? EchoRequestsSent, long? EchoRepliesReceived, long? DestinationUnreachable, long? TimeExceeded);

public sealed record InterfaceCounters(
    string? Name,
    int? Index,
    long BytesReceived,
    long BytesSent,
    long IncomingPacketsDiscarded,
    long IncomingPacketsWithErrors,
    long OutgoingPacketsDiscarded,
    long OutgoingPacketsWithErrors);

public sealed record TcpStateCount(string State, int Count);

public sealed record EphemeralPortUse(int Start, int End, int Used, int Capacity, double Utilization);

public sealed record NetworkStackStats(
    IReadOnlyList<InterfaceCounters> Interfaces,
    IReadOnlyList<ProtocolCounters> Tcp,
    IReadOnlyList<ProtocolCounters> Udp,
    IReadOnlyList<IcmpCounters> Icmp,
    IReadOnlyList<TcpStateCount> TcpStates,
    int UdpBindings,
    EphemeralPortUse? Ephemeral,
    bool Partial,
    string? Error);

public sealed record ProcessNetworkSummary(
    int Pid,
    string? Name,
    long BytesSent,
    long BytesReceived,
    int SendCount,
    int ReceiveCount,
    int Retransmits,
    int Disconnects,
    int Failures);

public sealed record PacketDropCounter(string Component, string? Kind, long Received, long Dropped, string? DropReason);

public sealed record PacketDropResult(
    int DurationMilliseconds,
    IReadOnlyList<PacketDropCounter> Counters,
    long TotalDropped,
    bool Truncated,
    bool Partial,
    string? Error);

public interface INetworkTopologySource
{
    OperationResult<NetworkTopology> Read();
}

public interface INetworkFilterSource
{
    OperationResult<NetworkFilterList> List();
}

public interface INetworkStackStatsSource
{
    OperationResult<NetworkStackStats> Read();
}

public interface IPacketDropCapture
{
    OperationResult<string> PlanStart(TimeSpan duration);
    Task<OperationResult<PacketDropResult>> CaptureAsync(TimeSpan duration, CancellationToken cancellationToken);
}
