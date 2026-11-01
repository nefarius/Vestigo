namespace Vestigo.Audio;

/// <summary>One Core Audio playback or recording endpoint and its current volume, meter, and sessions.</summary>
public sealed record AudioEndpoint(
    string Id,
    string FriendlyName,
    string? DeviceDescription,
    string? InterfaceName,
    string Flow,
    string State,
    string? FormFactor,
    IReadOnlyList<string> DefaultFor,
    double? VolumePercent,
    bool? Muted,
    IReadOnlyList<double>? ChannelVolumesPercent,
    double? PeakPercent,
    bool Active,
    IReadOnlyList<AudioSession> Sessions);

/// <summary>One audio session on an endpoint, typically a process that is playing or recording.</summary>
public sealed record AudioSession(
    int ProcessId,
    string? ProcessName,
    string? DisplayName,
    string State,
    bool IsSystemSounds);

/// <summary>Listed endpoints plus the Windows session they were read from.</summary>
public sealed record AudioEndpointListResult(IReadOnlyList<AudioEndpoint> Endpoints, int SessionId);

/// <summary>Result of switching the default endpoint, including the refreshed listing used to confirm the change.</summary>
public sealed record SetDefaultAudioResult(
    string Id,
    IReadOnlyList<string> AppliedRoles,
    IReadOnlyList<AudioEndpoint> Endpoints);

/// <summary>Filters for listing endpoints. Values are the same strings the MCP tools accept.</summary>
public sealed record AudioSelector(
    string Flow,
    string State,
    string? Id,
    bool IncludeSessions,
    int MeterSampleMilliseconds)
{
    public const string FlowAll = "all";
    public const string FlowRender = "render";
    public const string FlowCapture = "capture";

    public const string StateActive = "active";
    public const string StateAll = "all";

    public const string RoleDefault = "default";
    public const string RoleCommunications = "communications";
    public const string RoleAll = "all";

    public static AudioSelector Defaults { get; } = new(
        FlowAll,
        StateActive,
        null,
        true,
        AudioOptions.DefaultMeterSampleMilliseconds);

    public static bool TryParseFlow(string? value, out string flow, out string? error)
    {
        flow = FlowAll;
        error = null;
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, FlowAll, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, FlowRender, StringComparison.OrdinalIgnoreCase))
        {
            flow = FlowRender;
            return true;
        }

        if (string.Equals(value, FlowCapture, StringComparison.OrdinalIgnoreCase))
        {
            flow = FlowCapture;
            return true;
        }

        error = "flow must be 'all', 'render' or 'capture'.";
        return false;
    }

    public static bool TryParseState(string? value, out string state, out string? error)
    {
        state = StateActive;
        error = null;
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, StateActive, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, StateAll, StringComparison.OrdinalIgnoreCase))
        {
            state = StateAll;
            return true;
        }

        error = "state must be 'active' or 'all'.";
        return false;
    }

    public static bool TryParseRole(string? value, out string role, out IReadOnlyList<string> appliedRoles, out string? error)
    {
        role = RoleDefault;
        appliedRoles = RolesFor(RoleDefault);
        error = null;
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, RoleDefault, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, RoleCommunications, StringComparison.OrdinalIgnoreCase))
        {
            role = RoleCommunications;
            appliedRoles = RolesFor(RoleCommunications);
            return true;
        }

        if (string.Equals(value, RoleAll, StringComparison.OrdinalIgnoreCase))
        {
            role = RoleAll;
            appliedRoles = RolesFor(RoleAll);
            return true;
        }

        error = "role must be 'default', 'communications' or 'all'.";
        return false;
    }

    /// <summary>
    ///     <c>default</c> is Windows' Default Device (console plus multimedia). <c>communications</c> is the
    ///     Default Communication Device. <c>all</c> sets both.
    /// </summary>
    public static IReadOnlyList<string> RolesFor(string role) =>
        role switch
        {
            RoleCommunications => ["communications"],
            RoleAll => ["console", "multimedia", "communications"],
            _ => ["console", "multimedia"]
        };
}
