using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.RemoteDesktop;

namespace Vestigo.SystemInfo;

/// <summary>Logon sessions through Windows Terminal Services. Client details are returned to the tool, which decides whether to publish them.</summary>
public sealed class WindowsSessionPlatform : ISessionPlatform
{
    private const uint NoSession = 0xFFFFFFFF;
    private const int ErrorAccessDenied = 5;

    /// <inheritdoc />
    public SessionSnapshot Read()
    {
        uint console = PInvoke.WTSGetActiveConsoleSessionId();
        uint? consoleId = console == NoSession ? null : console;
        bool consoleHasToken = consoleId is uint id && HasUserToken(id);
        if (!TryEnumerate(out List<RawLogonSession>? sessions, out string? code, out string? error))
        {
            return SessionSnapshot.Fail(code!, error!);
        }

        return SessionSnapshot.Ok(consoleId, consoleHasToken, sessions!);
    }

    /// <inheritdoc />
    public unsafe SessionTokenRead ReadToken(uint sessionId)
    {
        HANDLE raw = default;
        if (!PInvoke.WTSQueryUserToken(sessionId, &raw))
        {
            return new SessionTokenRead(false, null, null, null);
        }

        using SafeFileHandle token = new(raw, ownsHandle: true);
        return new SessionTokenRead(true, ReadElevation(token), ReadAdministrator(token), ReadLogonId(token));
    }

    private static bool HasUserToken(uint sessionId)
    {
        unsafe
        {
            HANDLE raw = default;
            if (!PInvoke.WTSQueryUserToken(sessionId, &raw))
            {
                return false;
            }

            new SafeFileHandle(raw, ownsHandle: true).Dispose();
            return true;
        }
    }

    private static unsafe bool TryEnumerate(out List<RawLogonSession>? sessions, out string? code, out string? error)
    {
        sessions = null;
        code = null;
        error = null;
        WTS_SESSION_INFOW* info = null;
        uint count = 0;
        if (!PInvoke.WTSEnumerateSessions(default, 0, 1, &info, &count))
        {
            (code, error) = Win32Failure();
            return false;
        }

        try
        {
            sessions = [];
            for (uint index = 0; index < count; index++)
            {
                WTS_SESSION_INFOW row = info[index];
                string station = row.pWinStationName.ToString();
                if (string.IsNullOrWhiteSpace(station))
                {
                    station = string.Empty;
                }

                sessions.Add(ReadSession(row.SessionId, station, (int)row.State));
            }

            return true;
        }
        finally
        {
            if (info != null)
            {
                PInvoke.WTSFreeMemory(info);
            }
        }
    }

    private static RawLogonSession ReadSession(uint sessionId, string station, int state)
    {
        string? winStation = QueryString(sessionId, WTS_INFO_CLASS.WTSWinStationName) ?? station;
        return new RawLogonSession(
            sessionId,
            station,
            state,
            SessionMaps.User(QueryString(sessionId, WTS_INFO_CLASS.WTSDomainName), QueryString(sessionId, WTS_INFO_CLASS.WTSUserName)),
            QueryLogonTime(sessionId),
            QueryString(sessionId, WTS_INFO_CLASS.WTSClientName),
            QueryAddress(sessionId),
            QueryProtocol(sessionId),
            winStation);
    }

    private static unsafe string? QueryString(uint sessionId, WTS_INFO_CLASS kind)
    {
        if (!TryQuery(sessionId, kind, out byte* buffer, out uint bytes) || buffer == null || bytes < 2)
        {
            return null;
        }

        try
        {
            string value = new PWSTR((char*)buffer).ToString().TrimEnd('\0').Trim();
            return value.Length == 0 ? null : value;
        }
        finally
        {
            PInvoke.WTSFreeMemory(buffer);
        }
    }

    private static unsafe DateTimeOffset? QueryLogonTime(uint sessionId)
    {
        if (!TryQuery(sessionId, WTS_INFO_CLASS.WTSLogonTime, out byte* buffer, out uint bytes) || buffer == null || bytes < 8)
        {
            return null;
        }

        try
        {
            uint* words = (uint*)buffer;
            long ticks = ((long)words[1] << 32) | words[0];
            if (ticks == 0)
            {
                return null;
            }

            try
            {
                return new DateTimeOffset(DateTime.FromFileTimeUtc(ticks));
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
        finally
        {
            PInvoke.WTSFreeMemory(buffer);
        }
    }

    private static unsafe int? QueryProtocol(uint sessionId)
    {
        if (!TryQuery(sessionId, WTS_INFO_CLASS.WTSClientProtocolType, out byte* buffer, out uint bytes) || buffer == null || bytes < 2)
        {
            return null;
        }

        try
        {
            return *(ushort*)buffer;
        }
        finally
        {
            PInvoke.WTSFreeMemory(buffer);
        }
    }

    private static unsafe string? QueryAddress(uint sessionId)
    {
        if (!TryQuery(sessionId, WTS_INFO_CLASS.WTSClientAddress, out byte* buffer, out uint bytes) || buffer == null)
        {
            return null;
        }

        try
        {
            if (bytes < (uint)sizeof(WTS_CLIENT_ADDRESS))
            {
                return null;
            }

            WTS_CLIENT_ADDRESS value = *(WTS_CLIENT_ADDRESS*)buffer;
            ReadOnlySpan<byte> raw = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<WTS_CLIENT_ADDRESS, byte>(ref value),
                sizeof(WTS_CLIENT_ADDRESS));
            return raw.Length < 24 ? null : SessionMaps.ClientAddress(value.AddressFamily, raw.Slice(4, 20));
        }
        finally
        {
            PInvoke.WTSFreeMemory(buffer);
        }
    }

    private static unsafe bool TryQuery(uint sessionId, WTS_INFO_CLASS kind, out byte* buffer, out uint bytes)
    {
        buffer = null;
        bytes = 0;
        if (!PInvoke.WTSQuerySessionInformation(default, sessionId, kind, out PWSTR local, out uint localBytes) || local.Value == null)
        {
            if (local.Value != null)
            {
                PInvoke.WTSFreeMemory(local.Value);
            }

            return false;
        }

        buffer = (byte*)local.Value;
        bytes = localBytes;
        return true;
    }

    private static unsafe string? ReadElevation(SafeFileHandle token)
    {
        int type = 0;
        if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevationType, &type, sizeof(int), out _))
        {
            return null;
        }

        return SessionMaps.Elevation(type);
    }

    private static unsafe bool? ReadAdministrator(SafeFileHandle token)
    {
        uint size = 68;
        byte* sid = stackalloc byte[68];
        PSID administrators = (PSID)(void*)sid;
        if (!PInvoke.CreateWellKnownSid(WELL_KNOWN_SID_TYPE.WinBuiltinAdministratorsSid, default, administrators, ref size))
        {
            return null;
        }

        if (!PInvoke.CheckTokenMembership(token, administrators, out BOOL member))
        {
            return null;
        }

        return member;
    }

    private static unsafe string? ReadLogonId(SafeFileHandle token)
    {
        TOKEN_STATISTICS stats = default;
        if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenStatistics, &stats, (uint)sizeof(TOKEN_STATISTICS), out _))
        {
            return null;
        }

        return SessionMaps.LogonId(stats.AuthenticationId.HighPart, stats.AuthenticationId.LowPart);
    }

    private static (string Code, string Error) Win32Failure()
    {
        int error = Marshal.GetLastWin32Error();
        return error == ErrorAccessDenied
            ? ("access_denied", "Windows denied access to logon sessions.")
            : ("wts_unavailable", "Windows Terminal Services did not return logon sessions.");
    }
}
