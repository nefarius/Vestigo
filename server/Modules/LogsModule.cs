using Vestigo.LogFiles;

namespace Vestigo.Modules;

/// <summary>Tail and incremental reads of text files under configured roots.</summary>
public sealed class LogsModule : VestigoModule
{
    public override string Name => "Logs";

    public override string Description => "Tail and incremental reads of text log files with rotation detection.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(LogTools)];
}
