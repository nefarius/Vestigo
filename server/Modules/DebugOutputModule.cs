using Vestigo.Configuration;
using Vestigo.DebugOutput;

namespace Vestigo.Modules;

/// <summary>Win32 OutputDebugString capture through the bundled DebugViewConsole.</summary>
public sealed class DebugOutputModule : VestigoModule
{
    public override string Name => "DebugOutput";

    /// <summary>Off until an administrator opts in: the capture can read debug output from every session.</summary>
    public override bool EnabledByDefault => false;

    public override string Description =>
        "Capture, filter, and forward Win32 OutputDebugString messages through DebugViewConsole.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(DebugOutputTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.AddSingleton<IDebugViewProcess, DebugViewProcess>();
        services.AddSingleton<DebugOutputManager>();
        services.AddHostedService(provider => provider.GetRequiredService<DebugOutputManager>());
    }
}
