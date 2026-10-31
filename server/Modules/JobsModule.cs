using Vestigo.Configuration;

namespace Vestigo.Modules;

/// <summary>Background job registry shared by long-running tools.</summary>
public sealed class JobsModule : VestigoModule
{
    public override string Name => "Jobs";

    public override string Description => "Background jobs with paged output, wait, and cancel.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(Jobs.JobTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.AddSingleton<Jobs.JobManager>();
        services.AddHostedService(provider => provider.GetRequiredService<Jobs.JobManager>());
    }
}
