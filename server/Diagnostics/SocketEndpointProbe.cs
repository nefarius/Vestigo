using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

using Vestigo.Configuration;

namespace Vestigo.Diagnostics;

/// <summary>DNS, TCP, and TLS observations using the process that hosts Vestigo. Certificate validation is not bypassed.</summary>
public sealed class SocketEndpointProbe(VestigoOptions options) : IEndpointProbe
{
    public const int MaxAnswers = 8;

    public async Task<EndpointProbeResult> ProbeAsync(
        string host,
        int port,
        string protocol,
        TimeSpan timeout,
        bool includeResolvedAddress,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        CancellationToken token = budget.Token;
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        (IReadOnlyList<IPAddress> addresses, string? dnsError, bool timedOut) resolved = await Resolve(host, token, cancellationToken).ConfigureAwait(false);
        List<DnsObservation> answers = [];
        foreach (IPAddress address in resolved.addresses)
        {
            answers.Add(new DnsObservation(Family(address), includeResolvedAddress ? address.ToString() : null));
        }

        if (protocol == "dns" || answers.Count == 0)
        {
            return new EndpointProbeResult(host, port, protocol, observedAt, resolved.timedOut, resolved.dnsError, answers, [], null);
        }

        List<TcpObservation> attempts = [];
        TlsObservation? tls = null;
        bool timedOut = resolved.timedOut;
        foreach (IPAddress address in resolved.addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.IsCancellationRequested)
            {
                timedOut = true;
                break;
            }

            (TcpObservation tcp, TlsObservation? handshake, bool attemptTimedOut) = await Connect(
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
            if (handshake is not null)
            {
                tls = handshake;
                break;
            }

            if (tcp.Connected)
            {
                break;
            }
        }

        return new EndpointProbeResult(host, port, protocol, observedAt, timedOut, resolved.dnsError, answers, attempts, tls);
    }

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

    private static async Task<(TcpObservation Tcp, TlsObservation? Tls, bool TimedOut)> Connect(
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
        try
        {
            await client.ConnectAsync(address, port, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            return (new TcpObservation(shown, false, "timeout"), null, true);
        }
        catch (SocketException ex)
        {
            return (new TcpObservation(shown, false, ex.SocketErrorCode.ToString()), null, false);
        }

        if (protocol != "tls")
        {
            return (new TcpObservation(shown, true, null), null, false);
        }

        (TlsObservation tls, bool timedOut) = await Handshake(client, host, allowOnlineRevocation, token, caller).ConfigureAwait(false);
        return (new TcpObservation(shown, true, null), tls, timedOut);
    }

    private static async Task<(TlsObservation Observation, bool TimedOut)> Handshake(
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
        try
        {
            await stream.AuthenticateAsClientAsync(authentication, token).ConfigureAwait(false);
            return (new TlsObservation(true, stream.SslProtocol.ToString(), subject, thumbprint, chain), false);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            chain.Add("timeout");
            return (new TlsObservation(false, null, subject, thumbprint, chain), true);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            return (new TlsObservation(false, null, subject, thumbprint, chain), false);
        }
    }

    private static string Family(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4";
}
