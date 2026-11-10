using System.Text.Json;

using Microsoft.Extensions.Logging;

using Vestigo.SystemInfo;

namespace Vestigo.Server.Tests;

public class LogonSessionTests
{
    [Theory]
    [InlineData(0, "active")]
    [InlineData(1, "connected")]
    [InlineData(2, "connectQuery")]
    [InlineData(3, "shadow")]
    [InlineData(4, "disconnected")]
    [InlineData(5, "idle")]
    [InlineData(6, "listen")]
    [InlineData(7, "reset")]
    [InlineData(8, "down")]
    [InlineData(9, "init")]
    [InlineData(42, "unknown")]
    public void State_MapsEveryConnectStateAndUnknown(int state, string name) =>
        Assert.Equal(name, SessionMaps.State(state));

    [Theory]
    [InlineData(0, "console")]
    [InlineData(1, "ica")]
    [InlineData(2, "rdp")]
    [InlineData(9, "unknown")]
    public void Protocol_UsesTheWindowsProtocolNames(int protocol, string name) =>
        Assert.Equal(name, SessionMaps.Protocol(protocol));

    [Fact]
    public void ProtocolAndElevation_LeaveMissingValuesUnset()
    {
        Assert.Equal("unknown", SessionMaps.Protocol(null));
        Assert.Null(SessionMaps.Elevation(null));
        Assert.Equal("default", SessionMaps.Elevation(1));
        Assert.Equal("full", SessionMaps.Elevation(2));
        Assert.Equal("limited", SessionMaps.Elevation(3));
        Assert.Equal("unknown", SessionMaps.Elevation(9));
        Assert.Null(SessionMaps.User("LAB", null));
        Assert.Equal("LAB\\user", SessionMaps.User("LAB", "user"));
    }

    [Fact]
    public void ClientAddress_ReadsIpv4AndIpv6AfterTheFamilyPadding()
    {
        byte[] ipv4 = new byte[20];
        ipv4[2] = 203;
        ipv4[3] = 0;
        ipv4[4] = 113;
        ipv4[5] = 10;
        Assert.Equal("203.0.113.10", SessionMaps.ClientAddress(2, ipv4));
        Assert.Null(SessionMaps.ClientAddress(0, ipv4));

        byte[] ipv6 = new byte[20];
        ipv6[2] = 0x20;
        ipv6[3] = 0x01;
        ipv6[17] = 0x01;
        Assert.Equal("2001::1", SessionMaps.ClientAddress(23, ipv6));
    }

    [Fact]
    public void List_MarksNobodyLoggedOnAsNeitherConsoleNorCurrentInput()
    {
        RecordingLogger logger = new();
        SessionTools tools = Tools(logger, console: null, hasToken: false,
            Session(1, "RDP-Tcp#3", state: 4, user: "LAB\\user", address: "203.0.113.10", client: "LAB-PC"));

        LogonSessionList listed = tools.ListLogonSessions().Value!;

        Assert.Single(listed.Sessions);
        Assert.False(listed.Sessions[0].IsConsole);
        Assert.False(listed.Sessions[0].IsCurrentInput);
        Assert.Equal("disconnected", listed.Sessions[0].State);
        Assert.Null(listed.Sessions[0].ClientAddress);
        Assert.Null(listed.Sessions[0].ClientName);
        Assert.DoesNotContain("203.0.113.10", JsonSerializer.Serialize(listed), StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.10", string.Join('\n', logger.Lines), StringComparison.Ordinal);
        Assert.DoesNotContain("LAB-PC", string.Join('\n', logger.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void List_CurrentInputRequiresTheConsoleToken()
    {
        SessionTools withoutToken = Tools(new RecordingLogger(), console: 1, hasToken: false,
            Session(1, "Console", state: 0, user: "LAB\\user"),
            Session(2, "RDP-Tcp#4", state: 4, user: "LAB\\other"));
        LogonSession console = withoutToken.ListLogonSessions().Value!.Sessions.Single(item => item.SessionId == 1);
        Assert.True(console.IsConsole);
        Assert.False(console.IsCurrentInput);

        SessionTools withToken = Tools(new RecordingLogger(), console: 1, hasToken: true,
            Session(1, "Console", state: 0, user: "LAB\\user"),
            Session(2, "RDP-Tcp#4", state: 4, user: "LAB\\other"));
        LogonSessionList listed = withToken.ListLogonSessions().Value!;
        Assert.Equal(2, listed.Total);
        Assert.True(listed.Sessions.Single(item => item.IsCurrentInput).IsConsole);
        Assert.Equal(1u, listed.Sessions.Single(item => item.IsCurrentInput).SessionId);
        Assert.False(listed.Sessions.Single(item => item.SessionId == 2).IsConsole);
    }

    [Fact]
    public void List_OmitsClientDetailsUntilRequestedAndNeverLogsThem()
    {
        RecordingLogger logger = new();
        SessionTools tools = Tools(logger, console: 1, hasToken: true,
            Session(1, "Console", state: 0, user: "LAB\\user", address: "203.0.113.10", client: "LAB-PC", protocol: 2));

        LogonSession hidden = tools.ListLogonSessions().Value!.Sessions[0];
        LogonSession shown = tools.ListLogonSessions(includeClientAddresses: true).Value!.Sessions[0];

        Assert.Null(hidden.ClientAddress);
        Assert.Null(hidden.ClientName);
        Assert.Equal("203.0.113.10", shown.ClientAddress);
        Assert.Equal("LAB-PC", shown.ClientName);
        Assert.Equal("rdp", shown.Protocol);
        Assert.DoesNotContain("203.0.113.10", string.Join('\n', logger.Lines), StringComparison.Ordinal);
        Assert.DoesNotContain("LAB-PC", string.Join('\n', logger.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void List_FiltersDisconnectedSessionsAndListeners()
    {
        SessionTools tools = Tools(new RecordingLogger(), console: 1, hasToken: true,
            Session(1, "Console", state: 0, user: "LAB\\user"),
            Session(2, "RDP-Tcp#4", state: 4, user: "LAB\\other"),
            Session(3, "RDP-Tcp", state: 6, user: null));

        Assert.Equal([1u, 2u], tools.ListLogonSessions().Value!.Sessions.Select(item => item.SessionId));
        Assert.Equal([1u], tools.ListLogonSessions(includeDisconnected: false).Value!.Sessions.Select(item => item.SessionId));
        Assert.Equal([1u, 2u, 3u], tools.ListLogonSessions(includeListeners: true).Value!.Sessions.Select(item => item.SessionId));
    }

    [Fact]
    public void Get_ReturnsNullableTokenDetailsAndRejectsAMissingSession()
    {
        RecordingLogger logger = new();
        FakeSessions platform = new(
            SessionSnapshot.Ok(1, true, [Session(1, "Console", state: 0, user: "LAB\\user", address: "203.0.113.10", client: "LAB-PC")]),
            new SessionTokenRead(true, "limited", false, "0x0:3E7"));
        SessionTools tools = new(platform, logger);

        Assert.Equal("invalid_request", tools.GetLogonSession(-1).ErrorCode);
        Assert.Equal("session_not_found", tools.GetLogonSession(9).ErrorCode);

        LogonSessionDetail hidden = tools.GetLogonSession(1).Value!;
        Assert.Null(hidden.ClientAddress);
        Assert.Equal("limited", hidden.Elevation);
        Assert.False(hidden.IsAdministrator);
        Assert.Equal("0x0:3E7", hidden.LogonId);
        Assert.True(hidden.IsCurrentInput);

        platform.Token = new SessionTokenRead(false, "full", true, "0x1:1");
        LogonSessionDetail closed = tools.GetLogonSession(1, includeClientAddresses: true).Value!;
        Assert.Null(closed.Elevation);
        Assert.Null(closed.IsAdministrator);
        Assert.Null(closed.LogonId);
        Assert.Equal("203.0.113.10", closed.ClientAddress);
        Assert.DoesNotContain("203.0.113.10", string.Join('\n', logger.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void List_PassesThroughATerminalServicesFailure()
    {
        SessionTools tools = new(new FakeSessions(SessionSnapshot.Fail("wts_unavailable", "Windows Terminal Services did not return logon sessions."), new SessionTokenRead(false, null, null, null)), new RecordingLogger());

        Assert.Equal("wts_unavailable", tools.ListLogonSessions().ErrorCode);
        Assert.Equal("access_denied", new SessionTools(new FakeSessions(SessionSnapshot.Fail("access_denied", "Windows denied access to logon sessions."), new SessionTokenRead(false, null, null, null)), new RecordingLogger()).GetLogonSession(1).ErrorCode);
    }

    [Fact]
    public void WindowsPlatform_ListsTheConsoleWhenAUserIsLoggedOn()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Requires Windows.");
        }

        SessionTools tools = new(new WindowsSessionPlatform(), new RecordingLogger());
        OperationResult<LogonSessionList> listed = tools.ListLogonSessions(includeDisconnected: true, includeListeners: true);
        if (!listed.Success)
        {
            Assert.Skip($"Logon session enumeration is not available here: {listed.ErrorCode}");
        }

        Assert.NotEmpty(listed.Value!.Sessions);
        Assert.All(listed.Value.Sessions, session => Assert.False(string.IsNullOrWhiteSpace(session.State)));
        LogonSession[] input = listed.Value.Sessions.Where(session => session.IsCurrentInput).ToArray();
        Assert.All(input, session => Assert.True(session.IsConsole));
        if (input.Length != 1 || !listed.Value.Sessions.Any(session => session.State == "disconnected"))
        {
            Assert.Skip("Requires a console user and one disconnected session.");
        }

        LogonSession console = input[0];
        OperationResult<LogonSessionDetail> detail = tools.GetLogonSession((int)console.SessionId);
        Assert.True(detail.Success, detail.Error);
        Assert.Equal(console.SessionId, detail.Value!.SessionId);
        Assert.True(detail.Value.IsCurrentInput);
        Assert.DoesNotContain(listed.Value.Sessions, session => session.State == "disconnected" && session.IsCurrentInput);
    }

    private static SessionTools Tools(RecordingLogger logger, uint? console, bool hasToken, params RawLogonSession[] sessions) =>
        new(new FakeSessions(SessionSnapshot.Ok(console, hasToken, sessions), new SessionTokenRead(false, null, null, null)), logger);

    private static RawLogonSession Session(
        uint id,
        string station,
        int state,
        string? user,
        string? address = null,
        string? client = null,
        int? protocol = 0) =>
        new(id, station, state, user, DateTimeOffset.Parse("2026-04-05T06:07:08Z"), client, address, protocol, station);

    private sealed class FakeSessions(SessionSnapshot snapshot, SessionTokenRead token) : ISessionPlatform
    {
        public SessionTokenRead Token { get; set; } = token;

        public SessionSnapshot Read() => snapshot;

        public SessionTokenRead ReadToken(uint sessionId) => Token;
    }

    private sealed class RecordingLogger : ILogger<SessionTools>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
