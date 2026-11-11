namespace Vestigo.Diagnostics;

/// <summary>One DNS answer. <see cref="Address"/> is null unless resolved addresses were requested.</summary>
public sealed record DnsObservation(string Family, string? Address);

/// <summary>One TCP connect attempt. <see cref="Error"/> is a socket error name, never a message that contains an address.</summary>
public sealed record TcpObservation(string? Address, bool Connected, string? Error);

/// <summary>TLS handshake observation. Authentication is false when certificate validation fails.</summary>
public sealed record TlsObservation(
    bool Authenticated,
    string? Protocol,
    string? Subject,
    string? Thumbprint,
    IReadOnlyList<string> ChainStatus);

/// <summary>One UDP datagram. <see cref="Reachability" /> says what the outcome does not prove.</summary>
public sealed record UdpObservation(string Outcome, int BytesSent, int BytesReceived, string Reachability, StunObservation? Stun)
{
    public const string SentDoesNotProveReachability = "A sent datagram does not prove the peer is reachable. A blocked UDP port fails silently, and a timeout is not a firewall block.";

    public const string StunSilent = "No matching STUN binding response arrived. That does not prove a firewall blocked the port.";

    public const string StunAnswered = "A matching STUN binding response arrived. The mapped address is unauthenticated and is not a firewall verdict.";
}

/// <summary>A STUN Binding result. <see cref="Reflexive" /> is true only for XOR-MAPPED-ADDRESS.</summary>
public sealed record StunObservation(bool Responded, string? MappedAddress, int? MappedPort, bool Reflexive);

/// <summary>
///     DNS, TCP, TLS, or UDP observation of one selected destination. This is not a measure of media quality,
///     and it contains no payload, headers, or credentials.
/// </summary>
public sealed record EndpointProbeResult(
    string Host,
    int Port,
    string Protocol,
    DateTimeOffset ObservedAt,
    bool TimedOut,
    string? DnsError,
    IReadOnlyList<DnsObservation> Answers,
    IReadOnlyList<TcpObservation> Tcp,
    TlsObservation? Tls,
    UdpObservation? Udp = null);
