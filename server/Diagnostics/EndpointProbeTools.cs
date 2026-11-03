using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

using Vestigo.Configuration;

namespace Vestigo.Diagnostics;

/// <summary>Probes one explicitly selected destination from Vestigo's own network context.</summary>
[McpServerToolType]
public sealed class EndpointProbeTools(IEndpointProbe probe, VestigoOptions options, ILogger<EndpointProbeTools> logger)
{
    [McpServerTool(Name = "test_endpoint")]
    [Description(
        "Observes one destination from Vestigo's own network context, which can differ from an application's routing, proxy, or credentials. "
        + "protocol 'dns' resolves the host, 'tcp' also connects, and 'tls' also performs a TLS handshake with certificate name and chain validation. "
        + "The call contacts only the host and port given here, and only when that host, or that host and port, is listed in Vestigo:Diagnostics:AllowedProbeDestinations. "
        + "timeoutMs is capped by Vestigo:Diagnostics:MaxEndpointProbeDuration. includeResolvedAddress reports the addresses used. "
        + "A TCP result is not a measure of media quality, jitter, or loss. No payloads, headers, or credentials are read. "
        + "Requires Vestigo:Diagnostics:AllowNetworkProbes.")]
    public async Task<OperationResult<EndpointProbeResult>> TestEndpoint(
        [Description("Host name or IP address. Not a URL.")] string host,
        [Description("TCP port, 1-65535. Ignored for protocol 'dns' except for the allow-list match.")] int port,
        [Description("'dns', 'tcp', or 'tls'.")] string protocol = "tcp",
        [Description("Budget in milliseconds. Capped by Vestigo:Diagnostics:MaxEndpointProbeDuration.")] int timeoutMs = 5000,
        [Description("Include resolved addresses in the result.")] bool includeResolvedAddress = false,
        CancellationToken cancellationToken = default)
    {
        if (!options.Diagnostics.AllowNetworkProbes)
        {
            return Disabled();
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
        if (selected is not ("dns" or "tcp" or "tls"))
        {
            return Invalid("protocol must be dns, tcp, or tls.");
        }

        if (timeoutMs < 1)
        {
            return Invalid("timeoutMs must be at least 1.");
        }

        string normalized = ProbeDestinationPolicy.NormalizeHost(host);
        if (!ProbeDestinationPolicy.Allows(normalized, port, options.Diagnostics.AllowedProbeDestinations))
        {
            logger.LogInformation("test_endpoint refused {Code}", "destination_not_allowed");
            return OperationResult<EndpointProbeResult>.Fail(
                "destination_not_allowed",
                "The destination is not listed in Vestigo:Diagnostics:AllowedProbeDestinations.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        int capped = (int)Math.Min(timeoutMs, options.Diagnostics.MaxEndpointProbeDuration.TotalMilliseconds);
        EndpointProbeResult result = await probe.ProbeAsync(
            normalized,
            port,
            selected,
            TimeSpan.FromMilliseconds(capped),
            includeResolvedAddress,
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation("test_endpoint protocol {Protocol} timedOut {TimedOut}", selected, result.TimedOut);
        return OperationResult<EndpointProbeResult>.Ok(Redact(result, includeResolvedAddress));
    }

    private OperationResult<EndpointProbeResult> Disabled()
    {
        logger.LogInformation("test_endpoint refused {Code}", "network_probes_disabled");
        return OperationResult<EndpointProbeResult>.Fail(
            "network_probes_disabled",
            "Network probes are disabled. Set Vestigo:Diagnostics:AllowNetworkProbes to true.");
    }

    private OperationResult<EndpointProbeResult> Invalid(string error)
    {
        logger.LogInformation("test_endpoint failed {Code}", "invalid_request");
        return OperationResult<EndpointProbeResult>.Fail("invalid_request", error);
    }

    private static EndpointProbeResult Redact(EndpointProbeResult result, bool includeResolvedAddress)
    {
        if (includeResolvedAddress)
        {
            return result;
        }

        return result with
        {
            Answers = result.Answers.Select(answer => answer with { Address = null }).ToArray(),
            Tcp = result.Tcp.Select(attempt => attempt with { Address = null }).ToArray()
        };
    }
}
