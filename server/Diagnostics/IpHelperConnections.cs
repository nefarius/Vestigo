using System.Net;

using Windows.Win32;
using Windows.Win32.NetworkManagement.IpHelper;

namespace Vestigo.Diagnostics;

internal readonly record struct TableRead(bool Success, string? ErrorCode, string? Error, bool Partial, bool Truncated, IReadOnlyList<ConnectionRecord> Rows)
{
    public static TableRead Ok(bool partial, bool truncated, IReadOnlyList<ConnectionRecord> rows) => new(true, null, null, partial, truncated, rows);

    public static TableRead Fail(string code, string error) => new(false, code, error, false, false, []);
}

/// <summary>Copies owner-PID rows for one process out of the IP Helper tables. Other processes are not retained.</summary>
internal static class IpHelperConnections
{
    private const uint IPv4 = 2;

    private const uint IPv6 = 23;

    private const uint InsufficientBuffer = 122;

    private const uint NoData = 232;

    private const int MaxTableBytes = 32 * 1024 * 1024;

    public static TableRead Read(int pid, int limit)
    {
        List<ConnectionRecord> rows = [];
        bool partial = false;
        bool truncated = false;
        bool any = false;
        (bool Tcp, bool Ipv6)[] families = [(true, false), (true, true), (false, false), (false, true)];
        foreach ((bool tcp, bool ipv6) in families)
        {
            if (rows.Count >= limit)
            {
                truncated = true;
                break;
            }

            (bool ok, string? code, List<ConnectionRecord> found, bool cut) = ReadFamily(pid, limit - rows.Count, tcp, ipv6);
            if (!ok)
            {
                if (code == "unsupported")
                {
                    partial = true;
                    continue;
                }

                return any ? TableRead.Ok(partial: true, truncated, rows) : TableRead.Fail(code!, "The connection table could not be read.");
            }

            any = true;
            rows.AddRange(found);
            truncated |= cut;
        }

        return TableRead.Ok(partial, truncated, rows);
    }

    private static (bool Ok, string? Code, List<ConnectionRecord> Rows, bool Truncated) ReadFamily(int pid, int room, bool tcp, bool ipv6)
    {
        uint family = ipv6 ? IPv6 : IPv4;
        byte[]? buffer = tcp ? TcpBuffer(family, out string? code) : UdpBuffer(family, out code);
        if (buffer is null)
        {
            return (false, code, [], false);
        }

        List<ConnectionRecord> rows = [];
        bool truncated = false;
        if (tcp && !ipv6)
        {
            ReadTcp4(buffer, pid, room, rows, ref truncated);
        }
        else if (tcp)
        {
            ReadTcp6(buffer, pid, room, rows, ref truncated);
        }
        else if (!ipv6)
        {
            ReadUdp4(buffer, pid, room, rows, ref truncated);
        }
        else
        {
            ReadUdp6(buffer, pid, room, rows, ref truncated);
        }

        return (true, null, rows, truncated);
    }

    private delegate uint TableReader(byte[] buffer, ref uint size);

    private static byte[]? TcpBuffer(uint family, out string? code)
    {
        uint size = 0;
        uint first = PInvoke.GetExtendedTcpTable([], ref size, default, family, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
        return Copy(first, size, (byte[] span, ref uint length) => PInvoke.GetExtendedTcpTable(span, ref length, default, family, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0), out code);
    }

    private static byte[]? UdpBuffer(uint family, out string? code)
    {
        uint size = 0;
        uint first = PInvoke.GetExtendedUdpTable([], ref size, default, family, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0);
        return Copy(first, size, (byte[] span, ref uint length) => PInvoke.GetExtendedUdpTable(span, ref length, default, family, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0), out code);
    }

    private static byte[]? Copy(uint first, uint size, TableReader read, out string? code)
    {
        code = null;
        if (first is 0 or NoData)
        {
            return [];
        }

        if (first != InsufficientBuffer || size == 0 || size > MaxTableBytes)
        {
            code = first == 5 ? "access_denied" : "unsupported";
            return null;
        }

        byte[] buffer = new byte[size];
        uint second = read(buffer, ref size);
        if (second == InsufficientBuffer && size <= MaxTableBytes)
        {
            buffer = new byte[size];
            second = read(buffer, ref size);
        }

        if (second is not (0 or NoData))
        {
            code = second == 5 ? "access_denied" : "unavailable";
            return null;
        }

        return buffer;
    }

    private static unsafe void ReadTcp4(byte[] buffer, int pid, int room, List<ConnectionRecord> rows, ref bool truncated)
    {
        if (buffer.Length < sizeof(MIB_TCPTABLE_OWNER_PID) || room <= 0)
        {
            return;
        }

        fixed (byte* pointer = buffer)
        {
            ref MIB_TCPTABLE_OWNER_PID table = ref *(MIB_TCPTABLE_OWNER_PID*)pointer;
            int count = RowCount(table.dwNumEntries, buffer.Length, MIB_TCPTABLE_OWNER_PID.SizeOf(1), sizeof(MIB_TCPROW_OWNER_PID));
            for (int index = 0; index < count; index++)
            {
                ref MIB_TCPROW_OWNER_PID row = ref table.table[index];
                if ((int)row.dwOwningPid != pid)
                {
                    continue;
                }

                if (rows.Count >= room)
                {
                    truncated = true;
                    return;
                }

                rows.Add(new ConnectionRecord(
                    "tcp",
                    "ipv4",
                    TcpState(row.dwState),
                    new IPAddress(row.dwLocalAddr).ToString(),
                    Port(row.dwLocalPort),
                    new IPAddress(row.dwRemoteAddr).ToString(),
                    Port(row.dwRemotePort),
                    pid));
            }
        }
    }

    private static unsafe void ReadTcp6(byte[] buffer, int pid, int room, List<ConnectionRecord> rows, ref bool truncated)
    {
        if (buffer.Length < sizeof(MIB_TCP6TABLE_OWNER_PID) || room <= 0)
        {
            return;
        }

        fixed (byte* pointer = buffer)
        {
            ref MIB_TCP6TABLE_OWNER_PID table = ref *(MIB_TCP6TABLE_OWNER_PID*)pointer;
            int count = RowCount(table.dwNumEntries, buffer.Length, MIB_TCP6TABLE_OWNER_PID.SizeOf(1), sizeof(MIB_TCP6ROW_OWNER_PID));
            for (int index = 0; index < count; index++)
            {
                ref MIB_TCP6ROW_OWNER_PID row = ref table.table[index];
                if ((int)row.dwOwningPid != pid)
                {
                    continue;
                }

                if (rows.Count >= room)
                {
                    truncated = true;
                    return;
                }

                rows.Add(new ConnectionRecord(
                    "tcp",
                    "ipv6",
                    TcpState(row.dwState),
                    Address(row.ucLocalAddr, row.dwLocalScopeId),
                    Port(row.dwLocalPort),
                    Address(row.ucRemoteAddr, row.dwRemoteScopeId),
                    Port(row.dwRemotePort),
                    pid));
            }
        }
    }

    private static unsafe void ReadUdp4(byte[] buffer, int pid, int room, List<ConnectionRecord> rows, ref bool truncated)
    {
        if (buffer.Length < sizeof(MIB_UDPTABLE_OWNER_PID) || room <= 0)
        {
            return;
        }

        fixed (byte* pointer = buffer)
        {
            ref MIB_UDPTABLE_OWNER_PID table = ref *(MIB_UDPTABLE_OWNER_PID*)pointer;
            int count = RowCount(table.dwNumEntries, buffer.Length, MIB_UDPTABLE_OWNER_PID.SizeOf(1), sizeof(MIB_UDPROW_OWNER_PID));
            for (int index = 0; index < count; index++)
            {
                ref MIB_UDPROW_OWNER_PID row = ref table.table[index];
                if ((int)row.dwOwningPid != pid)
                {
                    continue;
                }

                if (rows.Count >= room)
                {
                    truncated = true;
                    return;
                }

                rows.Add(new ConnectionRecord("udp", "ipv4", null, new IPAddress(row.dwLocalAddr).ToString(), Port(row.dwLocalPort), null, null, pid));
            }
        }
    }

    private static unsafe void ReadUdp6(byte[] buffer, int pid, int room, List<ConnectionRecord> rows, ref bool truncated)
    {
        if (buffer.Length < sizeof(MIB_UDP6TABLE_OWNER_PID) || room <= 0)
        {
            return;
        }

        fixed (byte* pointer = buffer)
        {
            ref MIB_UDP6TABLE_OWNER_PID table = ref *(MIB_UDP6TABLE_OWNER_PID*)pointer;
            int count = RowCount(table.dwNumEntries, buffer.Length, MIB_UDP6TABLE_OWNER_PID.SizeOf(1), sizeof(MIB_UDP6ROW_OWNER_PID));
            for (int index = 0; index < count; index++)
            {
                ref MIB_UDP6ROW_OWNER_PID row = ref table.table[index];
                if ((int)row.dwOwningPid != pid)
                {
                    continue;
                }

                if (rows.Count >= room)
                {
                    truncated = true;
                    return;
                }

                rows.Add(new ConnectionRecord("udp", "ipv6", null, Address(row.ucLocalAddr, row.dwLocalScopeId), Port(row.dwLocalPort), null, null, pid));
            }
        }
    }

    private static int RowCount(uint reported, int length, int headerWithOneRow, int rowSize)
    {
        int header = headerWithOneRow - rowSize;
        if (rowSize <= 0 || header < 0 || length < header)
        {
            return 0;
        }

        int room = (length - header) / rowSize;
        return (int)Math.Min(reported, (uint)room);
    }

    internal static int? Port(uint networkOrder)
    {
        int port = (ushort)IPAddress.NetworkToHostOrder((short)(networkOrder & 0xFFFF));
        return port == 0 ? null : port;
    }

    private static string Address(__byte_16 bytes, uint scope)
    {
        byte[] copy = bytes.AsSpan().ToArray();
        uint hostScope = (uint)IPAddress.NetworkToHostOrder(unchecked((int)scope));
        return hostScope == 0 ? new IPAddress(copy).ToString() : new IPAddress(copy, hostScope).ToString();
    }

    private static string? TcpState(MIB_TCP_STATE state) => state switch
    {
        MIB_TCP_STATE.MIB_TCP_STATE_CLOSED => "closed",
        MIB_TCP_STATE.MIB_TCP_STATE_LISTEN => "listen",
        MIB_TCP_STATE.MIB_TCP_STATE_SYN_SENT => "synSent",
        MIB_TCP_STATE.MIB_TCP_STATE_SYN_RCVD => "synReceived",
        MIB_TCP_STATE.MIB_TCP_STATE_ESTAB => "established",
        MIB_TCP_STATE.MIB_TCP_STATE_FIN_WAIT1 => "finWait1",
        MIB_TCP_STATE.MIB_TCP_STATE_FIN_WAIT2 => "finWait2",
        MIB_TCP_STATE.MIB_TCP_STATE_CLOSE_WAIT => "closeWait",
        MIB_TCP_STATE.MIB_TCP_STATE_CLOSING => "closing",
        MIB_TCP_STATE.MIB_TCP_STATE_LAST_ACK => "lastAck",
        MIB_TCP_STATE.MIB_TCP_STATE_TIME_WAIT => "timeWait",
        MIB_TCP_STATE.MIB_TCP_STATE_DELETE_TCB => "deleteTcb",
        _ => null
    };
}
