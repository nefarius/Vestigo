using Vestigo.Configuration;
using Vestigo.RemoteDebugger;

namespace Vestigo.Modules;

/// <summary>Installs and runs the Visual Studio Remote Debugging Tools. Needs the Jobs and Processes modules.</summary>
public sealed class RemoteDebuggerModule : VestigoModule
{
    public override string Name => "RemoteDebugger";

    public override string Description => "Install, start, and stop Visual Studio Remote Debugging Tools (msvsmon). Needs Jobs and Processes.";

    /// <summary>Installs software and launches msvsmon, so it is opt-in.</summary>
    public override bool EnabledByDefault => false;

    public override IReadOnlyList<string> Dependencies { get; } = ["Jobs", "Processes"];

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(RemoteDebuggerTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        // The module needs JobManager (Jobs), IProcessLauncher and AppCloser (Processes).
        services.AddHttpClient(RemoteDebuggerService.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<IMsvsmonProbe, SystemMsvsmonProbe>();
        services.AddSingleton(provider =>
        {
            VestigoOptions current = provider.GetRequiredService<VestigoOptions>();
            string[] roots = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                }
                .Where(path => !string.IsNullOrEmpty(path)).ToArray();
            return new MsvsmonLocator(provider.GetRequiredService<IMsvsmonProbe>(), roots, current.RemoteDebugger.ExtraSearchPaths);
        });
        services.AddSingleton<IAuthenticodeVerifier, AuthenticodeVerifier>();
        services.AddSingleton<RemoteDebuggerService>();
    }
}
