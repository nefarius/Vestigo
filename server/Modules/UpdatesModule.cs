using Vestigo.Configuration;
using Vestigo.Updates;

namespace Vestigo.Modules;

/// <summary>Checks the signed Buildbot feed and optionally launches a silent self-update.</summary>
public sealed class UpdatesModule : VestigoModule
{
    public override string Name => "Updates";

    public override string Description =>
        "Check the signed Buildbot feed for a newer Vestigo release and optionally run a silent self-update.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(UpdateTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.AddHttpClient(UpdateFeedClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Vestigo");
        });
        services.AddSingleton<IUpdateFeedClient, UpdateFeedClient>();
        services.AddSingleton<IInstalledVersionSource, InstalledVersionReader>();
        services.AddSingleton<ISelfUpdateRunner, SelfUpdateRunner>();
    }
}
