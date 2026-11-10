using Vestigo.Configuration;
using Vestigo.Power;

namespace Vestigo.Modules;

/// <summary>Read-only AC, battery, scheme, capability, and sleep-policy tools.</summary>
public sealed class PowerModule : VestigoModule
{
    public override string Name => "Power";

    public override string Description =>
        "Read AC and battery state, the active power scheme, platform capabilities, and AC/DC sleep policy. There is no write tool.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(PowerTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.AddSingleton<IPowerPlatform, WindowsPowerPlatform>();
        services.AddSingleton<PowerService>();
    }
}
