using Nefarius.Utilities.ETW.Deserializer.WPP;

using Vestigo.Configuration;

namespace Vestigo.Etws;

/// <summary>
///     One realtime ETW session owned by a capture job.
/// </summary>
public interface IRealtimeTraceSession : IAsyncDisposable
{
    string SessionName { get; }

    void EnableProvider(Guid providerId, TraceLevelSelection level, ulong matchAnyKeyword, ulong matchAllKeyword);

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadEventsAsync(
        DecodingContext? context,
        bool rewriteProviderName,
        Action onMissingFormat,
        CancellationToken cancellationToken);
}

/// <summary>
///     Creates and queries realtime ETW sessions. Tests substitute a fake factory.
/// </summary>
public interface IRealtimeTraceSessionFactory
{
    IRealtimeTraceSession Create(string sessionName, VestigoOptions options);

    void StopOrphan(string sessionName);

    IReadOnlyList<string> ListSessions();
}

/// <summary>
///     ETW trace level, with the same numeric values as the native trace level.
/// </summary>
public enum TraceLevelSelection
{
    None = 0,
    Critical = 1,
    Error = 2,
    Warning = 3,
    Information = 4,
    Verbose = 5
}
