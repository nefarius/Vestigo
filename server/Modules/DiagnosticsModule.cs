using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Vestigo.Configuration;
using Vestigo.Devices;
using Vestigo.Diagnostics;
using Vestigo.EventLogs;
using Vestigo.Security.Signatures;
using Vestigo.Services;
using Vestigo.SystemInfo;

namespace Vestigo.Modules;

/// <summary>Read-only binary, module, and later device and graphics diagnostics. Opt-in.</summary>
public sealed class DiagnosticsModule : VestigoModule
{
    public override string Name => "Diagnostics";

    public override string Description =>
        "Inspect PE files and signatures, list loaded modules, read SetupAPI install history, and explain a device problem code. Opt-in. A valid Authenticode signature is not permission to load a kernel driver.";

    /// <summary>Module lists, signature subjects, and SetupAPI logs are information disclosure, so the module is opt-in.</summary>
    public override bool EnabledByDefault => false;

    public override IReadOnlyList<Type> ToolTypes { get; } =
        [typeof(BinaryTools), typeof(ProcessModuleTools), typeof(InstallHistoryTools), typeof(DeviceProblemTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.TryAddSingleton<ISignatureVerifier, WinTrustSignatureVerifier>();
        services.TryAddSingleton<IPeInspector, PeInspector>();
        services.TryAddSingleton<IProcessModuleSource, WindowsProcessModuleSource>();
        services.TryAddSingleton<IDeviceControl, DeviceControl>();
        services.AddSingleton<ISetupApiLogSource, SetupApiLogReader>();
        services.AddSingleton<IDeviceProblemSource>(provider => new DeviceEvidenceGatherer(
            provider.GetRequiredService<IDeviceControl>(),
            provider.GetRequiredService<ISetupApiLogSource>(),
            options,
            provider.GetService<IServiceControl>(),
            provider.GetService<SystemService>(),
            provider.GetService<EventLogService>()));
    }
}
