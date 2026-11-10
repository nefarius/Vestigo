using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace Vestigo.SystemInfo;

/// <summary>MCP tools that list Windows logon sessions. Client names and addresses are not written to the log.</summary>
[McpServerToolType]
public sealed class SessionTools(ISessionPlatform platform, ILogger<SessionTools> logger)
{
    [McpServerTool(Name = "list_logon_sessions", ReadOnly = true, Destructive = false)]
    [Description("Lists Windows logon sessions. isCurrentInput marks the session interactive tools would use. includeClientAddresses adds the client name and address; otherwise those fields are null and are not written to the log. Station names are displayed, not interpreted. There is no logoff or connect tool.")]
    public OperationResult<LogonSessionList> ListLogonSessions(
        [Description("Include disconnected sessions. Default true.")] bool includeDisconnected = true,
        [Description("Include listener rows such as RDP-Tcp. Default false.")] bool includeListeners = false,
        [Description("Include client name and address. Default false. These values are not written to the log.")] bool includeClientAddresses = false)
    {
        SessionSnapshot snapshot = platform.Read();
        if (!snapshot.Success)
        {
            return FailList(snapshot.ErrorCode!, snapshot.Error!);
        }

        List<LogonSession> sessions = [];
        foreach (RawLogonSession session in snapshot.Sessions)
        {
            if (!includeDisconnected && SessionMaps.State(session.State) == "disconnected")
            {
                continue;
            }

            if (!includeListeners && SessionMaps.State(session.State) == "listen")
            {
                continue;
            }

            sessions.Add(Project(session, snapshot, includeClientAddresses));
        }

        logger.LogInformation(
            "list_logon_sessions count {Count} clientAddresses {ClientAddresses}",
            sessions.Count,
            includeClientAddresses);
        return OperationResult<LogonSessionList>.Ok(new LogonSessionList(sessions, sessions.Count));
    }

    [McpServerTool(Name = "get_logon_session", ReadOnly = true, Destructive = false)]
    [Description("Returns one Windows logon session, including token elevation, local Administrators membership, and the logon id when the session token can be opened. Those token fields are null when the token cannot be read. includeClientAddresses adds the client name and address; otherwise those fields are null and are not written to the log.")]
    public OperationResult<LogonSessionDetail> GetLogonSession(
        [Description("WTS session id from list_logon_sessions.")] int sessionId,
        [Description("Include client name and address. Default false. These values are not written to the log.")] bool includeClientAddresses = false)
    {
        if (sessionId < 0)
        {
            return FailDetail("invalid_request", "sessionId must not be negative.");
        }

        SessionSnapshot snapshot = platform.Read();
        if (!snapshot.Success)
        {
            return FailDetail(snapshot.ErrorCode!, snapshot.Error!);
        }

        RawLogonSession? session = snapshot.Sessions.FirstOrDefault(item => item.SessionId == (uint)sessionId);
        if (session is null)
        {
            return FailDetail("session_not_found", "Logon session was not found.");
        }

        LogonSession projected = Project(session, snapshot, includeClientAddresses);
        SessionTokenRead token = platform.ReadToken(session.SessionId);
        logger.LogInformation(
            "get_logon_session session {SessionId} token {TokenOpened} clientAddresses {ClientAddresses}",
            session.SessionId,
            token.Opened,
            includeClientAddresses);
        return OperationResult<LogonSessionDetail>.Ok(new LogonSessionDetail(
            projected.SessionId,
            projected.StationName,
            projected.State,
            projected.UserName,
            projected.LogonTimeUtc,
            projected.ClientName,
            projected.ClientAddress,
            projected.IsConsole,
            projected.IsCurrentInput,
            projected.WinStationName,
            projected.Protocol,
            token.Opened ? token.Elevation : null,
            token.Opened ? token.IsAdministrator : null,
            token.Opened ? token.LogonId : null));
    }

    private static LogonSession Project(RawLogonSession session, SessionSnapshot snapshot, bool includeClientAddresses)
    {
        bool isConsole = snapshot.ConsoleSessionId == session.SessionId;
        return new LogonSession(
            session.SessionId,
            session.StationName,
            SessionMaps.State(session.State),
            session.UserName,
            session.LogonTimeUtc,
            includeClientAddresses ? session.ClientName : null,
            includeClientAddresses ? session.ClientAddress : null,
            isConsole,
            isConsole && snapshot.ConsoleHasToken,
            string.IsNullOrWhiteSpace(session.WinStationName) ? session.StationName : session.WinStationName,
            SessionMaps.Protocol(session.Protocol));
    }

    private OperationResult<LogonSessionList> FailList(string code, string error)
    {
        logger.LogInformation("list_logon_sessions failed {Code}", code);
        return OperationResult<LogonSessionList>.Fail(code, error);
    }

    private OperationResult<LogonSessionDetail> FailDetail(string code, string error)
    {
        logger.LogInformation("get_logon_session failed {Code}", code);
        return OperationResult<LogonSessionDetail>.Fail(code, error);
    }
}
