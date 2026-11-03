using Microsoft.Extensions.DependencyInjection.Extensions;

using Vestigo.Configuration;
using Vestigo.Diagnostics;
using Vestigo.Security.Signatures;

namespace Vestigo.Modules;

/// <summary>Read-only binary, module, and later device and graphics diagnostics. Opt-in.</summary>
public sealed class DiagnosticsModule : VestigoModule
{
    public override string Name => "Diagnostics";

    public override string Description =>
        "Inspect PE files and signatures, and list loaded modules. Opt-in. A valid Authenticode signature is not permission to load a kernel driver.";

    /// <summary>Module lists and signature subjects are information disclosure, so the module is opt-in.</summary>
    public override bool EnabledByDefault => false;

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(BinaryTools), typeof(ProcessModuleTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.TryAddSingleton<ISignatureVerifier, WinTrustSignatureVerifier>();
        services.TryAddSingleton<IPeInspector, PeInspector>();
        services.TryAddSingleton<IProcessModuleSource, WindowsProcessModuleSource>();
    }
}
