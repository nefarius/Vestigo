using System.Net;

namespace Vestigo.Diagnostics;

/// <summary>Pure connection filtering, TCP state histograms, and ephemeral-port utilization.</summary>
internal static class NetworkConnectionProjection
{
    public const int DefaultEphemeralStart = 49152;
    public const int DefaultEphemeralEnd = 65535;

    public static bool Matches(
        ConnectionRecord row,
        string? protocol,
        string? family,
        string? state,
        int? localPort,
        int? remotePort,
        int? pid)
    {
        if (!IsBlankOrEqual(protocol, row.Protocol) || !IsBlankOrEqual(family, row.Family))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(state) &&
            !string.Equals(state.Trim(), row.State, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (localPort is int local && row.LocalPort != local)
        {
            return false;
        }

        if (remotePort is int remote && row.RemotePort != remote)
        {
            return false;
        }

        return pid is null || row.OwningPid == pid;
    }

    public static bool TryParseAddress(string? text, out IPAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return IPAddress.TryParse(text, out address);
    }

    public static bool AddressMatches(string? stored, IPAddress? selector)
    {
        if (selector is null)
        {
            return true;
        }

        return IPAddress.TryParse(stored, out IPAddress? parsed) && parsed.Equals(selector);
    }

    public static IReadOnlyList<TcpStateCount> Histogram(IEnumerable<ConnectionRecord> rows)
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (ConnectionRecord row in rows)
        {
            if (row.Protocol != "tcp" || string.IsNullOrWhiteSpace(row.State))
            {
                continue;
            }

            counts.TryGetValue(row.State, out int count);
            counts[row.State] = count + 1;
        }

        return counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new TcpStateCount(pair.Key, pair.Value))
            .ToList();
    }

    public static int UdpCount(IEnumerable<ConnectionRecord> rows) =>
        rows.Count(row => row.Protocol == "udp");

    public static Performance.EphemeralPortUse Ephemeral(IEnumerable<ConnectionRecord> rows, int start = DefaultEphemeralStart, int end = DefaultEphemeralEnd)
    {
        int capacity = Math.Max(1, end - start + 1);
        HashSet<int> used = [];
        foreach (ConnectionRecord row in rows)
        {
            if (row.Protocol != "tcp" || row.LocalPort is not int port || port < start || port > end)
            {
                continue;
            }

            used.Add(port);
        }

        return new Performance.EphemeralPortUse(start, end, used.Count, capacity, used.Count / (double)capacity);
    }

    private static bool IsBlankOrEqual(string? expected, string actual) =>
        string.IsNullOrWhiteSpace(expected) ||
        string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase);
}
