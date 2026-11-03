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

/// <summary>
///     DNS, TCP, or TLS observation of one selected destination. This is not a measure of media quality,
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
    TlsObservation? Tls);
