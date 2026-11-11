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

    /// <summary>True when the session has reported lost events or lost buffers. Read this before disposal.</summary>
    bool EventsWereLost { get; }

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

    /// <summary>
    ///     Opens a consumer-only view of an existing session. Disposing it never stops the session.
    /// </summary>
    IRealtimeTraceSession Attach(string sessionName);

    void StopOrphan(string sessionName);

    IReadOnlyList<string> ListSessions();
}

/// <summary>
///     ETW trace level, with the same numeric values as the native trace level.
/// </summary>
public enum TraceLevelSelection
{
    /// <summary>No events.</summary>
    None = 0,

    /// <summary>Critical events.</summary>
    Critical = 1,

    /// <summary>Error events.</summary>
    Error = 2,

    /// <summary>Warning events.</summary>
    Warning = 3,

    /// <summary>Informational events.</summary>
    Information = 4,

    /// <summary>Verbose events.</summary>
    Verbose = 5
}
