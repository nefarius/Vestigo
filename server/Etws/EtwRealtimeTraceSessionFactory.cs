using Nefarius.Utilities.ETW;
using Nefarius.Utilities.ETW.Deserializer.WPP;

using Vestigo.Configuration;

namespace Vestigo.Etws;

/// <summary>
///     Realtime session factory backed by <c>Nefarius.Utilities.ETW</c>.
/// </summary>
public sealed class EtwRealtimeTraceSessionFactory : IRealtimeTraceSessionFactory
{
    public IRealtimeTraceSession Create(string sessionName, VestigoOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Realtime ETW capture requires Windows.");
        }

        EtwClockResolution clock = Enum.TryParse(options.ClockResolution, ignoreCase: true, out EtwClockResolution parsed)
            ? parsed
            : throw new InvalidOperationException($"Unknown ETW clock resolution '{options.ClockResolution}'.");

        EtwRealtimeSession session = EtwRealtimeSession.Create(sessionName, configure =>
        {
            configure.BufferSizeKb = options.BufferSizeKb;
            configure.FlushTimerSeconds = options.FlushTimerSeconds;
            configure.ClockResolution = clock;
        });
        return new EtwRealtimeTraceSession(session);
    }

    public IRealtimeTraceSession Attach(string sessionName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Realtime ETW capture requires Windows.");
        }

        return new AttachedTraceSession(sessionName);
    }

    public void StopOrphan(string sessionName)
    {
        if (OperatingSystem.IsWindows() && sessionName.StartsWith(CaptureManager.SessionPrefix, StringComparison.Ordinal))
        {
            EtwUtil.StopOrphanSession(sessionName);
        }
    }

    public IReadOnlyList<string> ListSessions()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Listing ETW sessions requires Windows.");
        }

        return EtwUtil.EnumerateSessionNames();
    }

    private sealed class AttachedTraceSession(string sessionName) : IRealtimeTraceSession
    {
        public string SessionName { get; } = sessionName;

        public void EnableProvider(Guid providerId, TraceLevelSelection level, ulong matchAnyKeyword, ulong matchAllKeyword) =>
            throw new NotSupportedException("Attached sessions are read-only.");

        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadEventsAsync(
            DecodingContext? context,
            bool rewriteProviderName,
            Action onMissingFormat,
            CancellationToken cancellationToken) =>
            EtwUtil.EnumerateRealtimeEventsAsync(
                SessionName,
                configure =>
                {
                    configure.WppDecodingContext = context;
                    configure.RewriteWppProviderName = rewriteProviderName;
                    configure.OnWppFormatMissing = (_, _, _) => onMissingFormat();
                },
                cancellationToken);

        // The session belongs to someone else; cancelling the enumeration closes only our consumer.
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EtwRealtimeTraceSession(EtwRealtimeSession session) : IRealtimeTraceSession
    {
        public string SessionName => session.SessionName;

        public void EnableProvider(Guid providerId, TraceLevelSelection level, ulong matchAnyKeyword, ulong matchAllKeyword) =>
            session.EnableProvider(providerId, (TraceEventLevel)level, matchAnyKeyword, matchAllKeyword);

        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadEventsAsync(
            DecodingContext? context,
            bool rewriteProviderName,
            Action onMissingFormat,
            CancellationToken cancellationToken) =>
            EtwUtil.EnumerateRealtimeEventsAsync(
                session.SessionName,
                configure =>
                {
                    configure.WppDecodingContext = context;
                    configure.RewriteWppProviderName = rewriteProviderName;
                    configure.OnWppFormatMissing = (_, _, _) => onMissingFormat();
                },
                cancellationToken);

        public ValueTask DisposeAsync()
        {
            session.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
