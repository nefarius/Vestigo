using Vestigo.Configuration;
using Vestigo.EnvVars;

namespace Vestigo.Modules;

/// <summary>Reads and changes machine, user, and process environment variables.</summary>
public sealed class EnvironmentModule : VestigoModule
{
    public override string Name => "Environment";

    public override string Description => "Read and change machine, user, and process environment variables.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(EnvironmentTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.AddSingleton<IEnvironmentStore, RegistryEnvironmentStore>();
        services.AddSingleton<EnvironmentService>();
    }
}
