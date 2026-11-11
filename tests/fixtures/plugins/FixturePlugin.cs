using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Vestigo.Extensibility;

namespace Vestigo.PluginFixture;

public sealed class Plugin : IVestigoPlugin
{
    public IReadOnlyList<VestigoModule> Modules => [new PrivateModule(), new SecondaryModule()];
}

public sealed class PrivateModule : VestigoModule
{
    public override bool EnabledByDefault => true; // The host must still require explicit external activation.
    public override string Name => "Private" + PrivateService.Major;
    public override string Description => "Versioned private dependency.";
    public override IReadOnlyList<Type> ToolTypes => [];
}

public sealed class SecondaryModule : VestigoModule
{
    public override string Name => "Secondary" + PrivateService.Major;
    public override string Description => "A second module, depending on a built-in and a sibling.";
    public override IReadOnlyList<string> Dependencies => ["Jobs", "Private" + PrivateService.Major];
    public override IReadOnlyList<Type> ToolTypes => [];
}

public sealed class AnonymousPlugin : IVestigoPlugin
{
    public IReadOnlyList<VestigoModule> Modules => [new AnonymousModule()];
}

public sealed class AnonymousModule : VestigoModule
{
    public override string Name => "AnonymousFixture";
    public override string Description => "Anonymous endpoint rejection fixture.";
    public override IReadOnlyList<Type> ToolTypes => [];
    public override void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context) =>
        endpoints.MapGet("/anonymous", () => "unsafe").AllowAnonymous();
}

public sealed class GroupAnonymousPlugin : IVestigoPlugin
{
    public IReadOnlyList<VestigoModule> Modules => [new GroupAnonymousModule()];
}

public sealed class GroupAnonymousModule : VestigoModule
{
    public override string Name => "GroupAnonymousFixture";
    public override string Description => "Anonymous route-group rejection fixture.";
    public override IReadOnlyList<Type> ToolTypes => [];
    public override void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
        RouteGroupBuilder group = (RouteGroupBuilder)endpoints;
        group.AllowAnonymous();
        group.MapGet("/group/anonymous", () => "unsafe");
    }
}

public sealed class LegacyPlugin : IVestigoPlugin
{
    public IReadOnlyList<VestigoModule> Modules => [new LegacyModule()];
}

public sealed class LegacyModule : VestigoModule
{
    public override string Name => "Legacy" + Legacy.LegacyTypes.Size;
    public override string Description => "A .NET Standard dependency with an older framework reference.";
    public override IReadOnlyList<Type> ToolTypes => [];
}

public sealed class FailedPlugin : IVestigoPlugin
{
    public IReadOnlyList<VestigoModule> Modules => throw new InvalidOperationException("fixture-secret-must-not-be-logged");
}

public sealed class FailedServicesPlugin : IVestigoPlugin
{
    public IReadOnlyList<VestigoModule> Modules => [new FailedServicesModule()];
}

public sealed class FailedServicesModule : VestigoModule
{
    public override string Name => "FailedServices";
    public override string Description => "Service-registration failure fixture.";
    public override IReadOnlyList<Type> ToolTypes => [];
    public override void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton<PrivateService>();
        throw new InvalidOperationException("fixture-secret-must-not-be-logged");
    }
}

public sealed class MissingDiPlugin : IVestigoPlugin
{
    public IReadOnlyList<VestigoModule> Modules => [new MissingDiModule()];
}

public sealed class MissingDiModule : VestigoModule
{
    public override string Name => "MissingDi";
    public override string Description => "Missing constructor dependency fixture.";
    public override IReadOnlyList<Type> ToolTypes => [typeof(MissingDiTools)];
}

[McpServerToolType]
public sealed class MissingDiTools(PrivateService missing)
{
    [McpServerTool(Name = "fixture_missing_di")]
    public string Invoke() => missing.ToString()!;
}
