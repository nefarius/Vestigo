using Vestigo.Configuration;
using Vestigo.Services;

namespace Vestigo.Modules;

/// <summary>Inspect and control Windows services and kernel drivers.</summary>
public sealed class ServicesModule : VestigoModule
{
    public override string Name => "Services";

    public override string Description => "List, start, stop, and configure Windows services and kernel drivers.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(ServiceTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.AddSingleton<IServiceControl, WindowsServiceControl>();
    }
}
