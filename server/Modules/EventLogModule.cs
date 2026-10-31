using Vestigo.Configuration;
using Vestigo.EventLogs;

namespace Vestigo.Modules;

/// <summary>Listing and querying Windows event logs and .evtx files.</summary>
public sealed class EventLogModule : VestigoModule
{
    public override string Name => "EventLog";

    public override string Description => "List and query Windows event log channels and .evtx files.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(EventLogTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options) =>
        services.AddSingleton<EventLogService>();
}
