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
        bool dropped = samples.Any(sample => sample.Dropped);
        List<PresentationSample> correlated = samples.Where(sample => Correlates(sample, processId, windowHandle)).ToList();
        HashSet<string> modes = [];
        foreach (PresentationSample sample in correlated)
        {
            if (ModeOf(sample) is string mode)
            {
                modes.Add(mode);
            }
        }

        string? reason = Reason(providersEnabled, providersFailed, dropped || eventsLost, cancelled, truncated, undecodedEvents, samples, correlated, modes);
        return new PresentationObservation(
            reason is null ? modes.Single() : PresentationProviders.Unknown,
            reason,
            "etw",
            startedAt,
            endedAt,
            processId,
            windowHandle,
            correlated.Count,
            samples.Count + undecodedEvents,
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
        IReadOnlyList<PresentationSample> samples,
        IReadOnlyList<PresentationSample> correlated,
        HashSet<string> modes)
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

        if (samples.Count == 0)
        {
            return "no_events";
        }

        if (correlated.Count == 0)
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

    private static bool Correlates(PresentationSample sample, int processId, long? windowHandle)
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
