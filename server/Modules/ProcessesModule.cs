using Vestigo.Configuration;
using Vestigo.Processes;

namespace Vestigo.Modules;

/// <summary>Starts, lists, closes, and kills processes as the service or the console user.</summary>
public sealed class ProcessesModule : VestigoModule
{
    public override string Name => "Processes";

    public override string Description => "Run, list, close, and kill processes as the service or the console user.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(ProcessTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        // The module needs JobManager from the Jobs module.
        services.AddSingleton<IProcessLauncher, ProcessLauncher>();
        services.AddSingleton<ExecutableResolver>();
        services.AddSingleton<AppCloser>();
    }
}
