using Microsoft.Extensions.DependencyInjection.Extensions;

using Vestigo.Audio;
using Vestigo.Configuration;
using Vestigo.Processes;

namespace Vestigo.Modules;

/// <summary>Lists audio endpoints and optionally switches the default playback, recording, or communications device.</summary>
public sealed class AudioModule : VestigoModule
{
    public override string Name => "Audio";

    public override string Description =>
        "List audio playback and recording endpoints, volumes, sessions, and defaults. Optionally switch the default device.";

    /// <summary>Endpoint names and the processes using them are information disclosure, so the module is opt-in.</summary>
    public override bool EnabledByDefault => false;

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(AudioTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.TryAddSingleton<IProcessLauncher, ProcessLauncher>();
        services.TryAddSingleton<SessionHelperClient>();
        services.AddSingleton<AudioInventory>();
    }
}
