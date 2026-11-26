namespace Vestigo.Etws;

/// <summary>
///     Classifies presentation mode only from explicit provider events. Window styles and
///     monitor-sized bounds are not inputs.
/// </summary>
public static class PresentationClassifier
{
    public static PresentationObservation Classify(
        IReadOnlyList<PresentationSample> samples,
        int processId,
        long? windowHandle,
        IReadOnlyList<string> providersEnabled,
        IReadOnlyList<string> providersFailed,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string? sessionName,
        bool cancelled,
        bool truncated,
        int undecodedEvents,
        bool eventsLost = false)
    {
        PresentationAccumulator accumulator = new(processId, windowHandle);
        foreach (PresentationSample sample in samples)
        {
            accumulator.Add(sample);
        }

        for (int i = 0; i < undecodedEvents; i++)
        {
            accumulator.AddUndecoded();
        }

        return accumulator.Build(
            providersEnabled,
            providersFailed,
            startedAt,
            endedAt,
            sessionName,
            cancelled,
            truncated,
            eventsLost);
    }

    internal static PresentationObservation Build(
        int processId,
        long? windowHandle,
        int decodedEvents,
        int correlatedEvents,
        int undecodedEvents,
        bool dropped,
        IReadOnlySet<string> modes,
        IReadOnlyList<string> providersEnabled,
        IReadOnlyList<string> providersFailed,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string? sessionName,
        bool cancelled,
        bool truncated,
        bool eventsLost)
    {
        string? reason = Reason(
            providersEnabled,
            providersFailed,
            dropped || eventsLost,
            cancelled,
            truncated,
            undecodedEvents,
            decodedEvents,
            correlatedEvents,
            modes);
        return new PresentationObservation(
            reason is null ? modes.Single() : PresentationProviders.Unknown,
            reason,
            "etw",
            startedAt,
            endedAt,
            processId,
            windowHandle,
            correlatedEvents,
            decodedEvents + undecodedEvents,
            providersEnabled,
            providersFailed,
            sessionName,
            null);
    }

    private static string? Reason(
        IReadOnlyList<string> providersEnabled,
        IReadOnlyList<string> providersFailed,
        bool dropped,
        bool cancelled,
        bool truncated,
        int undecodedEvents,
        int decodedEvents,
        int correlatedEvents,
        IReadOnlySet<string> modes)
    {
        if (cancelled)
        {
            return "cancelled";
        }

        if (providersEnabled.Count == 0 || providersFailed.Count > 0)
        {
            return "providers_unavailable";
        }

        if (dropped)
        {
            return "events_dropped";
        }

        if (truncated)
        {
            return "events_truncated";
        }

        if (undecodedEvents > 0)
        {
            return "decode_failed";
        }

        if (decodedEvents == 0)
        {
            return "no_events";
        }

        if (correlatedEvents == 0)
        {
            return "no_correlation";
        }

        if (modes.Count > 1)
        {
            return "ambiguous";
        }

        if (modes.Count == 0)
        {
            return "insufficient_evidence";
        }

        return null;
    }

    internal static bool Correlates(PresentationSample sample, int processId, long? windowHandle)
    {
        if (!IsSupported(sample.Provider))
        {
            return false;
        }

        int? pid = sample.AttributedProcessId ?? sample.ProcessId;
        if (pid != processId)
        {
            return false;
        }

        return windowHandle is null || sample.WindowHandle == windowHandle;
    }

    internal static string? ModeOf(PresentationSample sample)
    {
        if (sample.ExplicitMode is not null)
        {
            return sample.ExplicitMode;
        }

        if (IsProvider(sample.Provider, PresentationProviders.DxgKrnl, "DxgKrnl")
            && (sample.EventId == 266 || (sample.Task ?? "").StartsWith("IndependentFlip", StringComparison.OrdinalIgnoreCase)))
        {
            return PresentationProviders.IndependentFlip;
        }

        if (IsProvider(sample.Provider, PresentationProviders.DwmCore, "Dwm-Core")
            && string.Equals(sample.Task, "SCHEDULE_PRESENT", StringComparison.OrdinalIgnoreCase))
        {
            return PresentationProviders.Composed;
        }

        return null;
    }

    private static bool IsSupported(string? provider) =>
        IsProvider(provider, PresentationProviders.Dxgi, "DXGI")
        || IsProvider(provider, PresentationProviders.DxgKrnl, "DxgKrnl")
        || IsProvider(provider, PresentationProviders.DwmCore, "Dwm-Core");

    private static bool IsProvider(string? provider, Guid id, string name) =>
        provider is not null
        && (provider.Contains(name, StringComparison.OrdinalIgnoreCase)
            || (Guid.TryParse(provider.Trim('{', '}'), out Guid parsed) && parsed == id));
}

/// <summary>Streaming O(1)-memory accumulator for presentation ETW events.</summary>
internal sealed class PresentationAccumulator(int processId, long? windowHandle)
{
    private readonly HashSet<string> _modes = [];
    private int _decodedEvents;
    private int _correlatedEvents;
    private int _undecodedEvents;
    private bool _dropped;

    public int SeenEvents => _decodedEvents + _undecodedEvents;

    public void Add(PresentationSample sample)
    {
        _decodedEvents++;
        _dropped |= sample.Dropped;
        if (!PresentationClassifier.Correlates(sample, processId, windowHandle))
        {
            return;
        }

        _correlatedEvents++;
        if (PresentationClassifier.ModeOf(sample) is string mode)
        {
            _modes.Add(mode);
        }
    }

    public void AddUndecoded() => _undecodedEvents++;

    public PresentationObservation Build(
        IReadOnlyList<string> providersEnabled,
        IReadOnlyList<string> providersFailed,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string? sessionName,
        bool cancelled,
        bool truncated,
        bool eventsLost) =>
        PresentationClassifier.Build(
            processId,
            windowHandle,
            _decodedEvents,
            _correlatedEvents,
            _undecodedEvents,
            _dropped,
            _modes,
            providersEnabled,
            providersFailed,
            startedAt,
            endedAt,
            sessionName,
            cancelled,
            truncated,
            eventsLost);
}
