using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.NetworkManagement.IpHelper;

namespace Vestigo.Diagnostics;

/// <summary>Queries per-connection TCP EStats. Enables collection only for the observation window and restores prior state.</summary>
public sealed class WindowsTcpEstatsSource : ITcpEstatsSource
{
    public async Task<OperationResult<TcpConnectionStatsList>> InspectAsync(
        string? localAddress,
        int? localPort,
        string? remoteAddress,
        int? remotePort,
        int? pid,
        int limit,
        TimeSpan observe,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<TcpConnectionStatsList>.Fail("windows_required", "TCP EStats require Windows.");
        }

        TableRead table = IpHelperConnections.ReadAll(16_384);
        if (!table.Success)
        {
            return OperationResult<TcpConnectionStatsList>.Fail(table.ErrorCode!, table.Error!);
        }

        IReadOnlyDictionary<int, string> names = ProcessNameLookup.Snapshot();
        List<ConnectionRecord> selected = [];
        foreach (ConnectionRecord row in table.Rows)
        {
            if (row.Protocol != "tcp" || row.State != "established")
            {
                continue;
            }

            if (pid is int requiredPid && row.OwningPid != requiredPid)
            {
                continue;
            }

            if (localPort is int local && row.LocalPort != local)
            {
                continue;
            }

            if (remotePort is int remote && row.RemotePort != remote)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(localAddress) &&
                !string.Equals(row.LocalAddress, localAddress, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(remoteAddress) &&
                !string.Equals(row.RemoteAddress, remoteAddress, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            selected.Add(row);
            if (selected.Count >= limit)
            {
                break;
            }
        }

        if (selected.Count == 0)
        {
            return OperationResult<TcpConnectionStatsList>.Fail("not_found", "No established TCP connection matched the selection.");
        }

        List<(ConnectionRecord Row, bool Enable)> restore = [];
        try
        {
            foreach (ConnectionRecord row in selected)
            {
                bool already = CollectionEnabled(row);
                if (!already && observe > TimeSpan.Zero)
                {
                    SetCollection(row, true);
                    restore.Add((row, false));
                }
            }

            if (observe > TimeSpan.Zero)
            {
                await Task.Delay(observe, cancellationToken).ConfigureAwait(false);
            }

            List<TcpConnectionStats> results = [];
            bool partial = table.Partial;
            foreach (ConnectionRecord row in selected)
            {
                names.TryGetValue(row.OwningPid, out string? name);
                results.Add(Read(row, name, CollectionEnabled(row)));
            }

            return OperationResult<TcpConnectionStatsList>.Ok(new TcpConnectionStatsList(
                (int)observe.TotalMilliseconds,
                results,
                partial));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OperationResult<TcpConnectionStatsList>.Fail("tcp_estats_failed", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            foreach ((ConnectionRecord row, bool enable) in restore)
            {
                try
                {
                    SetCollection(row, enable);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }
            }
        }
    }

    private static TcpConnectionStats Read(ConnectionRecord row, string? name, bool enabled)
    {
        TcpEstatsSnapshot snapshot = Query(row);
        return new TcpConnectionStats(
            row.Family,
            row.LocalAddress ?? "",
            row.LocalPort ?? 0,
            row.RemoteAddress ?? "",
            row.RemotePort ?? 0,
            row.OwningPid,
            name,
            snapshot.SmoothedRttMs,
            snapshot.RttVarianceMs,
            snapshot.MinRttMs,
            snapshot.MaxRttMs,
            snapshot.CongestionWindow,
            snapshot.SlowStartThreshold,
            snapshot.Retransmits,
            snapshot.DuplicateAcks,
            snapshot.Sacks,
            snapshot.ZeroWindow,
            snapshot.BytesAcked,
            enabled,
            snapshot.Error);
    }

    private static bool CollectionEnabled(ConnectionRecord row)
    {
        TCP_ESTATS_DATA_RW_v0 rw = default;
        uint status = Get(row, TCP_ESTATS_TYPE.TcpConnectionEstatsData, MemoryMarshal.AsBytes(new Span<TCP_ESTATS_DATA_RW_v0>(ref rw)), [], []);
        return status == 0 && rw.EnableCollection;
    }

    private static void SetCollection(ConnectionRecord row, bool enable)
    {
        TCP_ESTATS_DATA_RW_v0 rw = new() { EnableCollection = enable };
        uint data = Set(row, TCP_ESTATS_TYPE.TcpConnectionEstatsData, MemoryMarshal.AsBytes(new Span<TCP_ESTATS_DATA_RW_v0>(ref rw)));
        TCP_ESTATS_DATA_RW_v0 path = new() { EnableCollection = enable };
        _ = Set(row, TCP_ESTATS_TYPE.TcpConnectionEstatsPath, MemoryMarshal.AsBytes(new Span<TCP_ESTATS_DATA_RW_v0>(ref path)));
        TCP_ESTATS_DATA_RW_v0 cong = new() { EnableCollection = enable };
        _ = Set(row, TCP_ESTATS_TYPE.TcpConnectionEstatsSndCong, MemoryMarshal.AsBytes(new Span<TCP_ESTATS_DATA_RW_v0>(ref cong)));
        TCP_ESTATS_DATA_RW_v0 rec = new() { EnableCollection = enable };
        _ = Set(row, TCP_ESTATS_TYPE.TcpConnectionEstatsRec, MemoryMarshal.AsBytes(new Span<TCP_ESTATS_DATA_RW_v0>(ref rec)));
        _ = data;
    }

    private static TcpEstatsSnapshot Query(ConnectionRecord row)
    {
        TCP_ESTATS_DATA_ROD_v0 data = default;
        TCP_ESTATS_PATH_ROD_v0 path = default;
        TCP_ESTATS_SND_CONG_ROD_v0 cong = default;
        TCP_ESTATS_REC_ROD_v0 rec = default;
        uint dataStatus = Get(row, TCP_ESTATS_TYPE.TcpConnectionEstatsData, [], [], MemoryMarshal.AsBytes(new Span<TCP_ESTATS_DATA_ROD_v0>(ref data)));
        uint pathStatus = Get(row, TCP_ESTATS_TYPE.TcpConnectionEstatsPath, [], [], MemoryMarshal.AsBytes(new Span<TCP_ESTATS_PATH_ROD_v0>(ref path)));
        uint congStatus = Get(row, TCP_ESTATS_TYPE.TcpConnectionEstatsSndCong, [], [], MemoryMarshal.AsBytes(new Span<TCP_ESTATS_SND_CONG_ROD_v0>(ref cong)));
        uint recStatus = Get(row, TCP_ESTATS_TYPE.TcpConnectionEstatsRec, [], [], MemoryMarshal.AsBytes(new Span<TCP_ESTATS_REC_ROD_v0>(ref rec)));
        string? error = null;
        if (dataStatus != 0 || pathStatus != 0 || congStatus != 0 || recStatus != 0)
        {
            error = $"EStats query status data={dataStatus} path={pathStatus} sndCong={congStatus} rec={recStatus}.";
        }

        return new TcpEstatsSnapshot(
            Milliseconds(path.SmoothedRtt),
            Milliseconds(path.RttVar),
            Milliseconds(path.MinRtt),
            Milliseconds(path.MaxRtt),
            cong.CurCwnd,
            cong.CurSsthresh,
            path.PktsRetrans,
            path.DupAcksIn,
            path.SacksRcvd,
            rec.CurRwinSent == 0 ? 1 : 0,
            (long)data.ThruBytesAcked,
            error);
    }

    private static double? Milliseconds(uint hundredNsOrMs)
    {
        if (hundredNsOrMs == 0)
        {
            return null;
        }

        // Path RTT fields are documented in milliseconds on modern Windows.
        return hundredNsOrMs;
    }

    private static uint Get(ConnectionRecord row, TCP_ESTATS_TYPE type, Span<byte> rw, Span<byte> ros, Span<byte> rod) =>
        row.Family == "ipv6" ? Get6(row, type, rw, ros, rod) : Get4(row, type, rw, ros, rod);

    private static uint Set(ConnectionRecord row, TCP_ESTATS_TYPE type, Span<byte> rw) =>
        row.Family == "ipv6" ? Set6(row, type, rw) : Set4(row, type, rw);

    private static uint Get4(ConnectionRecord row, TCP_ESTATS_TYPE type, Span<byte> rw, Span<byte> ros, Span<byte> rod)
    {
        MIB_TCPROW_LH tcp = ToRow4(row);
        return PInvoke.GetPerTcpConnectionEStats(in tcp, type, rw, 0, ros, 0, rod, 0);
    }

    private static uint Set4(ConnectionRecord row, TCP_ESTATS_TYPE type, Span<byte> rw)
    {
        MIB_TCPROW_LH tcp = ToRow4(row);
        return PInvoke.SetPerTcpConnectionEStats(in tcp, type, rw, 0, 0);
    }

    private static uint Get6(ConnectionRecord row, TCP_ESTATS_TYPE type, Span<byte> rw, Span<byte> ros, Span<byte> rod)
    {
        MIB_TCP6ROW tcp = ToRow6(row);
        return PInvoke.GetPerTcp6ConnectionEStats(in tcp, type, rw, 0, ros, 0, rod, 0);
    }

    private static uint Set6(ConnectionRecord row, TCP_ESTATS_TYPE type, Span<byte> rw)
    {
        MIB_TCP6ROW tcp = ToRow6(row);
        return PInvoke.SetPerTcp6ConnectionEStats(in tcp, type, rw, 0, 0);
    }

    private static MIB_TCPROW_LH ToRow4(ConnectionRecord row)
    {
        IPAddress local = IPAddress.Parse(row.LocalAddress ?? "0.0.0.0");
        IPAddress remote = IPAddress.Parse(row.RemoteAddress ?? "0.0.0.0");
        return new MIB_TCPROW_LH
        {
            dwLocalAddr = BitConverter.ToUInt32(local.GetAddressBytes()),
            dwLocalPort = NetworkPort(row.LocalPort),
            dwRemoteAddr = BitConverter.ToUInt32(remote.GetAddressBytes()),
            dwRemotePort = NetworkPort(row.RemotePort)
        };
    }

    private static MIB_TCP6ROW ToRow6(ConnectionRecord row)
    {
        MIB_TCP6ROW tcp = new()
        {
            dwLocalPort = NetworkPort(row.LocalPort),
            dwRemotePort = NetworkPort(row.RemotePort),
            State = MIB_TCP_STATE.MIB_TCP_STATE_ESTAB
        };
        Copy6(row.LocalAddress, tcp.LocalAddr.u.Byte.AsSpan(), out uint localScope);
        Copy6(row.RemoteAddress, tcp.RemoteAddr.u.Byte.AsSpan(), out uint remoteScope);
        tcp.dwLocalScopeId = localScope;
        tcp.dwRemoteScopeId = remoteScope;
        return tcp;
    }

    private static void Copy6(string? text, Span<byte> destination, out uint scope)
    {
        scope = 0;
        if (!IPAddress.TryParse(text, out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            destination.Clear();
            return;
        }

        address.GetAddressBytes().CopyTo(destination);
        scope = (uint)address.ScopeId;
    }

    private static uint NetworkPort(int? port) =>
        port is int value ? (uint)(ushort)IPAddress.HostToNetworkOrder((short)value) : 0;

    private readonly record struct TcpEstatsSnapshot(
        double? SmoothedRttMs,
        double? RttVarianceMs,
        double? MinRttMs,
        double? MaxRttMs,
        long? CongestionWindow,
        long? SlowStartThreshold,
        long? Retransmits,
        long? DuplicateAcks,
        long? Sacks,
        long? ZeroWindow,
        long? BytesAcked,
        string? Error);
}
