using Vestigo.Configuration;
using Vestigo.ScheduledTasks;

namespace Vestigo.Modules;

/// <summary>Lists, inspects, and optionally runs, stops, toggles, and deletes scheduled tasks.</summary>
public sealed class ScheduledTasksModule : VestigoModule
{
    public override string Name => "ScheduledTasks";

    public override string Description => "List, inspect, run, and delete Windows scheduled tasks.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(ScheduledTaskTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.AddSingleton<IScheduledTaskService, ComScheduledTaskService>();
        services.AddSingleton<ScheduledTaskHistoryReader>();
    }
}
