using Microsoft.AspNetCore.Authorization;

using Vestigo.Extensibility;
using Vestigo.Configuration;

namespace Vestigo.Plugins;

internal sealed class ExternalModuleAdapter(Extensibility.VestigoModule module, string pluginId, PluginContext context)
    : Modules.VestigoModule
{
    public string PluginId => pluginId;
    public override string Name { get; } = module.Name;
    public override string Description { get; } = module.Description;
    public override IReadOnlyList<Type> ToolTypes { get; } = module.ToolTypes.ToArray();
    public override IReadOnlyList<string> Dependencies { get; } = module.Dependencies.ToArray();
    public override IReadOnlyList<string> Capabilities { get; } = module.Capabilities.ToArray();
    // Installation must never activate an external module, even if it overrides its default.
    public override bool EnabledByDefault => false;
    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        try { module.ConfigureServices(services, context); }
        catch (Exception ex) { throw new InvalidOperationException($"Plugin '{pluginId}', module '{Name}' service registration failed ({ex.GetType().Name}); startup aborted."); }
    }

    public override void MapEndpoints(WebApplication app, VestigoOptions options)
    {
        RouteGroupBuilder group = app.MapGroup($"/plugins/{pluginId}");
        if (options.AuthenticationEnabled) group.RequireAuthorization();
        try { module.MapEndpoints(group, context); }
        catch (Exception ex) { throw new InvalidOperationException($"Plugin '{pluginId}', module '{Name}' endpoint registration failed ({ex.GetType().Name}); startup aborted."); }
        foreach (Endpoint endpoint in ((IEndpointRouteBuilder)group).DataSources.SelectMany(source => source.Endpoints))
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
                throw new InvalidOperationException($"Plugin '{pluginId}', module '{Name}' declares an anonymous endpoint; startup aborted.");
    }
}
