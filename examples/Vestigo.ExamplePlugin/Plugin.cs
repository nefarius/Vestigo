using System.ComponentModel;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Server;

using Vestigo.Extensibility;
using Vestigo.ExamplePlugin.Dependency;

namespace Vestigo.ExamplePlugin;

public sealed class Plugin : IVestigoPlugin
{
    public IReadOnlyList<VestigoModule> Modules { get; } = [new EchoModule()];
}

public sealed class EchoModule : VestigoModule
{
    public override string Name => "ExampleEcho";
    public override string Description => "Harmless echo demonstrating an independently deployed plugin.";
    public override IReadOnlyList<Type> ToolTypes => [typeof(EchoTools)];
    public override void ConfigureServices(IServiceCollection services, PluginContext context) => services.AddSingleton<EchoService>();
    public override void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context) =>
        endpoints.MapGet("/status", (EchoService service) => service.Echo("ok"));
}

[McpServerToolType]
public sealed class EchoTools(EchoService service)
{
    [McpServerTool(Name = "example_plugin_echo", ReadOnly = true)]
    [Description("Return the supplied string unchanged.")]
    public string Echo(string value) => service.Echo(value);
}
