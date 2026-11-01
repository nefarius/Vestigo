using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Processes;

namespace Vestigo.Audio;

/// <summary>MCP tools that list Core Audio endpoints and switch the default playback or recording device.</summary>
[McpServerToolType]
public sealed class AudioTools(AudioInventory inventory, VestigoOptions options)
{
    [McpServerTool(Name = "list_audio_endpoints")]
    [Description(
        "Lists Windows audio playback (render) and recording (capture) endpoints with friendly name, state, form "
        + "factor, default-role flags, volume, mute, a short peak-meter sample, and the processes currently playing "
        + "or recording. context 'interactive' (default) reads the console user's session through the session helper.")]
    public Task<OperationResult<AudioEndpointListResult>> ListAudioEndpoints(
        [Description("'all' (default), 'render' (speakers) or 'capture' (microphones).")] string flow = AudioSelector.FlowAll,
        [Description("'active' (default) or 'all' (includes disabled, unplugged and not-present).")] string state = AudioSelector.StateActive,
        [Description("Return only the endpoint with this id.")] string? id = null,
        [Description("Include per-process audio sessions. Defaults to true.")] bool includeSessions = true,
        [Description("'interactive' (default) or 'service'.")] string context = "interactive",
        CancellationToken cancellationToken = default)
    {
        if (!ProcessTools.TryParseContext(context, out LaunchContext launchContext))
        {
            return Task.FromResult(OperationResult<AudioEndpointListResult>.Fail("invalid_request", "context must be 'service' or 'interactive'."));
        }

        if (!AudioSelector.TryParseFlow(flow, out string parsedFlow, out string? error) ||
            !AudioSelector.TryParseState(state, out string parsedState, out error))
        {
            return Task.FromResult(OperationResult<AudioEndpointListResult>.Fail("invalid_request", error!));
        }

        AudioSelector selector = new(
            parsedFlow,
            parsedState,
            string.IsNullOrWhiteSpace(id) ? null : id.Trim(),
            includeSessions,
            options.Audio.MeterSampleMilliseconds);
        return inventory.ListAsync(selector, launchContext, cancellationToken);
    }

    [McpServerTool(Name = "set_default_audio_endpoint")]
    [Description(
        "Sets the default audio playback or recording endpoint. role 'default' is Windows' Default Device (console "
        + "and multimedia). 'communications' is the Default Communication Device. 'all' sets both. The flow follows "
        + "from the endpoint id. Requires Vestigo:Audio:AllowSetDefault. The result lists endpoints of the same flow "
        + "so the new defaults can be confirmed.")]
    public Task<OperationResult<SetDefaultAudioResult>> SetDefaultAudioEndpoint(
        [Description("Endpoint id from list_audio_endpoints.")] string id,
        [Description("'default' (Default Device), 'communications' (Default Communication Device) or 'all'.")] string role = AudioSelector.RoleDefault,
        [Description("'interactive' (default) or 'service'.")] string context = "interactive",
        CancellationToken cancellationToken = default)
    {
        if (!options.Audio.AllowSetDefault)
        {
            return Task.FromResult(OperationResult<SetDefaultAudioResult>.Fail(
                "set_default_disabled",
                "Changing the default audio endpoint is disabled. Set Vestigo:Audio:AllowSetDefault to true."));
        }

        if (!ProcessTools.TryParseContext(context, out LaunchContext launchContext))
        {
            return Task.FromResult(OperationResult<SetDefaultAudioResult>.Fail("invalid_request", "context must be 'service' or 'interactive'."));
        }

        if (!AudioSelector.TryParseRole(role, out string parsedRole, out _, out string? error))
        {
            return Task.FromResult(OperationResult<SetDefaultAudioResult>.Fail("invalid_request", error!));
        }

        return inventory.SetDefaultAsync(id, parsedRole, launchContext, cancellationToken);
    }
}
