using Microsoft.Extensions.DependencyInjection.Extensions;

using Vestigo.Configuration;
using Vestigo.Etws;
using Vestigo.Tools;

namespace Vestigo.Modules;

/// <summary>Offline WPP conversion and managed realtime ETW capture.</summary>
public sealed class TraceModule : VestigoModule
{
    public override string Name => "Trace";

    public override string Description => "Offline ETL/WPP conversion, managed realtime capture, and bounded presentation observation.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(TraceTools), typeof(PresentationTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.AddHttpClient("symbols")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler(provider => new SymbolServerAuthHandler(provider.GetRequiredService<VestigoOptions>()));
        services.AddSingleton<SymbolCatalogService>();
        services.AddSingleton<OfflineConversionService>();
        services.AddSingleton<TraceArchiveExtractor>();
        services.AddSingleton<IRealtimeTraceSessionFactory, EtwRealtimeTraceSessionFactory>();
        services.AddSingleton<CaptureManager>();
        services.AddHostedService(provider => provider.GetRequiredService<CaptureManager>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PresentationObserver>();
        services.AddHostedService(provider => provider.GetRequiredService<PresentationObserver>());
    }
}
