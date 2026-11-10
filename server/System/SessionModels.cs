namespace Vestigo.SystemInfo;

/// <summary>One logon session returned by <c>list_logon_sessions</c>.</summary>
/// <param name="SessionId">WTS session id.</param>
/// <param name="StationName">Window station name, such as <c>Console</c> or <c>RDP-Tcp#3</c>. Displayed, not interpreted.</param>
/// <param name="State"><c>active</c>, <c>connected</c>, <c>connectQuery</c>, <c>shadow</c>, <c>disconnected</c>, <c>idle</c>, <c>listen</c>, <c>reset</c>, <c>down</c>, <c>init</c>, or <c>unknown</c>.</param>
/// <param name="UserName">Domain and user, or null when the session has no user.</param>
/// <param name="LogonTimeUtc">Logon time in UTC, or null when Windows did not report one.</param>
/// <param name="ClientName">Client machine name. Null unless <c>includeClientAddresses</c> is true.</param>
/// <param name="ClientAddress">Client address. Null unless <c>includeClientAddresses</c> is true.</param>
/// <param name="IsConsole">True when this session is <c>WTSGetActiveConsoleSessionId</c>.</param>
/// <param name="IsCurrentInput">True when interactive helpers would attach to this session.</param>
/// <param name="WinStationName">Window station name reported for the session.</param>
/// <param name="Protocol"><c>console</c>, <c>ica</c>, <c>rdp</c>, or <c>unknown</c>.</param>
public sealed record LogonSession(
    uint SessionId,
    string StationName,
    string State,
    string? UserName,
    DateTimeOffset? LogonTimeUtc,
    string? ClientName,
    string? ClientAddress,
    bool IsConsole,
    bool IsCurrentInput,
    string WinStationName,
    string Protocol);

/// <summary>Result of <c>list_logon_sessions</c>.</summary>
/// <param name="Sessions">Sessions that matched the include flags.</param>
/// <param name="Total">Number of sessions returned.</param>
public sealed record LogonSessionList(IReadOnlyList<LogonSession> Sessions, int Total);

/// <summary>One logon session returned by <c>get_logon_session</c>, including token facts when the token could be opened.</summary>
/// <param name="SessionId">WTS session id.</param>
/// <param name="StationName">Window station name. Displayed, not interpreted.</param>
/// <param name="State">Connection state name.</param>
/// <param name="UserName">Domain and user, or null.</param>
/// <param name="LogonTimeUtc">Logon time in UTC, or null.</param>
/// <param name="ClientName">Client machine name. Null unless <c>includeClientAddresses</c> is true.</param>
/// <param name="ClientAddress">Client address. Null unless <c>includeClientAddresses</c> is true.</param>
/// <param name="IsConsole">True when this session is the active console session.</param>
/// <param name="IsCurrentInput">True when interactive helpers would attach to this session.</param>
/// <param name="WinStationName">Window station name reported for the session.</param>
/// <param name="Protocol"><c>console</c>, <c>ica</c>, <c>rdp</c>, or <c>unknown</c>.</param>
/// <param name="Elevation"><c>default</c>, <c>full</c>, <c>limited</c>, or <c>unknown</c>. Null when the token was not opened or the elevation type was not read.</param>
/// <param name="IsAdministrator">True when the token is in the local Administrators group. Null when that check did not run.</param>
/// <param name="LogonId">Authentication id, or null when the token statistics were not read.</param>
public sealed record LogonSessionDetail(
    uint SessionId,
    string StationName,
    string State,
    string? UserName,
    DateTimeOffset? LogonTimeUtc,
    string? ClientName,
    string? ClientAddress,
    bool IsConsole,
    bool IsCurrentInput,
    string WinStationName,
    string Protocol,
    string? Elevation,
    bool? IsAdministrator,
    string? LogonId);
