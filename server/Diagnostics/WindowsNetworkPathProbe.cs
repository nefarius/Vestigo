using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

using Vestigo.Performance;

namespace Vestigo.Diagnostics;

/// <summary>ICMP TTL traceroute and optional Path MTU binary search using BCL Ping.</summary>
public sealed class WindowsNetworkPathProbe : INetworkPathProbe
{
    public async Task<OperationResult<NetworkPathResult>> ProbeAsync(
        string host,
        int maxHops,
        int samplesPerHop,
        TimeSpan hopTimeout,
        bool includeHopAddresses,
        bool discoverPmtu,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<NetworkPathResult>.Fail("windows_required", "Path probes require Windows.");
        }

        IPAddress? destination = null;
        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            destination = literal;
        }
        else
        {
            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                destination = addresses.FirstOrDefault();
            }
            catch (SocketException)
            {
                return OperationResult<NetworkPathResult>.Fail("dns_failed", "The destination host name could not be resolved.");
            }
        }

        if (destination is null)
        {
            return OperationResult<NetworkPathResult>.Fail("dns_failed", "The destination host name could not be resolved.");
        }

        List<PathHop> hops = [];
        bool reached = false;
        for (int ttl = 1; ttl <= maxHops; ttl++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (PathHop hop, bool destinationHit) = await ProbeHop(destination, ttl, samplesPerHop, hopTimeout, includeHopAddresses, cancellationToken)
                .ConfigureAwait(false);
            hops.Add(hop);
            if (destinationHit)
            {
                reached = true;
                break;
            }
        }

        PathMtu? pmtu = null;
        if (discoverPmtu)
        {
            pmtu = await DiscoverPmtu(destination, hopTimeout, cancellationToken).ConfigureAwait(false);
        }

        return OperationResult<NetworkPathResult>.Ok(new NetworkPathResult(host, hops, pmtu, !reached));
    }

    private static async Task<(PathHop Hop, bool Destination)> ProbeHop(
        IPAddress destination,
        int ttl,
        int samples,
        TimeSpan timeout,
        bool includeHopAddresses,
        CancellationToken cancellationToken)
    {
        List<double> rtts = [];
        int received = 0;
        string? hopAddress = null;
        bool destinationHit = false;
        using Ping ping = new();
        for (int index = 0; index < samples; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PingReply reply = await ping.SendPingAsync(
                    destination,
                    timeout,
                    [],
                    new PingOptions(ttl, false),
                    cancellationToken).ConfigureAwait(false);
                if (reply.Status is IPStatus.Success or IPStatus.TtlExpired or IPStatus.TimeExceeded)
                {
                    received++;
                    rtts.Add(reply.RoundtripTime);
                    hopAddress ??= reply.Address?.ToString();
                    destinationHit |= reply.Status == IPStatus.Success;
                }
            }
            catch (PingException)
            {
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        return (new PathHop(
            ttl,
            includeHopAddresses ? hopAddress : null,
            PercentileStats.From(rtts),
            samples,
            received,
            NetworkProbeMath.LossPercent(samples, received),
            destinationHit), destinationHit);
    }

    private static async Task<PathMtu> DiscoverPmtu(IPAddress destination, TimeSpan timeout, CancellationToken cancellationToken)
    {
        int low = NetworkProbeMath.MinIpv4Mtu;
        int high = NetworkProbeMath.MaxIpv4ProbeMtu;
        bool blackhole = false;
        string? last = null;
        using Ping ping = new();
        while (low < high)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int mid = low + ((high - low + 1) / 2);
            int payload = NetworkProbeMath.PayloadBytesForMtu(mid);
            try
            {
                PingReply reply = await ping.SendPingAsync(
                    destination,
                    timeout,
                    new byte[payload],
                    new PingOptions(64, true),
                    cancellationToken).ConfigureAwait(false);
                last = NetworkProbeMath.StatusName(reply.Status);
                bool tooBig = reply.Status == IPStatus.PacketTooBig;
                if (reply.Status == IPStatus.TimedOut)
                {
                    blackhole = true;
                    tooBig = true;
                }

                (low, high, _, bool done) = NetworkProbeMath.NextPmtuSearch(low, high, mid, tooBig || reply.Status is not IPStatus.Success);
                if (done)
                {
                    break;
                }
            }
            catch (PingException ex)
            {
                last = ex.GetType().Name;
                break;
            }
        }

        return new PathMtu(low, blackhole, last);
    }
}
