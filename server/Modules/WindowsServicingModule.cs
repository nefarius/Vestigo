using Vestigo.Configuration;
using Vestigo.WindowsServicing;

namespace Vestigo.Modules;

/// <summary>Opt-in, read-only Windows Update and CBS diagnostics, separate from Vestigo self-updates.</summary>
public sealed class WindowsServicingModule : VestigoModule
{
    public override string Name => "WindowsServicing";
    public override string Description => "Read-only Windows Update, reboot queues, CBS packages, servicing logs, storage and file-locker diagnostics. Opt-in; needs Jobs.";
    public override bool EnabledByDefault => false;
    public override IReadOnlyList<string> Dependencies { get; } = ["Jobs"];
    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(WindowsServicingTools), typeof(UpdateDiagnosticsTools), typeof(FileLockerTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.AddSingleton<IServicingRegistry, ServicingRegistry>();
        services.AddSingleton<RebootPendingService>();
        services.AddSingleton<IWindowsUpdateAgent, WindowsUpdateAgent>();
        services.AddSingleton<IServicingPackageSource, ServicingPackageSource>();
        services.AddSingleton<NativeDiagnosticWorker>();
        services.AddSingleton<IFileLockerSource, RestartManagerSource>();
    }
}
