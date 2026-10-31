using Vestigo.Configuration;
using Vestigo.Etws;
using Vestigo.Tools;

namespace Vestigo.Modules;

/// <summary>Offline WPP conversion and managed realtime ETW capture.</summary>
public sealed class TraceModule : VestigoModule
{
    public override string Name => "Trace";

    public override string Description => "Offline ETL/WPP conversion and managed realtime capture.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(TraceTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.AddHttpClient("symbols");
        services.AddSingleton<SymbolCatalogService>();
        services.AddSingleton<OfflineConversionService>();
        services.AddSingleton<IRealtimeTraceSessionFactory, EtwRealtimeTraceSessionFactory>();
        services.AddSingleton<CaptureManager>();
        services.AddHostedService(provider => provider.GetRequiredService<CaptureManager>());
    }
}
