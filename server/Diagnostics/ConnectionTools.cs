using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

using Vestigo.Configuration;

namespace Vestigo.Diagnostics;

/// <summary>Lists TCP endpoints and UDP bindings for one process or the whole machine.</summary>
[McpServerToolType]
public sealed class ConnectionTools(IProcessConnectionSource source, VestigoOptions options, ILogger<ConnectionTools> logger)
{
    [McpServerTool(Name = "list_process_connections")]
    [Description(
        "Lists TCP endpoints and UDP bindings owned by one process. Each row has protocol, family, TCP state when known, "
        + "and the local address and port. includeRemoteAddresses adds the remote address and port; otherwise those fields are null. "
        + "A UDP row is a local binding and does not prove that the peer is reachable. No packet payloads, headers, or credentials are read. "
        + "expectedStartTime, from list_processes, rejects a reused pid. The list is a best-effort snapshot.")]
    public OperationResult<ProcessConnectionList> ListProcessConnections(
        [Description("Process id.")] int pid,
        [Description("Maximum rows to return. Clamped to 1..Vestigo:Diagnostics:MaxProcessConnections.")] int limit = 200,
        [Description("Include remote address and port. Local bindings are always returned.")] bool includeRemoteAddresses = false,
        [Description("Start time from list_processes. When set, a different process start time fails with process_identity_mismatch.")] DateTimeOffset? expectedStartTime = null)
    {
        if (pid <= 0)
        {
            return Fail(pid, "invalid_request", "pid must be a positive process id.");
        }

        int bounded = Math.Clamp(limit, 1, options.Diagnostics.MaxProcessConnections);
        ConnectionQuery query = source.Query(pid, bounded);
        if (!query.Success)
        {
            return Fail(pid, query.ErrorCode!, query.Error!);
        }

        if (expectedStartTime is { } expected && !WindowsProcessModuleSource.StartUnchanged(expected, query.StartTime))
        {
            return Fail(pid, "process_identity_mismatch", "The process start time does not match expectedStartTime. The pid may have been reused.");
        }

        List<ProcessConnection> connections = [];
        foreach (ConnectionRecord record in query.Connections)
        {
            connections.Add(new ProcessConnection(
                record.Protocol,
                record.Family,
                record.State,
                record.LocalAddress,
                record.LocalPort,
                includeRemoteAddresses ? record.RemoteAddress : null,
                includeRemoteAddresses ? record.RemotePort : null));
        }

        logger.LogInformation(
            "list_process_connections pid {Pid} count {Count} partial {Partial} truncated {Truncated}",
            pid,
            connections.Count,
            query.Partial,
            query.Truncated);
        return OperationResult<ProcessConnectionList>.Ok(new ProcessConnectionList(
            pid,
            query.StartTime,
            DateTimeOffset.UtcNow,
            query.Partial,
            query.Truncated,
            connections));
    }

    [McpServerTool(Name = "list_network_connections", ReadOnly = true)]
    [Description(
        "Lists TCP endpoints and UDP bindings for the whole machine. Filter by protocol, family, TCP state, local or remote port, "
        + "or owning pid. includeRemoteAddresses adds remote address and port; otherwise those fields are null. "
        + "limit is clamped to 1..Vestigo:Diagnostics:MaxNetworkConnections. The result always includes a TCP state histogram "
        + "and the total UDP binding count so TIME_WAIT, CLOSE_WAIT, and ephemeral-port pressure can be diagnosed in one call. "
        + "No packet payloads, headers, or credentials are read.")]
    public OperationResult<NetworkConnectionList> ListNetworkConnections(
        [Description("Maximum rows. Clamped to 1..Vestigo:Diagnostics:MaxNetworkConnections.")] int limit = 200,
        [Description("tcp or udp. Empty returns both.")] string? protocol = null,
        [Description("ipv4 or ipv6. Empty returns both.")] string? family = null,
        [Description("TCP state name such as established, timeWait, or closeWait.")] string? state = null,
        [Description("Match this local port.")] int? localPort = null,
        [Description("Match this remote port.")] int? remotePort = null,
        [Description("Owning process id.")] int? pid = null,
        [Description("Include remote address and port.")] bool includeRemoteAddresses = false)
    {
        if (protocol is not null && protocol.Trim() is not ("" or "tcp" or "udp"))
        {
            return FailList("invalid_request", "protocol must be tcp or udp.");
        }

        if (family is not null && family.Trim() is not ("" or "ipv4" or "ipv6"))
        {
            return FailList("invalid_request", "family must be ipv4 or ipv6.");
        }

        if (localPort is < 1 or > 65535 || remotePort is < 1 or > 65535)
        {
            return FailList("invalid_request", "Ports must be between 1 and 65535.");
        }

        if (pid is <= 0)
        {
            return FailList("invalid_request", "pid must be a positive process id.");
        }

        int bounded = Math.Clamp(limit, 1, options.Diagnostics.MaxNetworkConnections);
        TableRead table = IpHelperConnections.ReadAll(16_384);
        if (!table.Success)
        {
            return FailList(table.ErrorCode!, table.Error!);
        }

        IReadOnlyDictionary<int, string> names = ProcessNameLookup.Snapshot();
        List<NetworkConnection> connections = [];
        bool truncated = table.Truncated;
        foreach (ConnectionRecord record in table.Rows)
        {
            if (!NetworkConnectionProjection.Matches(record, protocol, family, state, localPort, remotePort, pid))
            {
                continue;
            }

            if (connections.Count >= bounded)
            {
                truncated = true;
                break;
            }

            names.TryGetValue(record.OwningPid, out string? processName);
            connections.Add(new NetworkConnection(
                record.OwningPid,
                processName,
                record.Protocol,
                record.Family,
                record.State,
                record.LocalAddress,
                record.LocalPort,
                includeRemoteAddresses ? record.RemoteAddress : null,
                includeRemoteAddresses ? record.RemotePort : null));
        }

        logger.LogInformation(
            "list_network_connections count {Count} truncated {Truncated} partial {Partial}",
            connections.Count,
            truncated,
            table.Partial);
        return OperationResult<NetworkConnectionList>.Ok(new NetworkConnectionList(
            DateTimeOffset.UtcNow,
            connections,
            NetworkConnectionProjection.Histogram(table.Rows),
            NetworkConnectionProjection.UdpCount(table.Rows),
            table.Partial,
            truncated));
    }

    private OperationResult<NetworkConnectionList> FailList(string code, string error)
    {
        logger.LogInformation("list_network_connections failed {Code}", code);
        return OperationResult<NetworkConnectionList>.Fail(code, error);
    }

    private OperationResult<ProcessConnectionList> Fail(int pid, string code, string error)
    {
        logger.LogInformation("list_process_connections pid {Pid} failed {Code}", pid, code);
        return OperationResult<ProcessConnectionList>.Fail(code, error);
    }
}
