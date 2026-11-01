using Microsoft.Extensions.DependencyInjection.Extensions;

using Vestigo.Configuration;
using Vestigo.Desktop;
using Vestigo.Processes;

namespace Vestigo.Modules;

/// <summary>Lists Win32 windows, their processes, bounds, and the displays they are rendered on.</summary>
public sealed class DesktopModule : VestigoModule
{
    public override string Name => "Desktop";

    public override string Description => "List windows, their processes, bounds, and displays on the console user's or service desktop.";

    /// <summary>Window titles from the user session are information disclosure, so the module is opt-in.</summary>
    public override bool EnabledByDefault => false;

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(DesktopTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.TryAddSingleton<IProcessLauncher, ProcessLauncher>();
        services.TryAddSingleton<SessionHelperClient>();
        services.AddSingleton<DesktopInventory>();
    }
}
