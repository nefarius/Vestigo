namespace Vestigo.SystemInfo;

/// <summary>One session returned by Windows Terminal Services before name mapping and redaction.</summary>
/// <param name="SessionId">WTS session id.</param>
/// <param name="StationName">Window station name from the session table. Displayed, not interpreted.</param>
/// <param name="State">Raw <c>WTS_CONNECTSTATE_CLASS</c> value.</param>
/// <param name="UserName">Domain and user, or null when nobody is logged on in that session.</param>
/// <param name="LogonTimeUtc">Logon time in UTC, or null when Windows did not report one.</param>
/// <param name="ClientName">Client machine name, when Windows reported one.</param>
/// <param name="ClientAddress">Client address, when Windows reported one.</param>
/// <param name="Protocol">Raw <c>WTSClientProtocolType</c>, or null when it was not reported.</param>
/// <param name="WinStationName">Window station name from <c>WTSWinStationName</c>, or the table name.</param>
public sealed record RawLogonSession(
    uint SessionId,
    string StationName,
    int State,
    string? UserName,
    DateTimeOffset? LogonTimeUtc,
    string? ClientName,
    string? ClientAddress,
    int? Protocol,
    string? WinStationName);

/// <summary>Privileged token facts for one session. Null fields were not available.</summary>
/// <param name="Opened">False when the session token could not be opened.</param>
/// <param name="Elevation"><c>default</c>, <c>full</c>, <c>limited</c>, <c>unknown</c>, or null.</param>
/// <param name="IsAdministrator">True when the token is a member of the local Administrators group.</param>
/// <param name="LogonId">Authentication id, or null when the token statistics could not be read.</param>
public sealed record SessionTokenRead(bool Opened, string? Elevation, bool? IsAdministrator, string? LogonId);

/// <summary>Session table plus the console session the interactive helpers target.</summary>
/// <param name="Success">False when the session table could not be read.</param>
/// <param name="ErrorCode">Stable code when <paramref name="Success" /> is false.</param>
/// <param name="Error">Caller-facing message when <paramref name="Success" /> is false.</param>
/// <param name="ConsoleSessionId">Active console session, or null when Windows reports none.</param>
/// <param name="ConsoleHasToken">True when that console session has a user token.</param>
/// <param name="Sessions">Sessions Windows returned. The tool applies include filters.</param>
public sealed record SessionSnapshot(
    bool Success,
    string? ErrorCode,
    string? Error,
    uint? ConsoleSessionId,
    bool ConsoleHasToken,
    IReadOnlyList<RawLogonSession> Sessions)
{
    public static SessionSnapshot Ok(uint? consoleSessionId, bool consoleHasToken, IReadOnlyList<RawLogonSession> sessions) =>
        new(true, null, null, consoleSessionId, consoleHasToken, sessions);

    public static SessionSnapshot Fail(string code, string error) =>
        new(false, code, error, null, false, []);
}

/// <summary>Reads Windows logon sessions. Tests supply a fake.</summary>
public interface ISessionPlatform
{
    /// <summary>Reads the session table and which session is the active console.</summary>
    SessionSnapshot Read();

    /// <summary>Reads token elevation, administrator membership, and the logon id for one session.</summary>
    SessionTokenRead ReadToken(uint sessionId);
}
