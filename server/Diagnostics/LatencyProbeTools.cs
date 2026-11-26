using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

using Vestigo.Configuration;

namespace Vestigo.Diagnostics;

/// <summary>Gated multi-sample ICMP or TCP latency, jitter, and loss probes.</summary>
[McpServerToolType]
public sealed class LatencyProbeTools(INetworkLatencyProbe probe, VestigoOptions options, ILogger<LatencyProbeTools> logger)
{
    [McpServerTool(Name = "probe_network_latency")]
    [Description(
        "Sends a bounded number of icmp echo or tcp connect samples to one allow-listed destination and returns RTT percentiles, "
        + "RFC 3550 jitter, sent/received/loss percent, and per-sample status. "
        + "samples is clamped to 1..Vestigo:Diagnostics:MaxNetworkProbeSamples. The call is capped by MaxLatencyProbeDuration. "
        + "Requires Vestigo:Diagnostics:AllowNetworkProbes and AllowedProbeDestinations. No packet payloads are captured.")]
    public async Task<OperationResult<NetworkLatencyResult>> ProbeNetworkLatency(
        [Description("Host name or IP address. Not a URL.")] string host,
        [Description("Port for tcp probes, 1-65535. Ignored for icmp except allow-list matching.")] int port = 443,
        [Description("icmp or tcp.")] string protocol = "icmp",
        [Description("Number of samples. Clamped to 1..MaxNetworkProbeSamples.")] int samples = 10,
        [Description("Include the destination in sample errors. Addresses are still omitted unless this is true.")] bool includeResolvedAddress = false,
        CancellationToken cancellationToken = default)
    {
        if (!options.Diagnostics.AllowNetworkProbes)
        {
            logger.LogInformation("probe_network_latency refused {Code}", "network_probes_disabled");
            return OperationResult<NetworkLatencyResult>.Fail(
                "network_probes_disabled",
                "Network probes are disabled. Set Vestigo:Diagnostics:AllowNetworkProbes to true.");
        }

        if (string.IsNullOrWhiteSpace(host) || !ProbeDestinationPolicy.IsValidHost(host))
        {
            return Invalid("host must be a single host name or IP address.");
        }

        if (port is < 1 or > 65535)
        {
            return Invalid("port must be between 1 and 65535.");
        }

        string selected = protocol.Trim().ToLowerInvariant();
        if (selected is not ("icmp" or "tcp"))
        {
            return Invalid("protocol must be icmp or tcp.");
        }

        string normalized = ProbeDestinationPolicy.NormalizeHost(host);
        bool allowed = selected == "icmp"
            ? ProbeDestinationPolicy.AllowsHost(normalized, options.Diagnostics.AllowedProbeDestinations)
            : ProbeDestinationPolicy.Allows(normalized, port, options.Diagnostics.AllowedProbeDestinations);
        if (!allowed)
        {
            logger.LogInformation("probe_network_latency refused {Code}", "destination_not_allowed");
            return OperationResult<NetworkLatencyResult>.Fail(
                "destination_not_allowed",
                "The destination is not listed in Vestigo:Diagnostics:AllowedProbeDestinations.");
        }

        int count = Math.Clamp(samples, 1, options.Diagnostics.MaxNetworkProbeSamples);
        OperationResult<NetworkLatencyResult> result = await probe.ProbeAsync(
            normalized,
            port,
            selected,
            count,
            options.Diagnostics.MaxLatencyProbeDuration,
            includeResolvedAddress,
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation("probe_network_latency protocol {Protocol} success {Success}", selected, result.Success);
        return result;
    }

    private OperationResult<NetworkLatencyResult> Invalid(string error)
    {
        logger.LogInformation("probe_network_latency failed {Code}", "invalid_request");
        return OperationResult<NetworkLatencyResult>.Fail("invalid_request", error);
    }
}
