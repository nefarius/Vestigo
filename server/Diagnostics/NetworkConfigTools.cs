using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace Vestigo.Diagnostics;

/// <summary>Read-only routing, neighbor, DNS client, and proxy inventory.</summary>
[McpServerToolType]
public sealed class NetworkConfigTools(INetworkConfigurationSource source, ILogger<NetworkConfigTools> logger)
{
    [McpServerTool(Name = "get_network_configuration", ReadOnly = true)]
    [Description(
        "Returns the routing table, a bounded ARP/NDP neighbor table, DNS client servers, search suffixes, NRPT rules, "
        + "and WinHTTP/IE proxy URLs, PAC, and bypass lists. Proxy credentials are never read. "
        + "partial is true when a source was denied. truncated is true when a table exceeded the bound.")]
    public OperationResult<NetworkConfiguration> GetNetworkConfiguration(
        [Description("Maximum route rows to return (1-4096).")] int maxRoutes = 512,
        [Description("Maximum neighbor rows to return (1-4096).")] int maxNeighbors = 512)
    {
        OperationResult<NetworkConfiguration> result = source.Read(Math.Clamp(maxRoutes, 1, 4096), Math.Clamp(maxNeighbors, 1, 4096));
        logger.LogInformation(
            "get_network_configuration success {Success} partial {Partial} truncated {Truncated}",
            result.Success,
            result.Value?.Partial,
            result.Value?.Truncated);
        return result;
    }
}
