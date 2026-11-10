using Microsoft.Extensions.DependencyInjection;

using Vestigo.Configuration;
using Vestigo.RegistryProbe;

namespace Vestigo.Modules;

/// <summary>Opt-in, read-only registry probe confined to allow-listed prefixes.</summary>
public sealed class RegistryModule : VestigoModule
{
    public override string Name => "Registry";

    public override string Description =>
        "Read allow-listed registry values and key metadata. Opt-in. HKCU is the console user. The 32-bit and 64-bit views are explicit. There is no write tool.";

    /// <summary>Registry values can disclose machine configuration, so the module is opt-in.</summary>
    public override bool EnabledByDefault => false;

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(RegistryTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.AddSingleton<IRegistryStore, WindowsRegistryStore>();
    }
}
