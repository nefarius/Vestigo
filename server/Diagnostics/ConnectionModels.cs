namespace Vestigo.Diagnostics;

/// <summary>One TCP endpoint or UDP binding owned by a process. Remote fields are null unless requested.</summary>
public sealed record ProcessConnection(
    string Protocol,
    string Family,
    string? State,
    string? LocalAddress,
    int? LocalPort,
    string? RemoteAddress,
    int? RemotePort);

/// <summary>TCP and UDP inventory for one process. A UDP row is a local binding and does not prove reachability.</summary>
public sealed record ProcessConnectionList(
    int Pid,
    DateTimeOffset? StartTime,
    DateTimeOffset ObservedAt,
    bool Partial,
    bool Truncated,
    IReadOnlyList<ProcessConnection> Connections);

/// <summary>One row read from the owner-PID TCP or UDP table, before disclosure filtering.</summary>
public sealed record ConnectionRecord(
    string Protocol,
    string Family,
    string? State,
    string? LocalAddress,
    int? LocalPort,
    string? RemoteAddress,
    int? RemotePort,
    int OwningPid);

/// <summary>Result of reading connections for one process.</summary>
public sealed record ConnectionQuery(
    bool Success,
    string? ErrorCode,
    string? Error,
    DateTimeOffset? StartTime,
    bool Partial,
    bool Truncated,
    IReadOnlyList<ConnectionRecord> Connections)
{
    public static ConnectionQuery Ok(DateTimeOffset? startTime, bool partial, bool truncated, IReadOnlyList<ConnectionRecord> connections) =>
        new(true, null, null, startTime, partial, truncated, connections);

    public static ConnectionQuery Fail(string code, string error) =>
        new(false, code, error, null, false, false, []);
}
