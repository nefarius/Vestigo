using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

using Vestigo.Performance;

namespace Vestigo.Diagnostics;

/// <summary>Multi-sample ICMP or TCP connect latency using BCL Ping/TcpClient.</summary>
public sealed class WindowsNetworkLatencyProbe : INetworkLatencyProbe
{
    public async Task<OperationResult<NetworkLatencyResult>> ProbeAsync(
        string host,
        int port,
        string protocol,
        int samples,
        TimeSpan timeout,
        bool includeResolvedAddress,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<NetworkLatencyResult>.Fail("windows_required", "Network latency probes require Windows.");
        }

        List<LatencySample> observed = [];
        List<double> rtts = [];
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        for (int index = 0; index < samples; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.IsCancellationRequested)
            {
                break;
            }

            LatencySample sample = protocol == "tcp"
                ? await TcpSample(host, port, budget.Token, cancellationToken).ConfigureAwait(false)
                : await IcmpSample(host, budget.Token, cancellationToken).ConfigureAwait(false);
            observed.Add(includeResolvedAddress ? sample : sample with { Error = sample.Error });
            if (sample.RttMs is double rtt)
            {
                rtts.Add(rtt);
            }
        }

        bool timedOut = budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
        return OperationResult<NetworkLatencyResult>.Ok(new NetworkLatencyResult(
            host,
            protocol,
            port,
            observed.Count,
            rtts.Count,
            NetworkProbeMath.LossPercent(observed.Count, rtts.Count),
            PercentileStats.From(rtts),
            NetworkProbeMath.Rfc3550JitterMs(rtts),
            observed,
            timedOut));
    }

    private static async Task<LatencySample> IcmpSample(string host, CancellationToken token, CancellationToken caller)
    {
        try
        {
            using Ping ping = new();
            PingReply reply = await ping.SendPingAsync(host, TimeSpan.FromSeconds(2), [], new PingOptions(64, false), token).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? new LatencySample("success", reply.RoundtripTime, null)
                : new LatencySample(NetworkProbeMath.StatusName(reply.Status), null, NetworkProbeMath.StatusName(reply.Status));
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            return new LatencySample("timedOut", null, "timedOut");
        }
        catch (PingException ex)
        {
            return new LatencySample("error", null, ex.GetType().Name);
        }
        catch (SocketException ex)
        {
            return new LatencySample("error", null, ex.SocketErrorCode.ToString());
        }
    }

    private static async Task<LatencySample> TcpSample(string host, int port, CancellationToken token, CancellationToken caller)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            using TcpClient client = new();
            await client.ConnectAsync(host, port, token).ConfigureAwait(false);
            return new LatencySample("success", Stopwatch.GetElapsedTime(started).TotalMilliseconds, null);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            return new LatencySample("timedOut", null, "timedOut");
        }
        catch (SocketException ex)
        {
            return new LatencySample("error", null, ex.SocketErrorCode.ToString());
        }
    }
}
