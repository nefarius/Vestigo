using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Vestigo.Extensibility;

/// <summary>A feature's services, MCP tools and optional authenticated HTTP endpoints.</summary>
public abstract class VestigoModule
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract IReadOnlyList<Type> ToolTypes { get; }
    public virtual bool EnabledByDefault => false;
    public virtual IReadOnlyList<string> Dependencies => [];
    /// <summary>Informational security capabilities; never a security boundary.</summary>
    public virtual IReadOnlyList<string> Capabilities => [];
    public virtual void ConfigureServices(IServiceCollection services, PluginContext context) { }
    /// <summary>Routes are relative to /plugins/{plugin-id} and inherit host authentication.</summary>
    public virtual void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context) { }
}

/// <summary>Selected host state. Settings is the module's Vestigo:Modules:{Name} section.</summary>
public sealed record PluginContext(IConfiguration Settings, string ContentDirectory, bool AuthenticationEnabled);

/// <summary>Explicit public, concrete entry type with a public parameterless constructor.</summary>
public interface IVestigoPlugin
{
    IReadOnlyList<VestigoModule> Modules { get; }
}

/// <summary>Independent of the Vestigo application release version.</summary>
public static class PluginApi
{
    public static Version Version { get; } = new(1, 0);
    public static bool IsCompatible(Version required) =>
        required.Major == Version.Major && required.Minor <= Version.Minor && required.Build < 0;
}
