using Vestigo.Configuration;
using Vestigo.LogFiles;

namespace Vestigo.Modules;

/// <summary>Tail, incremental reads and ripgrep search of text files under configured roots.</summary>
public sealed class LogsModule : VestigoModule
{
    public override string Name => "Logs";

    public override string Description =>
        "Tail and incremental reads of text log files with rotation detection, and server-side regex search through the bundled ripgrep.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(LogTools), typeof(SearchTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.AddSingleton<IRipgrepProcess, RipgrepProcess>();
        services.AddSingleton<RipgrepSearcher>();
    }
}
