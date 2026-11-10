using System.Net;

namespace Vestigo.SystemInfo;

/// <summary>Stable names for Windows Terminal Services session values.</summary>
public static class SessionMaps
{
    /// <summary>Maps a <c>WTS_CONNECTSTATE_CLASS</c> value. An unrecognized value is <c>unknown</c>.</summary>
    public static string State(int state) => state switch
    {
        0 => "active",
        1 => "connected",
        2 => "connectQuery",
        3 => "shadow",
        4 => "disconnected",
        5 => "idle",
        6 => "listen",
        7 => "reset",
        8 => "down",
        9 => "init",
        _ => "unknown"
    };

    /// <summary>Maps <c>WTSClientProtocolType</c>. 0 is console, 1 is ICA, 2 is RDP.</summary>
    public static string Protocol(int? protocol) => protocol switch
    {
        0 => "console",
        1 => "ica",
        2 => "rdp",
        _ => "unknown"
    };

    /// <summary>Maps <c>TokenElevationType</c>. Null stays null.</summary>
    public static string? Elevation(int? elevation) => elevation switch
    {
        null => null,
        1 => "default",
        2 => "full",
        3 => "limited",
        _ => "unknown"
    };

    /// <summary>Joins a domain and user. An empty user is null.</summary>
    public static string? User(string? domain, string? user)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(domain) ? user.Trim() : domain.Trim() + "\\" + user.Trim();
    }

    /// <summary>
    ///     Formats a <c>WTS_CLIENT_ADDRESS</c>. IPv4 and IPv6 start two bytes into the address buffer.
    ///     Any other family is omitted.
    /// </summary>
    public static string? ClientAddress(uint addressFamily, ReadOnlySpan<byte> address)
    {
        if (addressFamily == 2 && address.Length >= 6)
        {
            return new IPAddress(address.Slice(2, 4)).ToString();
        }

        if (addressFamily == 23 && address.Length >= 18)
        {
            return new IPAddress(address.Slice(2, 16)).ToString();
        }

        return null;
    }

    /// <summary>Formats a logon LUID as <c>0xHIGH:LOW</c>.</summary>
    public static string LogonId(int highPart, uint lowPart) => $"0x{(uint)highPart:X}:{lowPart:X}";
}
