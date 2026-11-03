using System.Net;
using System.Net.Sockets;

namespace Vestigo.Diagnostics;

/// <summary>Matches a requested host and port against <c>Vestigo:Diagnostics:AllowedProbeDestinations</c>.</summary>
internal static class ProbeDestinationPolicy
{
    public static bool Allows(string host, int port, IReadOnlyList<string> entries)
    {
        foreach (string entry in entries)
        {
            if (TryParse(entry, out string entryHost, out int? entryPort) &&
                HostEquals(host, entryHost) &&
                (entryPort is null || entryPort == port))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsValidEntry(string? entry) =>
        entry is not null &&
        entry.Length is > 0 and <= 262 &&
        entry.IndexOfAny(['*', '/', '\\', ' ', '\r', '\n', '?', '#', '@']) < 0 &&
        TryParse(entry.Trim(), out string host, out _) &&
        IsValidHost(host);

    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string trimmed = host.Trim().TrimEnd('.');
        if (trimmed.Length is 0 or > 253 || trimmed.IndexOfAny(['*', '/', '\\', ' ', '\r', '\n', '?', '#', '@']) >= 0)
        {
            return false;
        }

        if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length > 2)
        {
            trimmed = trimmed[1..^1];
        }

        if (IPAddress.TryParse(trimmed, out _))
        {
            return true;
        }

        if (trimmed.StartsWith('.') || trimmed.EndsWith('.') || trimmed.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (string label in trimmed.Split('.'))
        {
            if (label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-'))
            {
                return false;
            }

            foreach (char character in label)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character != '-')
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static string NormalizeHost(string host)
    {
        string trimmed = host.Trim().TrimEnd('.');
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length > 2)
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed;
    }

    private static bool TryParse(string entry, out string host, out int? port)
    {
        host = "";
        port = null;
        string text = entry.Trim();
        if (text.StartsWith('['))
        {
            int end = text.IndexOf(']');
            if (end <= 1)
            {
                return false;
            }

            host = text[1..end];
            if (!IPAddress.TryParse(host, out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }

            if (end == text.Length - 1)
            {
                return true;
            }

            return TryPort(text, end + 1, out port);
        }

        int colon = text.IndexOf(':');
        if (colon < 0)
        {
            host = text;
            return IsValidHost(host);
        }

        if (colon != text.LastIndexOf(':'))
        {
            host = text;
            return IPAddress.TryParse(text, out _);
        }

        host = text[..colon];
        return IsValidHost(host) && TryPort(text, colon, out port);
    }

    private static bool TryPort(string text, int separator, out int? port)
    {
        port = null;
        if (separator >= text.Length || text[separator] != ':')
        {
            return false;
        }

        if (!int.TryParse(text[(separator + 1)..], out int parsed) || parsed is < 1 or > 65535)
        {
            return false;
        }

        port = parsed;
        return true;
    }

    private static bool HostEquals(string requested, string allowed)
    {
        string left = NormalizeHost(requested);
        string right = NormalizeHost(allowed);
        if (IPAddress.TryParse(left, out IPAddress? leftAddress) && IPAddress.TryParse(right, out IPAddress? rightAddress))
        {
            return leftAddress.Equals(rightAddress);
        }

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
