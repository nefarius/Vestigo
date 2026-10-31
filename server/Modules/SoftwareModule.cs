using Vestigo.Configuration;
using Vestigo.Software;

namespace Vestigo.Modules;

/// <summary>Lists installed software and optionally uninstalls it silently.</summary>
public sealed class SoftwareModule : VestigoModule
{
    public override string Name => "Software";

    public override string Description => "List installed software and run silent uninstalls.";

    public override IReadOnlyList<string> Dependencies { get; } = ["Jobs"];

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(SoftwareTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.AddSingleton<ISoftwareInventory, RegistrySoftwareInventory>();
        services.AddSingleton<UninstallPlanner>();
        services.AddSingleton<IUninstallRunner, ServiceUninstallRunner>();
    }
}
