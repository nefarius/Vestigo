using Microsoft.Extensions.DependencyInjection.Extensions;

using Vestigo.Configuration;
using Vestigo.Processes;

namespace Vestigo.Modules;

/// <summary>Starts, lists, closes, and kills processes as the service or the console user.</summary>
public sealed class ProcessesModule : VestigoModule
{
    public override string Name => "Processes";

    public override string Description => "Run, list, close, and kill processes as the service or the console user.";

    /// <summary>Runs arbitrary programs, so it is opt-in and requires API tokens.</summary>
    public override bool EnabledByDefault => false;

    public override IReadOnlyList<string> Dependencies { get; } = ["Jobs"];

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(ProcessTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        // JobManager comes from the Jobs module (declared in Dependencies). Desktop also registers the launcher.
        services.TryAddSingleton<IProcessLauncher, ProcessLauncher>();
        services.TryAddSingleton<SessionHelperClient>();
        services.AddSingleton<ExecutableResolver>();
        services.AddSingleton<AppCloser>();
    }
}
