using Vestigo.Processes;

namespace Vestigo.Diagnostics;

/// <summary>Runs the WASAPI probe in the console user's session so it sees that user's endpoints.</summary>
internal sealed class SessionAudioFormatProbe(IProcessLauncher launcher, SessionHelperClient helper) : IAudioFormatProbe
{
    public async Task<OperationResult<AudioFormatProbeResult>> ProbeAsync(
        string endpoint,
        string format,
        int durationMs,
        bool playback,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<AudioFormatProbeResult>.Fail("windows_required", "Audio format probes require Windows.");
        }

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated: false);
        if (!available.Success)
        {
            return available.ErrorCode == "no_interactive_session"
                ? OperationResult<AudioFormatProbeResult>.Fail("user_not_available", available.Error!, available.Reason)
                : available.FailAs<AudioFormatProbeResult>();
        }

        return await helper.RunAsync<AudioFormatProbeResult>(
            SessionHelper.BuildProbeAudioFormatArguments(endpoint, format, durationMs, playback),
            elevated: false,
            TimeSpan.FromMilliseconds(durationMs + 10_000),
            maxOutputBytes: 64 * 1024,
            cancellationToken).ConfigureAwait(false);
    }
}
