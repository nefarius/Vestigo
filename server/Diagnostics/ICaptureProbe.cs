namespace Vestigo.Diagnostics;

/// <summary>Runs a capture-support probe in the console user's session. Results contain no pixel data.</summary>
public interface ICaptureProbe
{
    Task<OperationResult<CaptureProbeResult>> ProbeAsync(
        string target,
        string backend,
        int durationMs,
        CancellationToken cancellationToken);
}
