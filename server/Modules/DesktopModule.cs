using Microsoft.Extensions.DependencyInjection.Extensions;

using Vestigo.Configuration;
using Vestigo.Desktop;
using Vestigo.Files;
using Vestigo.Processes;
using Vestigo.Vision;

namespace Vestigo.Modules;

/// <summary>Lists Win32 windows, their processes, bounds, and the displays they are rendered on, and optionally captures screenshots.</summary>
public sealed class DesktopModule : VestigoModule
{
    public override string Name => "Desktop";

    public override string Description =>
        "List windows, their processes, bounds, and displays on the console user's or service desktop. Optionally take screenshots and inspect the screen with UI Automation, OCR and OpenCV.";

    /// <summary>Window titles from the user session are information disclosure, so the module is opt-in.</summary>
    public override bool EnabledByDefault => false;

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(DesktopTools), typeof(PerceptionTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.TryAddSingleton<IProcessLauncher, ProcessLauncher>();
        services.TryAddSingleton<SessionHelperClient>();
        services.TryAddSingleton<IRebootScheduler, Win32RebootScheduler>();
        services.TryAddSingleton<FileDeployer>();
        services.AddSingleton<DesktopInventory>();
        services.AddSingleton<CaptureCache>();
        services.AddSingleton<PerceptionService>();
    }
}
