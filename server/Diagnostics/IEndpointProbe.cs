namespace Vestigo.Diagnostics;

/// <summary>Resolves and, when requested, connects to one host from Vestigo's own network context.</summary>
public interface IEndpointProbe
{
    Task<EndpointProbeResult> ProbeAsync(string host, int port, string protocol, TimeSpan timeout, bool includeResolvedAddress, bool stun, CancellationToken cancellationToken);
}
