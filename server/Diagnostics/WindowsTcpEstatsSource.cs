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

        List<(ConnectionRecord Row, TCP_ESTATS_TYPE Type)> restore = [];
        Dictionary<ConnectionRecord, List<string>> enableErrors = [];
        try
        {
            foreach (ConnectionRecord row in selected)
            {
                if (observe <= TimeSpan.Zero)
                {
                    continue;
                }

                foreach (TCP_ESTATS_TYPE type in Categories)
                {
                    if (!TryReadEnabled(row, type, out bool enabled))
                    {
                        Note(enableErrors, row, $"{CategoryName(type)} enable query failed");
                        continue;
                    }

                    if (enabled)
                    {
                        continue;
                    }

                    if (!TrySetEnabled(row, type, true))
                    {
                        Note(enableErrors, row, $"{CategoryName(type)} enable failed");
                        continue;
                    }

                    restore.Add((row, type));
                }
            }

            if (observe > TimeSpan.Zero)
            {
                await Task.Delay(observe, cancellationToken).ConfigureAwait(false);
            }

            List<TcpConnectionStats> results = [];
            bool partial = table.Partial || enableErrors.Count > 0;
            foreach (ConnectionRecord row in selected)
            {
                names.TryGetValue(row.OwningPid, out string? name);
                enableErrors.TryGetValue(row, out List<string>? notes);
                results.Add(Read(row, name, notes));
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
            foreach ((ConnectionRecord row, TCP_ESTATS_TYPE type) in restore)
            {
                _ = TrySetEnabled(row, type, false);
            }
        }
    }

    private static readonly TCP_ESTATS_TYPE[] Categories =
    [
        TCP_ESTATS_TYPE.TcpConnectionEstatsData,
        TCP_ESTATS_TYPE.TcpConnectionEstatsPath,
        TCP_ESTATS_TYPE.TcpConnectionEstatsSndCong,
        TCP_ESTATS_TYPE.TcpConnectionEstatsRec
    ];

    private static string CategoryName(TCP_ESTATS_TYPE type) => type switch
    {
        TCP_ESTATS_TYPE.TcpConnectionEstatsData => "data",
        TCP_ESTATS_TYPE.TcpConnectionEstatsPath => "path",
        TCP_ESTATS_TYPE.TcpConnectionEstatsSndCong => "sndCong",
        TCP_ESTATS_TYPE.TcpConnectionEstatsRec => "rec",
        _ => type.ToString()
    };

    private static void Note(Dictionary<ConnectionRecord, List<string>> map, ConnectionRecord row, string message)
    {
        if (!map.TryGetValue(row, out List<string>? notes))
        {
            notes = [];
            map[row] = notes;
        }

        notes.Add(message);
    }

    private static TcpConnectionStats Read(ConnectionRecord row, string? name, IReadOnlyList<string>? enableErrors)
    {
        TcpEstatsSnapshot snapshot = Query(row, enableErrors);
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
            snapshot.CollectionEnabled,
            snapshot.Error);
    }

    private static bool TryReadEnabled(ConnectionRecord row, TCP_ESTATS_TYPE type, out bool enabled)
    {
        TCP_ESTATS_DATA_RW_v0 rw = default;
        uint status = Get(row, type, MemoryMarshal.AsBytes(new Span<TCP_ESTATS_DATA_RW_v0>(ref rw)), [], []);
        enabled = status == 0 && rw.EnableCollection;
        return status == 0;
    }

    private static bool TrySetEnabled(ConnectionRecord row, TCP_ESTATS_TYPE type, bool enable)
    {
        try
        {
            TCP_ESTATS_DATA_RW_v0 rw = new() { EnableCollection = enable };
            return Set(row, type, MemoryMarshal.AsBytes(new Span<TCP_ESTATS_DATA_RW_v0>(ref rw))) == 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static TcpEstatsSnapshot Query(ConnectionRecord row, IReadOnlyList<string>? enableErrors)
    {
        (bool dataOn, uint dataStatus, TCP_ESTATS_DATA_ROD_v0 data) = ReadData(row);
        (bool pathOn, uint pathStatus, TCP_ESTATS_PATH_ROD_v0 path) = ReadPath(row);
        (bool congOn, uint congStatus, TCP_ESTATS_SND_CONG_ROD_v0 cong) = ReadCong(row);
        (bool recOn, uint recStatus, TCP_ESTATS_REC_ROD_v0 rec) = ReadRec(row);
        List<string> errors = enableErrors is { Count: > 0 } ? [.. enableErrors] : [];
        if (dataStatus != 0) errors.Add($"data={dataStatus}");
        if (pathStatus != 0) errors.Add($"path={pathStatus}");
        if (congStatus != 0) errors.Add($"sndCong={congStatus}");
        if (recStatus != 0) errors.Add($"rec={recStatus}");
        if (!dataOn && dataStatus == 0) errors.Add("data collection disabled");
        if (!pathOn && pathStatus == 0) errors.Add("path collection disabled");
        if (!congOn && congStatus == 0) errors.Add("sndCong collection disabled");
        if (!recOn && recStatus == 0) errors.Add("rec collection disabled");
        bool enabled = dataOn || pathOn || congOn || recOn;
        return new TcpEstatsSnapshot(
            pathOn ? Milliseconds(path.SmoothedRtt) : null,
            pathOn ? Milliseconds(path.RttVar) : null,
            pathOn ? Milliseconds(path.MinRtt) : null,
            pathOn ? Milliseconds(path.MaxRtt) : null,
            congOn ? cong.CurCwnd : null,
            congOn ? cong.CurSsthresh : null,
            pathOn ? path.PktsRetrans : null,
            pathOn ? path.DupAcksIn : null,
            pathOn ? path.SacksRcvd : null,
            recOn ? rec.CurRwinSent == 0 ? 1 : 0 : null,
            dataOn ? (long)data.ThruBytesAcked : null,
            enabled,
            errors.Count == 0 ? null : "EStats " + string.Join("; ", errors) + ".");
    }

    private static (bool Enabled, uint Status, TCP_ESTATS_DATA_ROD_v0 Rod) ReadData(ConnectionRecord row)
    {
        TCP_ESTATS_DATA_RW_v0 rw = default;
        TCP_ESTATS_DATA_ROD_v0 rod = default;
        uint status = Get(
            row,
            TCP_ESTATS_TYPE.TcpConnectionEstatsData,
            MemoryMarshal.AsBytes(new Span<TCP_ESTATS_DATA_RW_v0>(ref rw)),
            [],
            MemoryMarshal.AsBytes(new Span<TCP_ESTATS_DATA_ROD_v0>(ref rod)));
        return (status == 0 && rw.EnableCollection, status, rod);
    }

    private static (bool Enabled, uint Status, TCP_ESTATS_PATH_ROD_v0 Rod) ReadPath(ConnectionRecord row)
    {
        TCP_ESTATS_DATA_RW_v0 rw = default;
        TCP_ESTATS_PATH_ROD_v0 rod = default;
        uint status = Get(
            row,
            TCP_ESTATS_TYPE.TcpConnectionEstatsPath,
            MemoryMarshal.AsBytes(new Span<TCP_ESTATS_DATA_RW_v0>(ref rw)),
            [],
            MemoryMarshal.AsBytes(new Span<TCP_ESTATS_PATH_ROD_v0>(ref rod)));
        return (status == 0 && rw.EnableCollection, status, rod);
    }

    private static (bool Enabled, uint Status, TCP_ESTATS_SND_CONG_ROD_v0 Rod) ReadCong(ConnectionRecord row)
    {
        TCP_ESTATS_DATA_RW_v0 rw = default;
        TCP_ESTATS_SND_CONG_ROD_v0 rod = default;
        uint status = Get(
            row,
            TCP_ESTATS_TYPE.TcpConnectionEstatsSndCong,
            MemoryMarshal.AsBytes(new Span<TCP_ESTATS_DATA_RW_v0>(ref rw)),
            [],
            MemoryMarshal.AsBytes(new Span<TCP_ESTATS_SND_CONG_ROD_v0>(ref rod)));
        return (status == 0 && rw.EnableCollection, status, rod);
    }

    private static (bool Enabled, uint Status, TCP_ESTATS_REC_ROD_v0 Rod) ReadRec(ConnectionRecord row)
    {
        TCP_ESTATS_DATA_RW_v0 rw = default;
        TCP_ESTATS_REC_ROD_v0 rod = default;
        uint status = Get(
            row,
            TCP_ESTATS_TYPE.TcpConnectionEstatsRec,
            MemoryMarshal.AsBytes(new Span<TCP_ESTATS_DATA_RW_v0>(ref rw)),
            [],
            MemoryMarshal.AsBytes(new Span<TCP_ESTATS_REC_ROD_v0>(ref rod)));
        return (status == 0 && rw.EnableCollection, status, rod);
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
        bool CollectionEnabled,
        string? Error);
}
