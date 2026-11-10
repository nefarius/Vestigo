using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.RemoteDesktop;

namespace Vestigo.SystemInfo;

/// <summary>Stable <c>reason</c> values when no console user is available. The error code stays unchanged.</summary>
public static class ConsoleSessionReasons
{
    public const string NobodyLoggedOn = "nobody_logged_on";

    public const string OnlyDisconnectedSessions = "only_disconnected_sessions";

    public const string Session0Only = "session_0_only";

    public const string ConsoleSessionHasNoToken = "console_session_has_no_token";

    /// <summary>Caller-facing message. The reason is a separate token.</summary>
    public const string Message = "No user is logged on at the console.";
}

/// <summary>Session id and connect state from the Terminal Services session table.</summary>
/// <param name="SessionId">WTS session id.</param>
/// <param name="State">Raw <c>WTS_CONNECTSTATE_CLASS</c> value.</param>
public readonly record struct SessionPresence(uint SessionId, int State);

/// <summary>
///     Opens the active console user's token and explains why it is missing.
///     Process launch treats session 0 as unusable. Environment and registry still try that session's token.
/// </summary>
public static class ConsoleSession
{
    public const uint NoSession = 0xFFFFFFFF;

    public const int ErrorNoToken = 1008;

    public const int ErrorAccessDenied = 5;

    /// <summary><c>WTSDisconnected</c>.</summary>
    public const int DisconnectedState = (int)WTS_CONNECTSTATE_CLASS.WTSDisconnected;

    /// <summary><c>WTSListen</c>.</summary>
    public const int ListenState = (int)WTS_CONNECTSTATE_CLASS.WTSListen;

    /// <summary>Active console session id. <see cref="NoSession" /> means Windows has no console session.</summary>
    internal static uint ActiveConsoleSessionId() => PInvoke.WTSGetActiveConsoleSessionId();

    /// <summary>Opens the session token. <paramref name="error" /> is the Win32 code when the result is null.</summary>
    internal static unsafe SafeFileHandle? TryOpenToken(uint sessionId, out int error)
    {
        error = 0;
        HANDLE raw = default;
        if (!PInvoke.WTSQueryUserToken(sessionId, &raw))
        {
            error = Marshal.GetLastWin32Error();
            return null;
        }

        return new SafeFileHandle(raw, ownsHandle: true);
    }

    /// <summary>Session id and state only. Null when the session table cannot be read.</summary>
    internal static unsafe IReadOnlyList<SessionPresence>? TryList()
    {
        WTS_SESSION_INFOW* info = null;
        uint count = 0;
        if (!PInvoke.WTSEnumerateSessions(default, 0, 1, &info, &count))
        {
            return null;
        }

        try
        {
            SessionPresence[] rows = new SessionPresence[count];
            for (uint index = 0; index < count; index++)
            {
                WTS_SESSION_INFOW row = info[index];
                rows[index] = new SessionPresence(row.SessionId, (int)row.State);
            }

            return rows;
        }
        finally
        {
            if (info != null)
            {
                PInvoke.WTSFreeMemory(info);
            }
        }
    }

    /// <summary>
    ///     Reason for a missing console user, or null when the failure is not a missing user.
    ///     Access denied and other token errors stay null so callers do not hide them behind a guessed reason.
    ///     A null <paramref name="sessions" /> means the table could not be read.
    /// </summary>
    public static string? Explain(uint sessionId, int? tokenError, IReadOnlyList<SessionPresence>? sessions, bool sessionZeroIsUnusable)
    {
        if (tokenError is int error && error != ErrorNoToken)
        {
            return null;
        }

        if (sessionId == NoSession)
        {
            return sessions is null ? ConsoleSessionReasons.NobodyLoggedOn : ExplainTable(sessions);
        }

        if (sessionId == 0 && sessionZeroIsUnusable)
        {
            return ConsoleSessionReasons.Session0Only;
        }

        return tokenError == ErrorNoToken ? ConsoleSessionReasons.ConsoleSessionHasNoToken : null;
    }

    /// <summary>Classifies a session table when the console session id is absent.</summary>
    public static string ExplainTable(IReadOnlyList<SessionPresence> sessions)
    {
        bool sawUserSession = false;
        bool allDisconnected = true;
        foreach (SessionPresence row in sessions)
        {
            if (row.SessionId == 0 || row.State == ListenState)
            {
                continue;
            }

            sawUserSession = true;
            if (row.State != DisconnectedState)
            {
                allDisconnected = false;
            }
        }

        if (!sawUserSession)
        {
            return ConsoleSessionReasons.Session0Only;
        }

        return allDisconnected
            ? ConsoleSessionReasons.OnlyDisconnectedSessions
            : ConsoleSessionReasons.NobodyLoggedOn;
    }
}
