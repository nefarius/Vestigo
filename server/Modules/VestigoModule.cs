using Vestigo.Configuration;

namespace Vestigo.Modules;

/// <summary>
///     A self-contained feature area: its services, MCP tool types, and optional HTTP endpoints.
///     Adding a feature means adding a module class and listing it in <see cref="ModuleRegistry" />.
/// </summary>
public abstract class VestigoModule : Extensibility.VestigoModule
{
    /// <summary>Stable name used in <c>Vestigo:Modules:&lt;Name&gt;:Enabled</c>.</summary>
    public abstract override string Name { get; }

    /// <summary>One-line description for documentation.</summary>
    public abstract override string Description { get; }

    /// <summary>Classes marked <c>McpServerToolType</c> that this module contributes.</summary>
    public abstract override IReadOnlyList<Type> ToolTypes { get; }

    /// <summary>Whether the module runs when configuration does not mention it.</summary>
    public override bool EnabledByDefault => true;

    /// <summary>Names of modules that must be enabled whenever this module is enabled.</summary>
    public override IReadOnlyList<string> Dependencies => [];

    public virtual void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
    }

    public virtual void MapEndpoints(WebApplication app, VestigoOptions options)
    {
    }

    public bool IsEnabled(VestigoOptions options) =>
        options.Modules.TryGetValue(Name, out ModuleSettings? settings) ? settings.Enabled : EnabledByDefault;
}
