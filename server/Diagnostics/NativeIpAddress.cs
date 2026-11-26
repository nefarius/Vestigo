using System.Net;
using System.Net.Sockets;

using Windows.Win32.Networking.WinSock;

namespace Vestigo.Diagnostics;

/// <summary>Formats CsWin32 <c>SOCKADDR_INET</c> values without credentials or extra P/Invoke.</summary>
internal static class NativeIpAddress
{
    public static unsafe string Format(in SOCKADDR_INET inet)
    {
        ADDRESS_FAMILY family = inet.si_family;
        if (family == ADDRESS_FAMILY.AF_INET)
        {
            return new IPAddress(inet.Ipv4.sin_addr.S_un.S_addr).ToString();
        }

        if (family == ADDRESS_FAMILY.AF_INET6)
        {
            byte[] bytes = inet.Ipv6.sin6_addr.u.Byte.AsSpan().ToArray();
            uint scope = inet.Ipv6.sin6_scope_id;
            return scope == 0 ? new IPAddress(bytes).ToString() : new IPAddress(bytes, scope).ToString();
        }

        return family.ToString();
    }

    public static string FamilyName(ADDRESS_FAMILY family) => family switch
    {
        ADDRESS_FAMILY.AF_INET => "ipv4",
        ADDRESS_FAMILY.AF_INET6 => "ipv6",
        _ => family.ToString()
    };

    public static string FamilyName(AddressFamily family) =>
        family == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4";
}
