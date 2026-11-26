using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

using Vestigo.Configuration;

namespace Vestigo.Diagnostics;

/// <summary>Per-connection TCP EStats for a selected 4-tuple or a process's established connections.</summary>
[McpServerToolType]
public sealed class TcpEstatsTools(ITcpEstatsSource source, ILogger<TcpEstatsTools> logger)
{
    [McpServerTool(Name = "inspect_tcp_connection_stats")]
    [Description(
        "Reads TCP EStats (smoothed RTT, variance, min/max RTT, cwnd, ssthresh, retransmits, dup-ACKs, SACKs, zero-window, bytes acked) "
        + "for a selected established connection or the top established connections of a pid. "
        + "durationMs, 0..5000, enables collection only when it was off and restores the prior state afterwards. "
        + "limit is clamped to 1..16. No packet payloads are captured.")]
    public Task<OperationResult<TcpConnectionStatsList>> InspectTcpConnectionStats(
        [Description("Local address of the connection.")] string? localAddress = null,
        [Description("Local port.")] int? localPort = null,
        [Description("Remote address of the connection.")] string? remoteAddress = null,
        [Description("Remote port.")] int? remotePort = null,
        [Description("Owning process id. When set without a 4-tuple, inspects that process's established connections.")] int? pid = null,
        [Description("Maximum connections to inspect (1-16).")] int limit = 4,
        [Description("Optional observation window in milliseconds (0-5000) during which EStats collection is enabled.")] int durationMs = 0,
        CancellationToken cancellationToken = default)
    {
        if (localPort is < 1 or > 65535 || remotePort is < 1 or > 65535)
        {
            return Task.FromResult(Fail("invalid_request", "Ports must be between 1 and 65535."));
        }

        if (pid is <= 0)
        {
            return Task.FromResult(Fail("invalid_request", "pid must be a positive process id."));
        }

        if (durationMs is < 0 or > 5000)
        {
            return Task.FromResult(Fail("invalid_request", "durationMs must be between 0 and 5000."));
        }

        if (string.IsNullOrWhiteSpace(localAddress) && localPort is null && string.IsNullOrWhiteSpace(remoteAddress)
            && remotePort is null && pid is null)
        {
            return Task.FromResult(Fail("invalid_request", "Select a connection 4-tuple or a pid."));
        }

        int bounded = Math.Clamp(limit, 1, 16);
        return Inspect(localAddress, localPort, remoteAddress, remotePort, pid, bounded, TimeSpan.FromMilliseconds(durationMs), cancellationToken);
    }

    private async Task<OperationResult<TcpConnectionStatsList>> Inspect(
        string? localAddress,
        int? localPort,
        string? remoteAddress,
        int? remotePort,
        int? pid,
        int limit,
        TimeSpan observe,
        CancellationToken cancellationToken)
    {
        OperationResult<TcpConnectionStatsList> result = await source.InspectAsync(
            localAddress,
            localPort,
            remoteAddress,
            remotePort,
            pid,
            limit,
            observe,
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "inspect_tcp_connection_stats success {Success} count {Count}",
            result.Success,
            result.Value?.Connections.Count);
        return result;
    }

    private OperationResult<TcpConnectionStatsList> Fail(string code, string error)
    {
        logger.LogInformation("inspect_tcp_connection_stats failed {Code}", code);
        return OperationResult<TcpConnectionStatsList>.Fail(code, error);
    }
}
