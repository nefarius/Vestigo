using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;

namespace Vestigo.Diagnostics;

/// <summary>MCP tool that observes whether a capture backend delivers frames. It never returns pixels.</summary>
[McpServerToolType]
public sealed class CaptureProbeTools(ICaptureProbe probe, VestigoOptions options)
{
    [McpServerTool(Name = "probe_capture_support")]
    [Description(
        "Observes whether frames arrive for one display in the console user's session. backend 'dxgi' uses Desktop Duplication "
        + "through the session helper. backend 'wgc' is unsupported in this build. durationMs is capped by "
        + "Vestigo:Diagnostics:MaxProbeDuration. The result reports the session, desktop name, what the API advertised, "
        + "how many frames arrived, first-frame latency, and the final HRESULT. Frame arrival is an observation and proves no cause. "
        + "No pixel data is returned. Requires Vestigo:Diagnostics:AllowCaptureProbes. Fails with user_not_available when nobody is logged on.")]
    public Task<OperationResult<CaptureProbeResult>> ProbeCaptureSupport(
        [Description("'primary' or a GDI device name such as \\\\.\\DISPLAY1.")] string target = "primary",
        [Description("'dxgi' or 'wgc'. Only dxgi is implemented.")] string backend = "dxgi",
        [Description("How long to wait for frames, in milliseconds. Capped by Vestigo:Diagnostics:MaxProbeDuration.")] int durationMs = 1000,
        CancellationToken cancellationToken = default)
    {
        if (!options.Diagnostics.AllowCaptureProbes)
        {
            return Task.FromResult(OperationResult<CaptureProbeResult>.Fail(
                "probes_disabled",
                "Capture probes are disabled. Set Vestigo:Diagnostics:AllowCaptureProbes to true."));
        }

        if (string.IsNullOrWhiteSpace(target) || target.Length > 260 || target.IndexOfAny(['\r', '\n']) >= 0)
        {
            return Task.FromResult(OperationResult<CaptureProbeResult>.Fail("invalid_request", "target must be 'primary' or a display device name."));
        }

        if (durationMs < 1)
        {
            return Task.FromResult(OperationResult<CaptureProbeResult>.Fail("invalid_request", "durationMs must be at least 1."));
        }

        int capped = (int)Math.Min(durationMs, options.Diagnostics.MaxProbeDuration.TotalMilliseconds);
        return probe.ProbeAsync(target.Trim(), backend.Trim(), capped, cancellationToken);
    }
}
