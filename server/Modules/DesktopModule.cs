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

    public override IReadOnlyList<Type> ToolTypes { get; } =
        [typeof(DesktopTools), typeof(PerceptionTools), typeof(WindowInspectionTools), typeof(WindowEventTools), typeof(WindowPlacementTools), typeof(DisplayLayoutTools), typeof(ManagedDesktopTools), typeof(VirtualDisplayTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.TryAddSingleton<IProcessLauncher, ProcessLauncher>();
        services.TryAddSingleton<SessionHelperClient>();
        services.TryAddSingleton<IRebootScheduler, Win32RebootScheduler>();
        services.TryAddSingleton<FileDeployer>();
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ScreenshotShareStore>();
        services.AddSingleton<DesktopInventory>();
        services.AddSingleton<IWindowInspectionSource, WindowsWindowInspectionSource>();
        services.AddSingleton<WindowInspector>();
        services.AddSingleton<IWindowEventSessionFactory, HelperWindowEventSessionFactory>();
        services.AddSingleton<WindowEventCaptureManager>();
        services.AddHostedService(provider => provider.GetRequiredService<WindowEventCaptureManager>());
        services.AddSingleton<WindowPlacementStore>();
        services.AddSingleton<IWindowPlacementSource, WindowsWindowPlacementSource>();
        services.AddSingleton<WindowPlacer>();
        services.AddSingleton<IDisplayLayoutClient, HelperDisplayLayoutClient>();
        services.TryAddSingleton<IVirtualDisplayBackend, UnavailableVirtualDisplayBackend>();
        services.AddSingleton<VirtualDisplayCoordinator>();
        services.AddHostedService(provider => provider.GetRequiredService<VirtualDisplayCoordinator>());
        services.AddSingleton<DisplayLayoutTransactionManager>();
        services.AddSingleton<HelperDesktopHostClient>();
        services.AddSingleton<IDesktopHostClient>(provider => provider.GetRequiredService<HelperDesktopHostClient>());
        services.TryAddSingleton<IOwnedProcessCleanup, NoOwnedProcessCleanup>();
        services.AddSingleton<ManagedDesktopSessionManager>();
        services.AddSingleton<IManagedDesktopDirectory>(provider => provider.GetRequiredService<ManagedDesktopSessionManager>());
        services.AddHostedService(provider => provider.GetRequiredService<ManagedDesktopSessionManager>());
        // Registered after the desktop manager: hosted services stop in reverse order, so an open display transaction
        // restores through a desktop manager that is still running.
        services.AddHostedService(provider => provider.GetRequiredService<DisplayLayoutTransactionManager>());
        services.AddSingleton<CaptureCache>();
        services.AddSingleton<PerceptionService>();
    }

    public override void MapEndpoints(WebApplication app, VestigoOptions options) =>
        ScreenshotShareEndpoint.Map(app, options);
}
