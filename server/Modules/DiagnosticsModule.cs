using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Vestigo.Configuration;
using Vestigo.Devices;
using Vestigo.Diagnostics;
using Vestigo.EventLogs;
using Vestigo.Processes;
using Vestigo.Security.Signatures;
using Vestigo.Services;
using Vestigo.SystemInfo;

namespace Vestigo.Modules;

/// <summary>Opt-in diagnostics. Process dumps are a job and need the Jobs module.</summary>
public sealed class DiagnosticsModule : VestigoModule
{
    public override string Name => "Diagnostics";

    public override string Description =>
        "Inspect PE files and signatures, list loaded modules, read SetupAPI install history, explain a device problem code, explain a driver load failure from cited evidence, list graphics adapters, list process connections, list media capabilities, list Windows Error Reporting report metadata, read Windows Firewall profiles and rules, read Driver Verifier state, and list kernel dump metadata. Opt-in. Needs Jobs. Capture probes, network probes, audible audio probes, process dumps, firewall inventory, and Driver Verifier changes are separate gates. A valid Authenticode signature is not permission to load a kernel driver.";

    /// <summary>Module lists, signature subjects, SetupAPI logs, and crash metadata are information disclosure, so the module is opt-in.</summary>
    public override bool EnabledByDefault => false;

    /// <summary><c>capture_process_dump</c> runs as a job, so the module requires Jobs.</summary>
    public override IReadOnlyList<string> Dependencies { get; } = ["Jobs"];

    public override IReadOnlyList<Type> ToolTypes { get; } =
        [typeof(BinaryTools), typeof(ProcessModuleTools), typeof(InstallHistoryTools), typeof(DeviceProblemTools), typeof(GraphicsTools), typeof(CaptureProbeTools), typeof(ConnectionTools), typeof(EndpointProbeTools), typeof(MediaTools), typeof(CrashTools), typeof(KernelDumpTools), typeof(FirewallTools), typeof(VerifierTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.TryAddSingleton<ISignatureVerifier, WinTrustSignatureVerifier>();
        services.TryAddSingleton<IPeInspector, PeInspector>();
        services.TryAddSingleton<IProcessModuleSource, WindowsProcessModuleSource>();
        services.TryAddSingleton<IDeviceControl, DeviceControl>();
        services.AddSingleton<ISetupApiLogSource, SetupApiLogReader>();
        services.AddSingleton<IGraphicsInventory, WindowsGraphicsInventory>();
        services.TryAddSingleton<IProcessLauncher, ProcessLauncher>();
        services.TryAddSingleton<SessionHelperClient>();
        services.AddSingleton<ICaptureProbe, SessionCaptureProbe>();
        services.AddSingleton<IProcessConnectionSource, WindowsProcessConnectionSource>();
        services.AddSingleton<IEndpointProbe, SocketEndpointProbe>();
        services.AddSingleton<IVideoEncoderSource, MediaFoundationVideoEncoders>();
        services.AddSingleton<ICameraFormatSource, MediaFoundationCameras>();
        services.AddSingleton<IAudioFormatProbe, SessionAudioFormatProbe>();
        services.AddSingleton<ICrashReportSource, WerCrashReportSource>();
        services.AddSingleton<IKernelDumpInventory, WindowsKernelDumpInventory>();
        services.AddSingleton<IFirewallInventory, WindowsFirewallInventory>();
        services.AddSingleton<IProcessDumpWriter, MiniDumpWriter>();
        services.AddSingleton<IVerifierQuery, WindowsVerifierQuery>();
        services.AddSingleton<VerifierStateService>();
        services.AddSingleton<IVerifierControl, WindowsVerifierControl>();
        services.AddSingleton<VerifierControlService>();
        services.AddSingleton<IVolumeSpace, DriveVolumeSpace>();
        services.AddSingleton<ICodeIntegrityProbe, WindowsCodeIntegrity>();
        services.AddSingleton<IDeviceProblemSource>(provider => new DeviceEvidenceGatherer(
            provider.GetRequiredService<IDeviceControl>(),
            provider.GetRequiredService<ISetupApiLogSource>(),
            options,
            provider.GetService<IServiceControl>(),
            provider.GetService<SystemService>(),
            provider.GetService<EventLogService>(),
            provider.GetService<ISignatureVerifier>(),
            provider.GetService<ICodeIntegrityProbe>()));
    }
}
