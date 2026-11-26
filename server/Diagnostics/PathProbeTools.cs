using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

using Vestigo.Configuration;

namespace Vestigo.Diagnostics;

/// <summary>Gated hop-by-hop traceroute and optional Path MTU discovery.</summary>
[McpServerToolType]
public sealed class PathProbeTools(INetworkPathProbe probe, VestigoOptions options, ILogger<PathProbeTools> logger)
{
    [McpServerTool(Name = "probe_network_path")]
    [Description(
        "Sends ICMP TTL probes from 1 to maxHops and returns per-hop RTT percentiles and loss. "
        + "Hop addresses are omitted unless includeHopAddresses is true. "
        + "discoverPmtu performs a bounded don't-fragment binary search for the effective Path MTU and reports a blackhole when timed-out DF probes are seen. "
        + "maxHops is clamped to 1..Vestigo:Diagnostics:MaxPathProbeHops. "
        + "Requires Vestigo:Diagnostics:AllowNetworkProbes and AllowedProbeDestinations. No packet payloads are captured.")]
    public async Task<OperationResult<NetworkPathResult>> ProbeNetworkPath(
        [Description("Host name or IP address. Not a URL.")] string host,
        [Description("Maximum hops. Clamped to 1..MaxPathProbeHops.")] int maxHops = 30,
        [Description("Samples per hop (1-5).")] int samplesPerHop = 3,
        [Description("Include hop IP addresses.")] bool includeHopAddresses = false,
        [Description("Also search for the effective Path MTU.")] bool discoverPmtu = false,
        CancellationToken cancellationToken = default)
    {
        if (!options.Diagnostics.AllowNetworkProbes)
        {
            logger.LogInformation("probe_network_path refused {Code}", "network_probes_disabled");
            return OperationResult<NetworkPathResult>.Fail(
                "network_probes_disabled",
                "Network probes are disabled. Set Vestigo:Diagnostics:AllowNetworkProbes to true.");
        }

        if (string.IsNullOrWhiteSpace(host) || !ProbeDestinationPolicy.IsValidHost(host))
        {
            return Invalid("host must be a single host name or IP address.");
        }

        string normalized = ProbeDestinationPolicy.NormalizeHost(host);
        if (!ProbeDestinationPolicy.AllowsHost(normalized, options.Diagnostics.AllowedProbeDestinations))
        {
            logger.LogInformation("probe_network_path refused {Code}", "destination_not_allowed");
            return OperationResult<NetworkPathResult>.Fail(
                "destination_not_allowed",
                "The destination is not listed in Vestigo:Diagnostics:AllowedProbeDestinations.");
        }

        int hops = Math.Clamp(maxHops, 1, options.Diagnostics.MaxPathProbeHops);
        int samples = Math.Clamp(samplesPerHop, 1, 5);
        OperationResult<NetworkPathResult> result = await probe.ProbeAsync(
            normalized,
            hops,
            samples,
            TimeSpan.FromSeconds(2),
            includeHopAddresses,
            discoverPmtu,
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation("probe_network_path success {Success} truncated {Truncated}", result.Success, result.Value?.Truncated);
        return result;
    }

    private OperationResult<NetworkPathResult> Invalid(string error)
    {
        logger.LogInformation("probe_network_path failed {Code}", "invalid_request");
        return OperationResult<NetworkPathResult>.Fail("invalid_request", error);
    }
}
