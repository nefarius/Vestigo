using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Vestigo.Configuration;

namespace Vestigo.Diagnostics;

/// <summary>DNS, TCP, TLS, and UDP observations using the process that hosts Vestigo. Certificate validation is not bypassed.</summary>
public sealed class SocketEndpointProbe(VestigoOptions options) : IEndpointProbe
{
    public const int MaxAnswers = 8;

    public async Task<EndpointProbeResult> ProbeAsync(
        string host,
        int port,
        string protocol,
        TimeSpan timeout,
        bool includeResolvedAddress,
        bool stun,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        CancellationToken token = budget.Token;
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        long dnsStarted = Stopwatch.GetTimestamp();
        (IReadOnlyList<IPAddress> addresses, string? dnsError, bool timedOut) resolved = await Resolve(host, token, cancellationToken).ConfigureAwait(false);
        double dnsMs = Stopwatch.GetElapsedTime(dnsStarted).TotalMilliseconds;
        List<DnsObservation> answers = [];
        foreach (IPAddress address in resolved.addresses)
        {
            answers.Add(new DnsObservation(Family(address), includeResolvedAddress ? address.ToString() : null));
        }

        if (protocol == "dns" || answers.Count == 0)
        {
            return new EndpointProbeResult(host, port, protocol, observedAt, resolved.timedOut, resolved.dnsError, answers, [], null, null, dnsMs);
        }

        if (protocol == "udp")
        {
            return await SendUdp(host, port, protocol, observedAt, resolved.addresses, resolved.timedOut, resolved.dnsError, answers, stun, dnsMs, token, cancellationToken).ConfigureAwait(false);
        }

        List<TcpObservation> attempts = [];
        TlsObservation? tls = null;
        bool timedOut = resolved.timedOut;
        double? connectMs = null;
        double? tlsMs = null;
        foreach (IPAddress address in resolved.addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.IsCancellationRequested)
            {
                timedOut = true;
                break;
            }

            (TcpObservation tcp, TlsObservation? handshake, bool attemptTimedOut, double connectDuration, double? handshakeDuration) = await Connect(
                host,
                port,
                protocol,
                address,
                includeResolvedAddress,
                options.Diagnostics.AllowOnlineRevocation,
                token,
                cancellationToken).ConfigureAwait(false);
            attempts.Add(tcp);
            timedOut |= attemptTimedOut;
            connectMs ??= connectDuration;
            if (handshake is not null)
            {
                tls = handshake;
                tlsMs = handshakeDuration;
                break;
            }

            if (tcp.Connected)
            {
                break;
            }
        }

        return new EndpointProbeResult(host, port, protocol, observedAt, timedOut, resolved.dnsError, answers, attempts, tls, null, dnsMs, connectMs, tlsMs);
    }

    private async Task<EndpointProbeResult> SendUdp(
        string host,
        int port,
        string protocol,
        DateTimeOffset observedAt,
        IReadOnlyList<IPAddress> addresses,
        bool timedOut,
        string? dnsError,
        IReadOnlyList<DnsObservation> answers,
        bool stun,
        double dnsMs,
        CancellationToken token,
        CancellationToken caller)
    {
        byte[] transaction = new byte[StunBinding.TransactionLength];
        RandomNumberGenerator.Fill(transaction);
        byte[] payload = stun ? StunBinding.CreateRequest(transaction) : [0x00];
        if (payload.Length > options.Diagnostics.MaxUdpPayloadBytes)
        {
            return Result(host, port, protocol, observedAt, false, dnsError, answers, new UdpObservation("send_failed", 0, 0, UdpObservation.SentDoesNotProveReachability, null), dnsMs);
        }

        foreach (IPAddress address in addresses)
        {
            caller.ThrowIfCancellationRequested();
            if (token.IsCancellationRequested)
            {
                return Result(host, port, protocol, observedAt, true, dnsError, answers, new UdpObservation("send_failed", 0, 0, UdpObservation.SentDoesNotProveReachability, null), dnsMs);
            }

            try
            {
                using Socket socket = new(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                long sentAt = Stopwatch.GetTimestamp();
                int sent = await socket.SendToAsync(payload, SocketFlags.None, new IPEndPoint(address, port), token).ConfigureAwait(false);
                if (sent != payload.Length)
                {
                    continue;
                }

                if (!stun)
                {
                    return Result(host, port, protocol, observedAt, timedOut, dnsError, answers, new UdpObservation("sent", sent, 0, UdpObservation.SentDoesNotProveReachability, null), dnsMs);
                }

                return await WaitForStun(socket, address, port, host, protocol, observedAt, dnsError, answers, transaction, sent, dnsMs, sentAt, token, caller).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
            {
                return Result(host, port, protocol, observedAt, true, dnsError, answers, new UdpObservation("send_failed", 0, 0, UdpObservation.SentDoesNotProveReachability, null), dnsMs);
            }
            catch (SocketException)
            {
                // Try the next resolved address. A socket error is not a firewall verdict.
            }
        }

        return Result(host, port, protocol, observedAt, timedOut, dnsError, answers, new UdpObservation("send_failed", 0, 0, UdpObservation.SentDoesNotProveReachability, null), dnsMs);
    }

    private static async Task<EndpointProbeResult> WaitForStun(
        Socket socket,
        IPAddress address,
        int port,
        string host,
        string protocol,
        DateTimeOffset observedAt,
        string? dnsError,
        IReadOnlyList<DnsObservation> answers,
        byte[] transaction,
        int sent,
        double dnsMs,
        long sentAt,
        CancellationToken token,
        CancellationToken caller)
    {
        byte[] buffer = new byte[2048];
        EndPoint remote = new IPEndPoint(address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        bool timedOut = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                SocketReceiveFromResult received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, token).ConfigureAwait(false);
                if (received.RemoteEndPoint is not IPEndPoint from || from.Port != port || !from.Address.Equals(address))
                {
                    continue;
                }

                StunDecode decoded = StunBinding.Read(buffer.AsSpan(0, received.ReceivedBytes), transaction);
                if (!decoded.Success || decoded.MappedAddress is null)
                {
                    continue;
                }

                StunObservation stun = new(true, decoded.MappedAddress?.ToString(), decoded.MappedPort, decoded.Reflexive);
                return Result(host, port, protocol, observedAt, false, dnsError, answers, new UdpObservation("sent", sent, received.ReceivedBytes, UdpObservation.StunAnswered, stun), dnsMs, Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds);
            }
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            timedOut = true;
        }
        catch (SocketException)
        {
            // An ICMP error after a successful send is still not a firewall verdict.
        }

        StunObservation silent = new(false, null, null, false);
        return Result(host, port, protocol, observedAt, timedOut, dnsError, answers, new UdpObservation("sent", sent, 0, UdpObservation.StunSilent, silent), dnsMs);
    }

    private static EndpointProbeResult Result(
        string host,
        int port,
        string protocol,
        DateTimeOffset observedAt,
        bool timedOut,
        string? dnsError,
        IReadOnlyList<DnsObservation> answers,
        UdpObservation udp,
        double dnsMs,
        double? stunRttMs = null) =>
        new(host, port, protocol, observedAt, timedOut, dnsError, answers, [], null, udp, dnsMs, null, null, stunRttMs);

    private static async Task<(IReadOnlyList<IPAddress> Addresses, string? Error, bool TimedOut)> Resolve(
        string host,
        CancellationToken token,
        CancellationToken caller)
    {
        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            return ([literal], null, false);
        }

        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
            return (addresses.Take(MaxAnswers).ToArray(), addresses.Length == 0 ? "no_answers" : null, false);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            return ([], "timeout", true);
        }
        catch (SocketException ex)
        {
            return ([], ex.SocketErrorCode.ToString(), false);
        }
    }

    private static async Task<(TcpObservation Tcp, TlsObservation? Tls, bool TimedOut, double ConnectMs, double? TlsMs)> Connect(
        string host,
        int port,
        string protocol,
        IPAddress address,
        bool includeResolvedAddress,
        bool allowOnlineRevocation,
        CancellationToken token,
        CancellationToken caller)
    {
        string? shown = includeResolvedAddress ? address.ToString() : null;
        using TcpClient client = new();
        long connectStarted = Stopwatch.GetTimestamp();
        try
        {
            await client.ConnectAsync(address, port, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            return (new TcpObservation(shown, false, "timeout"), null, true, Stopwatch.GetElapsedTime(connectStarted).TotalMilliseconds, null);
        }
        catch (SocketException ex)
        {
            return (new TcpObservation(shown, false, ex.SocketErrorCode.ToString()), null, false, Stopwatch.GetElapsedTime(connectStarted).TotalMilliseconds, null);
        }

        double connectMs = Stopwatch.GetElapsedTime(connectStarted).TotalMilliseconds;
        if (protocol != "tls")
        {
            return (new TcpObservation(shown, true, null), null, false, connectMs, null);
        }

        (TlsObservation tls, bool timedOut, double handshakeMs) = await Handshake(client, host, allowOnlineRevocation, token, caller).ConfigureAwait(false);
        return (new TcpObservation(shown, true, null), tls, timedOut, connectMs, handshakeMs);
    }

    private static async Task<(TlsObservation Observation, bool TimedOut, double HandshakeMs)> Handshake(
        TcpClient client,
        string host,
        bool allowOnlineRevocation,
        CancellationToken token,
        CancellationToken caller)
    {
        string? subject = null;
        string? thumbprint = null;
        List<string> chain = [];
        SslClientAuthenticationOptions authentication = new()
        {
            TargetHost = host,
            CertificateRevocationCheckMode = allowOnlineRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, certificate, built, errors) =>
            {
                subject = certificate?.Subject;
                if (certificate is X509Certificate2 parsed)
                {
                    thumbprint = parsed.Thumbprint;
                }

                if (built is not null)
                {
                    chain.AddRange(built.ChainStatus.Select(item => item.Status.ToString()).Where(status => status != "NoError"));
                }

                return errors == SslPolicyErrors.None;
            }
        };
        await using SslStream stream = new(client.GetStream());
        long handshakeStarted = Stopwatch.GetTimestamp();
        try
        {
            await stream.AuthenticateAsClientAsync(authentication, token).ConfigureAwait(false);
            return (new TlsObservation(true, stream.SslProtocol.ToString(), subject, thumbprint, chain), false, Stopwatch.GetElapsedTime(handshakeStarted).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            chain.Add("timeout");
            return (new TlsObservation(false, null, subject, thumbprint, chain), true, Stopwatch.GetElapsedTime(handshakeStarted).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            return (new TlsObservation(false, null, subject, thumbprint, chain), false, Stopwatch.GetElapsedTime(handshakeStarted).TotalMilliseconds);
        }
    }

    private static string Family(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4";
}
