using Vestigo.Processes;

namespace Vestigo.Diagnostics;

/// <summary>Starts <c>probe-capture</c> in the console session. <c>wgc</c> is unsupported in this build.</summary>
internal sealed class SessionCaptureProbe(IProcessLauncher launcher, SessionHelperClient helper) : ICaptureProbe
{
    public async Task<OperationResult<CaptureProbeResult>> ProbeAsync(
        string target,
        string backend,
        int durationMs,
        CancellationToken cancellationToken)
    {
        if (backend.Equals("wgc", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<CaptureProbeResult>.Fail(
                "unsupported",
                "Windows Graphics Capture is not available. This build does not reference WinRT projections. Use backend dxgi.");
        }

        if (!backend.Equals("dxgi", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<CaptureProbeResult>.Fail("invalid_request", "backend must be 'dxgi' or 'wgc'.");
        }

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated: false);
        if (!available.Success)
        {
            return available.ErrorCode == "no_interactive_session"
                ? OperationResult<CaptureProbeResult>.Fail("user_not_available", available.Error!, available.Reason)
                : available.FailAs<CaptureProbeResult>();
        }

        return await helper.RunAsync<CaptureProbeResult>(
            SessionHelper.BuildProbeCaptureArguments(target, "dxgi", durationMs),
            elevated: false,
            TimeSpan.FromMilliseconds(durationMs + 10_000),
            maxOutputBytes: 256 * 1024,
            cancellationToken).ConfigureAwait(false);
    }
}
