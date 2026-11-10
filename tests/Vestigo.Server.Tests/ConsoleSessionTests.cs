using Vestigo.Configuration;
using Vestigo.Diagnostics;
using Vestigo.EnvVars;
using Vestigo.Jobs;
using Vestigo.Processes;
using Vestigo.SystemInfo;

namespace Vestigo.Server.Tests;

public class ConsoleSessionTests
{
    [Fact]
    public void Explain_NamesEachMissingConsoleUser()
    {
        Assert.Equal(
            ConsoleSessionReasons.NobodyLoggedOn,
            ConsoleSession.Explain(ConsoleSession.NoSession, null, sessions: null, sessionZeroIsUnusable: false));
        Assert.Equal(
            ConsoleSessionReasons.Session0Only,
            ConsoleSession.ExplainTable(
            [
                new SessionPresence(0, 0),
                new SessionPresence(65536, ConsoleSession.ListenState)
            ]));
        Assert.Equal(
            ConsoleSessionReasons.OnlyDisconnectedSessions,
            ConsoleSession.ExplainTable(
            [
                new SessionPresence(0, 0),
                new SessionPresence(2, ConsoleSession.DisconnectedState)
            ]));
        Assert.Equal(
            ConsoleSessionReasons.NobodyLoggedOn,
            ConsoleSession.ExplainTable(
            [
                new SessionPresence(2, ConsoleSession.DisconnectedState),
                new SessionPresence(3, 0)
            ]));
        Assert.Equal(
            ConsoleSessionReasons.Session0Only,
            ConsoleSession.Explain(0, null, [new SessionPresence(2, ConsoleSession.DisconnectedState)], sessionZeroIsUnusable: true));
        Assert.Equal(
            ConsoleSessionReasons.ConsoleSessionHasNoToken,
            ConsoleSession.Explain(1, ConsoleSession.ErrorNoToken, null, sessionZeroIsUnusable: false));
        Assert.Equal(
            ConsoleSessionReasons.ConsoleSessionHasNoToken,
            ConsoleSession.Explain(0, ConsoleSession.ErrorNoToken, null, sessionZeroIsUnusable: false));
    }

    [Fact]
    public void Explain_LeavesAccessDeniedUnclassified()
    {
        Assert.Null(ConsoleSession.Explain(
            1,
            ConsoleSession.ErrorAccessDenied,
            [new SessionPresence(2, ConsoleSession.DisconnectedState)],
            sessionZeroIsUnusable: true));
        Assert.Null(ConsoleSession.Explain(ConsoleSession.NoSession, ConsoleSession.ErrorAccessDenied, null, sessionZeroIsUnusable: false));
        Assert.Null(ConsoleSession.Explain(1, tokenError: null, sessions: null, sessionZeroIsUnusable: false));
    }

    [Fact]
    public void Environment_KeepsTheCodeAndSurfacesTheReason()
    {
        UnavailableStore store = new() { Reason = ConsoleSessionReasons.OnlyDisconnectedSessions };
        EnvironmentService service = new(store, new VestigoOptions());

        OperationResult<EnvironmentVariableInfo> result = service.Get("user", "PATH");

        Assert.Equal("user_not_available", result.ErrorCode);
        Assert.Equal(ConsoleSessionReasons.Message, result.Error);
        Assert.Equal(ConsoleSessionReasons.OnlyDisconnectedSessions, result.Reason);
    }

    [Fact]
    public async Task CaptureProbe_RemapsTheCodeAndKeepsTheReason()
    {
        UnavailableLauncher launcher = new();
        SessionCaptureProbe probe = new(launcher, new SessionHelperClient(launcher));

        OperationResult<CaptureProbeResult> result = await probe.ProbeAsync("primary", "dxgi", 100, CancellationToken.None);

        Assert.Equal("user_not_available", result.ErrorCode);
        Assert.Equal(ConsoleSessionReasons.Session0Only, result.Reason);
        Assert.False(launcher.Started);
    }

    private sealed class UnavailableStore : IEnvironmentStore
    {
        public string? Reason { get; init; }

        public EnvironmentValue? Get(EnvironmentScope scope, string name) =>
            throw new UserNotAvailableException(ConsoleSessionReasons.Message, Reason);

        public IReadOnlyDictionary<string, EnvironmentValue> GetAll(EnvironmentScope scope) =>
            throw new UserNotAvailableException(ConsoleSessionReasons.Message, Reason);

        public void Set(EnvironmentScope scope, string name, EnvironmentValue value) =>
            throw new UserNotAvailableException(ConsoleSessionReasons.Message, Reason);

        public bool Remove(EnvironmentScope scope, string name) =>
            throw new UserNotAvailableException(ConsoleSessionReasons.Message, Reason);

        public bool BroadcastChange() => true;
    }

    private sealed class UnavailableLauncher : IProcessLauncher
    {
        public bool Started { get; private set; }

        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) =>
            OperationResult<bool>.Fail(
                "no_interactive_session",
                ConsoleSessionReasons.Message,
                ConsoleSessionReasons.Session0Only);

        public Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken)
        {
            Started = true;
            throw new InvalidOperationException("The probe started a process.");
        }
    }
}
