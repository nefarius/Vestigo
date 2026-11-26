using Vestigo.Performance;

namespace Vestigo.Diagnostics;

/// <summary>RFC 3550 jitter, PMTU search, and hop-status mapping. Pure; no sockets.</summary>
internal static class NetworkProbeMath
{
    public const int MinIpv4Mtu = 68;
    public const int MaxIpv4ProbeMtu = 1500;
    public const int Ipv4HeaderPlusIcmp = 28;

    public static double Rfc3550JitterMs(IReadOnlyList<double> rttMs)
    {
        if (rttMs.Count < 2)
        {
            return 0;
        }

        double jitter = 0;
        for (int index = 1; index < rttMs.Count; index++)
        {
            double delta = Math.Abs(rttMs[index] - rttMs[index - 1]);
            jitter += (delta - jitter) / 16.0;
        }

        return jitter;
    }

    public static double LossPercent(int sent, int received)
    {
        if (sent <= 0)
        {
            return 0;
        }

        int lost = Math.Max(0, sent - received);
        return 100.0 * lost / sent;
    }

    public static int PayloadBytesForMtu(int mtu) =>
        Math.Max(0, Math.Clamp(mtu, MinIpv4Mtu, MaxIpv4ProbeMtu) - Ipv4HeaderPlusIcmp);

    public static int MtuForPayload(int payloadBytes) =>
        Math.Clamp(payloadBytes + Ipv4HeaderPlusIcmp, MinIpv4Mtu, MaxIpv4ProbeMtu);

    public static (int Low, int High, int Mid, bool Done) NextPmtuSearch(int low, int high, bool tooBig)
    {
        if (tooBig)
        {
            high = Math.Max(low, high - 1);
        }
        else
        {
            low = high;
        }

        if (low >= high)
        {
            return (low, high, low, true);
        }

        int mid = low + ((high - low + 1) / 2);
        return (low, high, mid, false);
    }

    public static string StatusName(System.Net.NetworkInformation.IPStatus status) => status switch
    {
        System.Net.NetworkInformation.IPStatus.Success => "success",
        System.Net.NetworkInformation.IPStatus.TtlExpired => "ttlExpired",
        System.Net.NetworkInformation.IPStatus.TimedOut => "timedOut",
        System.Net.NetworkInformation.IPStatus.TimeExceeded => "timeExceeded",
        System.Net.NetworkInformation.IPStatus.DestinationUnreachable => "destinationUnreachable",
        System.Net.NetworkInformation.IPStatus.DestinationHostUnreachable => "destinationHostUnreachable",
        System.Net.NetworkInformation.IPStatus.DestinationNetworkUnreachable => "destinationNetworkUnreachable",
        System.Net.NetworkInformation.IPStatus.DestinationPortUnreachable => "destinationPortUnreachable",
        System.Net.NetworkInformation.IPStatus.PacketTooBig => "packetTooBig",
        System.Net.NetworkInformation.IPStatus.HardwareError => "hardwareError",
        System.Net.NetworkInformation.IPStatus.IcmpError => "icmpError",
        _ => status.ToString()
    };

    public static PercentileStats Rtt(IReadOnlyList<double> samples) => PercentileStats.From(samples);
}
