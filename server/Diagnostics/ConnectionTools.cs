using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

using Vestigo.Configuration;

namespace Vestigo.Diagnostics;

/// <summary>Lists TCP endpoints and UDP bindings owned by one process.</summary>
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

    private OperationResult<ProcessConnectionList> Fail(int pid, string code, string error)
    {
        logger.LogInformation("list_process_connections pid {Pid} failed {Code}", pid, code);
        return OperationResult<ProcessConnectionList>.Fail(code, error);
    }
}
