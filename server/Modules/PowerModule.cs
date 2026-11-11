using Vestigo.Configuration;
using Vestigo.Power;

namespace Vestigo.Modules;

/// <summary>Read-only AC, battery, scheme, capability, and sleep-policy tools.</summary>
public sealed class PowerModule : VestigoModule
{
    public override string Name => "Power";

    public override string Description =>
        "Read AC and battery state, the active power scheme, platform capabilities, AC/DC sleep policy, and the newest Power-Troubleshooter wake event. There is no write tool and no power-request list.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(PowerTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.AddSingleton<IPowerPlatform, WindowsPowerPlatform>();
        services.AddSingleton<PowerService>();
    }
}
