using Vestigo.Processes;

namespace Vestigo.Audio;

/// <summary>
///     Lists audio endpoints and switches defaults in-process (service context) or through the session helper
///     (interactive context). Per-user audio sessions and defaults are only visible from the console user's session.
/// </summary>
public sealed class AudioInventory(IProcessLauncher launcher, SessionHelperClient helper)
{
    internal const long HelperMaxOutputBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(30);

    public async Task<OperationResult<AudioEndpointListResult>> ListAsync(
        AudioSelector selector,
        LaunchContext context,
        CancellationToken cancellationToken)
    {
        if (context == LaunchContext.Service)
        {
            return await Task.Run(() => AudioEndpoints.List(selector), cancellationToken).ConfigureAwait(false);
        }

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated: false);
        if (!available.Success)
        {
            return OperationResult<AudioEndpointListResult>.Fail(available.ErrorCode!, available.Error!);
        }

        return await helper.RunAsync<AudioEndpointListResult>(
            SessionHelper.BuildListAudioArguments(selector),
            elevated: false,
            HelperTimeout,
            HelperMaxOutputBytes,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<SetDefaultAudioResult>> SetDefaultAsync(
        string id,
        string role,
        LaunchContext context,
        CancellationToken cancellationToken)
    {
        if (context == LaunchContext.Service)
        {
            return await Task.Run(() => AudioEndpoints.SetDefault(id, role), cancellationToken).ConfigureAwait(false);
        }

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated: false);
        if (!available.Success)
        {
            return OperationResult<SetDefaultAudioResult>.Fail(available.ErrorCode!, available.Error!);
        }

        return await helper.RunAsync<SetDefaultAudioResult>(
            SessionHelper.BuildSetDefaultAudioArguments(id, role),
            elevated: false,
            HelperTimeout,
            HelperMaxOutputBytes,
            cancellationToken).ConfigureAwait(false);
    }
}
