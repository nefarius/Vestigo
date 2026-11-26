namespace Vestigo.Diagnostics;

public sealed record RouteEntry(
    string Family,
    string Destination,
    int PrefixLength,
    string? NextHop,
    int InterfaceIndex,
    int Metric,
    string? Protocol,
    string? Origin);

public sealed record NeighborEntry(
    string Family,
    string Address,
    int InterfaceIndex,
    string? PhysicalAddress,
    string? State,
    bool? IsRouter,
    bool? IsUnreachable);

public sealed record DnsClientConfiguration(
    IReadOnlyList<string> Servers,
    IReadOnlyList<string> SearchSuffixes,
    IReadOnlyList<string> NrptRules);

public sealed record ProxyConfiguration(
    bool? AutoDetect,
    string? AutoConfigUrl,
    string? Proxy,
    string? Bypass);

public sealed record NetworkConfiguration(
    IReadOnlyList<RouteEntry> Routes,
    IReadOnlyList<NeighborEntry> Neighbors,
    DnsClientConfiguration Dns,
    ProxyConfiguration Proxy,
    bool Partial,
    bool Truncated,
    string? Error);

public sealed record NetworkConnection(
    int Pid,
    string? ProcessName,
    string Protocol,
    string Family,
    string? State,
    string? LocalAddress,
    int? LocalPort,
    string? RemoteAddress,
    int? RemotePort);

public sealed record NetworkConnectionList(
    DateTimeOffset ObservedAt,
    IReadOnlyList<NetworkConnection> Connections,
    IReadOnlyList<TcpStateCount> TcpStates,
    int UdpBindings,
    bool Partial,
    bool Truncated);

public sealed record TcpStateCount(string State, int Count);

public sealed record TcpConnectionStats(
    string Family,
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int RemotePort,
    int Pid,
    string? ProcessName,
    double? SmoothedRttMs,
    double? RttVarianceMs,
    double? MinRttMs,
    double? MaxRttMs,
    long? CongestionWindow,
    long? SlowStartThreshold,
    long? Retransmits,
    long? DuplicateAcks,
    long? Sacks,
    long? ZeroWindow,
    long? BytesAcked,
    bool CollectionEnabled,
    string? Error);

public sealed record TcpConnectionStatsList(
    int DurationMilliseconds,
    IReadOnlyList<TcpConnectionStats> Connections,
    bool Partial);

public sealed record LatencySample(string Status, double? RttMs, string? Error);

public sealed record NetworkLatencyResult(
    string Host,
    string Protocol,
    int Port,
    int Sent,
    int Received,
    double LossPercent,
    Vestigo.Performance.PercentileStats RttMs,
    double JitterMs,
    IReadOnlyList<LatencySample> Samples,
    bool TimedOut);

public sealed record PathHop(
    int Ttl,
    string? Address,
    Vestigo.Performance.PercentileStats RttMs,
    int Sent,
    int Received,
    double LossPercent,
    bool Destination);

public sealed record PathMtu(int Bytes, bool Blackhole, string? Status);

public sealed record NetworkPathResult(
    string Host,
    IReadOnlyList<PathHop> Hops,
    PathMtu? Pmtu,
    bool Truncated);

public interface INetworkConfigurationSource
{
    OperationResult<NetworkConfiguration> Read(int maxRoutes, int maxNeighbors);
}

public interface ITcpEstatsSource
{
    Task<OperationResult<TcpConnectionStatsList>> InspectAsync(
        string? localAddress,
        int? localPort,
        string? remoteAddress,
        int? remotePort,
        int? pid,
        int limit,
        TimeSpan observe,
        CancellationToken cancellationToken);
}

public interface INetworkLatencyProbe
{
    Task<OperationResult<NetworkLatencyResult>> ProbeAsync(
        string host,
        int port,
        string protocol,
        int samples,
        TimeSpan timeout,
        bool includeResolvedAddress,
        CancellationToken cancellationToken);
}

public interface INetworkPathProbe
{
    Task<OperationResult<NetworkPathResult>> ProbeAsync(
        string host,
        int maxHops,
        int samplesPerHop,
        TimeSpan hopTimeout,
        bool includeHopAddresses,
        bool discoverPmtu,
        CancellationToken cancellationToken);
}
