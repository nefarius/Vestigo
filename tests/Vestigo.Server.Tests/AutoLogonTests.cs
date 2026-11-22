using System.Security.Cryptography;

using Microsoft.Extensions.Logging.Abstractions;

using Vestigo.Configuration;
using Vestigo.Hosting;
using Vestigo.Installer.Actions.Logic;
using Vestigo.Modules;
using Vestigo.Processes;
using Vestigo.SystemInfo;

namespace Vestigo.Server.Tests;

public class AutoLogonTests
{
    [Fact]
    public void SystemOptions_RejectsNonPositiveAndOverlongConsentLifetime()
    {
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions
        {
            System = { LogonConsentLifetime = TimeSpan.Zero }
        }.Validate());
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions
        {
            System = { LogonConsentLifetime = TimeSpan.FromMinutes(31) }
        }.Validate());
        new VestigoOptions { System = { LogonConsentLifetime = TimeSpan.FromMinutes(30) } }.Validate();
    }

    [Fact]
    public void PrivilegedWarning_NamesAutoLogonAndInteractiveLogonWithoutTokens()
    {
        VestigoOptions open = new()
        {
            System = { AllowAutoLogonChanges = true, AllowInteractiveLogon = true }
        };
        VestigoOptions tokens = new()
        {
            ApiTokens = ["t"],
            System = { AllowAutoLogonChanges = true, AllowInteractiveLogon = true }
        };

        string warning = ModuleRegistry.GetPrivilegedModuleWarning(open)!;
        Assert.Contains("Auto-logon changes", warning);
        Assert.Contains("Interactive logon/unlock", warning);
        Assert.Null(ModuleRegistry.GetPrivilegedModuleWarning(tokens));
    }

    [Fact]
    public void LogonAccount_NormalizesDomainSlashAndRejectsReserved()
    {
        Assert.True(LogonAccount.TryNormalize(@"CONTOSO\lab", null, out string user, out string? domain, out _));
        Assert.Equal("lab", user);
        Assert.Equal("CONTOSO", domain);
        Assert.False(LogonAccount.TryNormalize("SYSTEM", null, out _, out _, out _));
        Assert.False(LogonAccount.TryNormalize(@"CONTOSO\SYSTEM", null, out _, out _, out _));
        Assert.False(LogonAccount.TryNormalize("lab", "SYSTEM", out _, out _, out _));
        Assert.False(LogonAccount.TryNormalize(@"CONTOSO\lab", "CONTOSO", out _, out _, out _));
        Assert.False(LogonAccount.TryNormalizeLogonCount(0, out _, out _));
        Assert.True(LogonAccount.TryNormalizeLogonCount(null, out int? count, out _));
        Assert.Null(count);
    }

    [Fact]
    public void ConsentStore_IssuesAndCancelsWithoutReturningAPassword()
    {
        ManualClock time = new() { Now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero) };
        LogonConsentStore store = new(time, new VestigoOptions());
        OperationResult<LogonConsentLink> issued = store.Issue(
            LogonConsentKind.AutoLogonEnable, "lab", null, 1, false, "http://127.0.0.1:5098");
        Assert.True(issued.Success);
        Assert.Contains("/system/logon/consent/", issued.Value!.Url, StringComparison.Ordinal);
        Assert.Contains("ticket=", issued.Value.Url, StringComparison.Ordinal);
        Assert.Equal(LogonConsentLink.PresentToUser, issued.Value.Instruction);

        LogonRequestView view = store.Peek(issued.Value.RequestId)!;
        Assert.Equal("pending", view.Status);
        Assert.DoesNotContain("password", view.Notes, StringComparer.OrdinalIgnoreCase);
        Assert.True(store.Cancel(issued.Value.RequestId));
        Assert.Equal("cancelled", store.Peek(issued.Value.RequestId)!.Status);
    }

    [Fact]
    public void ConsentStore_RejectsMismatchExpiredReplayAndCapsRetries()
    {
        ManualClock time = new() { Now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero) };
        LogonConsentStore store = new(time, new VestigoOptions { System = { LogonConsentLifetime = TimeSpan.FromMinutes(1) } });
        LogonConsentLink link = store.Issue(LogonConsentKind.InteractiveLogon, "lab", null, null, false, "http://h").Value!;
        string ticket = Ticket(link);

        Assert.False(store.TryOpen("missing", ticket, out _, out string? code, out _));
        Assert.Equal("unknown_request", code);
        Assert.False(store.TryOpen(link.RequestId, "not-the-ticket", out _, out code, out _));
        Assert.Equal("mismatched_ticket", code);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(store.TryOpen(link.RequestId, ticket, out _, out code, out _));
        Assert.Equal("expired", code);

        LogonConsentLink retry = store.Issue(LogonConsentKind.AutoLogonEnable, "lab", null, null, false, "http://h").Value!;
        Assert.True(store.TryOpen(retry.RequestId, Ticket(retry), out LogonConsentStore.PendingConsent? pending, out _, out _));
        for (int attempt = 0; attempt < LogonConsentStore.MaxFailedAttempts - 1; attempt++)
        {
            Assert.True(store.RecordFailure(pending!.RequestId, out bool locked));
            Assert.False(locked);
        }

        Assert.True(store.RecordFailure(pending!.RequestId, out bool failed));
        Assert.True(failed);
        Assert.Equal("failed", store.Peek(retry.RequestId)!.Status);
        Assert.False(store.TryOpen(retry.RequestId, Ticket(retry), out _, out code, out _));
        Assert.Equal("already_completed", code);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(store.TryOpen(retry.RequestId, Ticket(retry), out _, out code, out _));
        Assert.Equal("already_completed", code);
        Assert.Equal("failed", store.Peek(retry.RequestId)!.Status);
    }

    [Fact]
    public void ConsentStore_ClaimsADecisionOnce()
    {
        LogonConsentStore store = new(TimeProvider.System, new VestigoOptions());
        LogonConsentLink link = store.Issue(LogonConsentKind.AutoLogonEnable, "lab", null, null, false, "http://h").Value!;
        Assert.True(store.TryBeginDecision(link.RequestId, Ticket(link), out _, out _, out _));
        Assert.False(store.TryBeginDecision(link.RequestId, Ticket(link), out _, out string? code, out _));
        Assert.Equal("already_completed", code);
        Assert.False(store.Cancel(link.RequestId));
        store.ReleaseDecision(link.RequestId);
        Assert.True(store.Cancel(link.RequestId, out LogonRequestView? cancelled));
        Assert.Equal("cancelled", cancelled!.Status);
    }

    [Fact]
    public async Task ConsentStore_WaitReturnsAfterCompletion()
    {
        ManualClock time = new();
        LogonConsentStore store = new(time, new VestigoOptions());
        LogonConsentLink link = store.Issue(LogonConsentKind.AutoLogonEnable, "lab", null, null, false, "http://h").Value!;
        Task<LogonRequestView?> wait = store.WaitAsync(link.RequestId, TimeSpan.FromSeconds(5), CancellationToken.None);
        store.Complete(link.RequestId, LogonRequestStatus.Approved, null, null);
        LogonRequestView? view = await wait;
        Assert.Equal("approved", view!.Status);
    }

    [Fact]
    public async Task InteractiveLogonService_MarksFailedWhenDispatchThrows()
    {
        VestigoOptions options = new() { System = { AllowInteractiveLogon = true } };
        LogonConsentStore store = new(TimeProvider.System, options);
        InteractiveLogonService service = new(
            new ThrowingDispatcher(),
            new LockedConsole(),
            new FakeAutoLogon(),
            store,
            options,
            NullLogger<InteractiveLogonService>.Instance);
        LogonConsentLink link = store.Issue(
            LogonConsentKind.InteractiveLogon,
            "lab",
            null,
            logonCount: null,
            forceAutoLogon: false,
            "http://127.0.0.1").Value!;
        Assert.True(store.TryOpen(link.RequestId, Ticket(link), out LogonConsentStore.PendingConsent? pending, out _, out _));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ApproveAsync(pending!, "secret".ToCharArray(), CancellationToken.None));

        LogonRequestView? view = store.Peek(link.RequestId);
        Assert.Equal("failed", view!.Status);
        Assert.Equal("logon_failed", view.ErrorCode);
    }

    [Fact]
    public void AutoLogonService_RefusesWhenGateIsOff_AndDoesNotCallPlatform()
    {
        FakeAutoLogon platform = new();
        AutoLogonHarness service = Service(platform, allow: false);
        Assert.Equal("autologon_disabled", service.RequestEnable("lab", null, null, false).ErrorCode);
        Assert.Equal("autologon_disabled", service.Disable(false).ErrorCode);
        Assert.Equal(0, platform.EnableCalls);
        Assert.Equal(0, platform.DisableCalls);
    }

    [Fact]
    public void AutoLogonService_ApproveStoresThroughPlatform()
    {
        FakeAutoLogon platform = new();
        AutoLogonHarness service = Service(platform, allow: true);
        LogonConsentLink link = service.RequestEnable("lab", "BOX", 2, true).Value!;
        Assert.True(service.Requests.Peek(link.RequestId) is not null);
        Assert.True(service.Store.TryOpen(link.RequestId, Ticket(link), out LogonConsentStore.PendingConsent? pending, out _, out _));
        OperationResult<LogonRequestView> approved = service.Approve(pending!, "secret".ToCharArray());
        Assert.True(approved.Success);
        Assert.Equal(1, platform.EnableCalls);
        Assert.Equal("lab", platform.LastUser);
        Assert.Equal("BOX", platform.LastDomain);
        Assert.Equal(2, platform.LastCount);
        Assert.True(platform.LastForce);
        Assert.Equal("approved", approved.Value!.Status);
        Assert.DoesNotContain("secret", approved.Value.Notes, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Protocol_RoundTripsHelloDispatchAndStatus()
    {
        byte[] hello = CredentialProviderProtocol.EncodeHello(
            new CredentialProviderProtocol.HelloMessage(
                CredentialProviderProtocol.UsageScenario.Unlock, 2, ["S-1-5-21-1"]));
        CredentialProviderProtocol.HelloMessage decoded = CredentialProviderProtocol.DecodeHello(hello);
        Assert.Equal(CredentialProviderProtocol.UsageScenario.Unlock, decoded.Scenario);
        Assert.Equal(2u, decoded.SessionId);
        Assert.Equal("S-1-5-21-1", decoded.UserSids.Single());

        byte[] dispatch = CredentialProviderProtocol.EncodeDispatch("lab", "BOX", "pw", "S-1-5-21-1");
        byte[] emoji = CredentialProviderProtocol.EncodeDispatch("lab", "BOX", "pw😀", "S-1-5-21-1");
        CredentialProviderProtocol.DispatchMessage emojiBody = CredentialProviderProtocol.DecodeDispatch(emoji);
        Assert.Equal("pw😀", new string(emojiBody.Password));
        CredentialProviderProtocol.DispatchMessage body = CredentialProviderProtocol.DecodeDispatch(dispatch);
        Assert.Equal("lab", body.UserName);
        Assert.Equal("BOX", body.Domain);
        Assert.Equal("pw", new string(body.Password));
        Assert.Equal("S-1-5-21-1", body.TargetSid);
        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(body.Password.AsSpan()));

        byte[] header = CredentialProviderProtocol.WriteHeader(CredentialProviderProtocol.MessageType.Status, 4);
        Assert.True(CredentialProviderProtocol.TryReadHeader(header, out CredentialProviderProtocol.MessageType type, out int length, out _));
        Assert.Equal(CredentialProviderProtocol.MessageType.Status, type);
        Assert.Equal(4, length);
        Assert.False(CredentialProviderProtocol.TryReadHeader([1, 2, 3], out _, out _, out string? error));
        Assert.Equal("truncated", error);
    }

    [Fact]
    public void CredentialProviderHost_UnlockDispatchUsesConsoleLockNotHelloScenario()
    {
        CredentialProviderProtocol.HelloMessage logon = new(CredentialProviderProtocol.UsageScenario.Logon, 1, ["S-1-5-21-1"]);
        CredentialProviderProtocol.HelloMessage unlock = new(
            CredentialProviderProtocol.UsageScenario.Unlock, 1, ["S-1-5-21-1"]);
        ConsoleLockRead locked = ConsoleLockRead.Ok("locked", 1, true, "lab");
        ConsoleLockRead logonScreen = ConsoleLockRead.Ok("logonScreen", 1, false, null);
        ConsoleLockRead unlocked = ConsoleLockRead.Ok("unlocked", 1, true, "lab");

        Assert.Null(CredentialProviderHost.RejectDispatchPeer(logon, requireUnlock: true, expectedSid: null, locked));
        Assert.Null(CredentialProviderHost.RejectDispatchPeer(unlock, requireUnlock: true, expectedSid: null, locked));
        Assert.Null(CredentialProviderHost.RejectDispatchPeer(logon, requireUnlock: false, expectedSid: null, consoleLock: null));
        Assert.Equal(
            "not_locked",
            CredentialProviderHost.RejectDispatchPeer(logon, requireUnlock: true, expectedSid: null, logonScreen)!.ErrorCode);
        Assert.Equal(
            "already_unlocked",
            CredentialProviderHost.RejectDispatchPeer(logon, requireUnlock: true, expectedSid: null, unlocked)!.ErrorCode);
        Assert.Equal(
            "sid_mismatch",
            CredentialProviderHost.RejectDispatchPeer(logon, requireUnlock: true, expectedSid: "S-1-5-21-9", locked)!.ErrorCode);
        ConsoleLockRead otherSession = ConsoleLockRead.Ok("locked", 2, true, "lab");
        Assert.Equal(
            "logonui_not_connected",
            CredentialProviderHost.RejectDispatchPeer(logon, requireUnlock: true, expectedSid: null, otherSession)!.ErrorCode);
    }

    [Fact]
    public void Identity_MatchesInstallerDeploymentConstants()
    {
        Assert.Equal(CredentialProviderIdentity.ClsidBraced, CredentialProviderDeployment.ClsidBraced);
        Assert.Equal(CredentialProviderIdentity.StagingFileName, CredentialProviderDeployment.StagingFileName);
        Assert.Equal(
            CredentialProviderIdentity.LiveFileName("0123456789abcdefzzzz"),
            CredentialProviderDeployment.LiveFileName("0123456789abcdefzzzz"));
    }

    [Fact]
    public void ConfigurationHints_DescribeTheNewSystemGates()
    {
        Assert.Contains("consent", ConfigurationFieldMetadata.BundledExecutableHint("System:AllowAutoLogonChanges"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Credential Provider", ConfigurationFieldMetadata.BundledExecutableHint("System:AllowInteractiveLogon"), StringComparison.Ordinal);
        Assert.Contains("lock_workstation", ConfigurationFieldMetadata.BundledExecutableHint("System:AllowInteractiveLogon"), StringComparison.Ordinal);
        Assert.Contains("consent", ConfigurationFieldMetadata.BundledExecutableHint("System:LogonConsentLifetime"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConsentLink_InstructsUserFacingBrowserAndForbidsHeadlessAgents()
    {
        string instruction = LogonConsentLink.PresentToUser;
        Assert.Contains("visible desktop browser", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clickable", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("headless", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("embedded", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Playwright", instruction, StringComparison.Ordinal);
        Assert.Contains("WebFetch", instruction, StringComparison.Ordinal);
        Assert.Contains("get_logon_request", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("Do not fetch the URL yourself", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionHelper_ParsesLockWorkStationArguments()
    {
        string[] args = [.. SessionHelper.BuildLockWorkStationArguments()];
        Assert.Equal(["--session-helper", "lock-workstation"], args);
        Assert.True(SessionHelper.TryParseLockWorkStation(args, out string? error));
        Assert.Null(error);
        Assert.False(SessionHelper.TryParseLockWorkStation([SessionHelper.Flag, SessionHelper.LockWorkStationCommand, "--extra"], out error));
        Assert.Contains("Unexpected argument", error!, StringComparison.Ordinal);
        Assert.False(SessionHelper.TryParseLockWorkStation([SessionHelper.Flag, "other"], out _));
    }

    [Fact]
    public async Task LockWorkstation_RefusesWhenGateIsOff()
    {
        FakeLock platform = new();
        InteractiveLogonService service = Interactive(
            new MutableConsole("unlocked", 1, "lab"),
            new MutableDispatcher(),
            allow: false,
            platform);
        OperationResult<WorkstationLockResult> result = await service.LockWorkstationAsync(5, CancellationToken.None);
        Assert.Equal("interactive_logon_disabled", result.ErrorCode);
        Assert.Equal(0, platform.Calls);
    }

    [Fact]
    public async Task LockWorkstation_IsIdempotentWhenAlreadyLocked()
    {
        FakeLock platform = new();
        MutableDispatcher dispatcher = new() { LogonUiConnected = true };
        InteractiveLogonService service = Interactive(
            new MutableConsole("locked", 1, "lab"),
            dispatcher,
            allow: true,
            platform);
        OperationResult<WorkstationLockResult> result = await service.LockWorkstationAsync(5, CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(result.Value!.Locked);
        Assert.True(result.Value.AlreadyLocked);
        Assert.Equal(0, platform.Calls);
    }

    [Fact]
    public async Task LockWorkstation_WaitsForLogonUiWhenAlreadyLocked()
    {
        MutableDispatcher dispatcher = new() { LogonUiConnected = false, ProviderRegistered = true };
        MutableConsole console = new("locked", 1, "lab");
        console.BeforeRead = reads =>
        {
            if (reads >= 2)
            {
                dispatcher.LogonUiConnected = true;
            }
        };
        FakeLock platform = new();
        InteractiveLogonService service = Interactive(console, dispatcher, allow: true, platform);
        OperationResult<WorkstationLockResult> result = await service.LockWorkstationAsync(5, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Value!.Locked);
        Assert.True(result.Value.AlreadyLocked);
        Assert.True(result.Value.LogonUiConnected);
        Assert.Equal(0, platform.Calls);
        Assert.True(console.Reads >= 2);
    }

    [Fact]
    public async Task LockWorkstation_ReportsUnknownWhenLaterObservationFails()
    {
        MutableDispatcher dispatcher = new() { LogonUiConnected = false, ProviderRegistered = true };
        MutableConsole console = new("locked", 1, "lab");
        console.AfterRead = _ => console.FailNext = true;
        FakeLock platform = new();
        InteractiveLogonService service = Interactive(console, dispatcher, allow: true, platform);
        OperationResult<WorkstationLockResult> result = await service.LockWorkstationAsync(1, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Value!.Locked);
        Assert.True(result.Value.AlreadyLocked);
        Assert.Equal("unknown", result.Value.ConsoleLockState);
        Assert.Contains(result.Value.Notes, note => note.Contains("could not be determined", StringComparison.Ordinal));
        Assert.Equal(0, platform.Calls);
    }

    [Fact]
    public async Task LockWorkstation_RejectsLogonScreen()
    {
        FakeLock platform = new();
        InteractiveLogonService service = Interactive(
            new MutableConsole("logonScreen", 1, null),
            new MutableDispatcher(),
            allow: true,
            platform);
        OperationResult<WorkstationLockResult> result = await service.LockWorkstationAsync(5, CancellationToken.None);
        Assert.Equal("no_console_session", result.ErrorCode);
        Assert.Equal(0, platform.Calls);
    }

    [Fact]
    public async Task LockWorkstation_InvokesHelperAndPollsUntilLockedAndConnected()
    {
        MutableDispatcher dispatcher = new() { LogonUiConnected = false, ProviderRegistered = true };
        MutableConsole console = new("unlocked", 1, "lab");
        console.BeforeRead = reads =>
        {
            if (reads >= 3)
            {
                dispatcher.LogonUiConnected = true;
            }
        };
        console.AfterRead = reads =>
        {
            if (reads == 1)
            {
                console.State = "locked";
            }
        };
        FakeLock platform = new();
        InteractiveLogonService service = Interactive(console, dispatcher, allow: true, platform);
        OperationResult<WorkstationLockResult> result = await service.LockWorkstationAsync(5, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Value!.Locked);
        Assert.False(result.Value.AlreadyLocked);
        Assert.True(result.Value.LogonUiConnected);
        Assert.Equal(1, platform.Calls);
        Assert.True(console.Reads >= 3);
    }

    [Fact]
    public async Task LockWorkstation_ReportsHelperFailure()
    {
        FakeLock platform = new()
        {
            OnLock = () => Task.FromResult(OperationResult<bool>.Fail("lock_failed", "Windows could not lock the workstation."))
        };
        InteractiveLogonService service = Interactive(
            new MutableConsole("unlocked", 1, "lab"),
            new MutableDispatcher(),
            allow: true,
            platform);
        OperationResult<WorkstationLockResult> result = await service.LockWorkstationAsync(5, CancellationToken.None);
        Assert.Equal("lock_failed", result.ErrorCode);
        Assert.Equal(1, platform.Calls);
    }

    [Fact]
    public void RequestUnlock_RefusesWhenGateIsOff()
    {
        InteractiveLogonService service = Interactive(
            new MutableConsole("locked", 1, "lab"),
            new MutableDispatcher { LogonUiConnected = true },
            allow: false);
        Assert.Equal("interactive_logon_disabled", service.RequestUnlock(null, null).ErrorCode);
    }

    [Fact]
    public void RequestUnlock_RequiresRegisteredProvider()
    {
        InteractiveLogonService service = Interactive(
            new MutableConsole("locked", 1, "lab"),
            new MutableDispatcher { ProviderRegistered = false, LogonUiConnected = true });
        Assert.Equal("provider_not_registered", service.RequestUnlock(null, null).ErrorCode);
    }

    [Fact]
    public void RequestUnlock_RefusesUnlockedAndLogonScreen()
    {
        MutableDispatcher dispatcher = new() { LogonUiConnected = true };
        Assert.Equal(
            "already_unlocked",
            Interactive(new MutableConsole("unlocked", 1, "lab"), dispatcher).RequestUnlock(null, null).ErrorCode);
        Assert.Equal(
            "not_locked",
            Interactive(new MutableConsole("logonScreen", 1, null), dispatcher).RequestUnlock(null, null).ErrorCode);
    }

    [Fact]
    public void RequestUnlock_RequiresLogonUi()
    {
        InteractiveLogonService service = Interactive(
            new MutableConsole("locked", 1, "lab"),
            new MutableDispatcher { LogonUiConnected = false });
        Assert.Equal("logonui_not_connected", service.RequestUnlock(null, null).ErrorCode);
    }

    [Fact]
    public void RequestUnlock_AutoFillsLockedConsoleUser()
    {
        InteractiveLogonService service = Interactive(
            new MutableConsole("locked", 1, @"BOX\lab"),
            new MutableDispatcher { LogonUiConnected = true });
        OperationResult<LogonConsentLink> issued = service.RequestUnlock(null, null);
        Assert.True(issued.Success);
        Assert.Equal("unlockWorkstation", issued.Value!.Kind);
        Assert.Equal("lab", issued.Value.UserName);
        Assert.Equal("BOX", issued.Value.Domain);
        Assert.Equal(LogonConsentLink.PresentToUser, issued.Value.Instruction);
    }

    [Fact]
    public void RequestUnlock_UsesExplicitAccountAndRejectsDomainWithoutUser()
    {
        InteractiveLogonService service = Interactive(
            new MutableConsole("locked", 1, "lab"),
            new MutableDispatcher { LogonUiConnected = true });
        OperationResult<LogonConsentLink> issued = service.RequestUnlock("other", "BOX");
        Assert.True(issued.Success);
        Assert.Equal("other", issued.Value!.UserName);
        Assert.Equal("BOX", issued.Value.Domain);
        Assert.Equal("invalid_request", service.RequestUnlock(null, "BOX").ErrorCode);
        Assert.Equal(
            "console_user_unknown",
            Interactive(
                    new MutableConsole("locked", 1, null),
                    new MutableDispatcher { LogonUiConnected = true })
                .RequestUnlock(null, null)
                .ErrorCode);
    }

    [Fact]
    public async Task ApproveUnlock_FailsWhenConsoleIsNoLongerLocked()
    {
        VestigoOptions options = new() { System = { AllowInteractiveLogon = true } };
        LogonConsentStore store = new(TimeProvider.System, options);
        MutableDispatcher dispatcher = new() { LogonUiConnected = true };
        InteractiveLogonService service = new(
            dispatcher,
            new MutableConsole("logonScreen", 1, null),
            new FakeAutoLogon(),
            store,
            options,
            NullLogger<InteractiveLogonService>.Instance);
        LogonConsentLink link = store.Issue(
            LogonConsentKind.UnlockWorkstation,
            "lab",
            null,
            logonCount: null,
            forceAutoLogon: false,
            "http://127.0.0.1").Value!;
        Assert.True(store.TryOpen(link.RequestId, Ticket(link), out LogonConsentStore.PendingConsent? pending, out _, out _));

        OperationResult<LogonRequestView> result = await service.ApproveAsync(
            pending!,
            "secret".ToCharArray(),
            CancellationToken.None);

        Assert.Equal("not_locked", result.ErrorCode);
        Assert.Equal(0, dispatcher.DispatchCalls);
        Assert.Equal("failed", store.Peek(link.RequestId)!.Status);
    }

    [Fact]
    public async Task ApproveUnlock_PassesRequireUnlockWhenConsoleStaysLocked()
    {
        VestigoOptions options = new() { System = { AllowInteractiveLogon = true } };
        LogonConsentStore store = new(TimeProvider.System, options);
        MutableDispatcher dispatcher = new() { LogonUiConnected = true };
        InteractiveLogonService service = new(
            dispatcher,
            new MutableConsole("locked", 1, "lab"),
            new FakeAutoLogon(),
            store,
            options,
            NullLogger<InteractiveLogonService>.Instance);
        LogonConsentLink link = store.Issue(
            LogonConsentKind.UnlockWorkstation,
            "lab",
            null,
            logonCount: null,
            forceAutoLogon: false,
            "http://127.0.0.1").Value!;
        Assert.True(store.TryOpen(link.RequestId, Ticket(link), out LogonConsentStore.PendingConsent? pending, out _, out _));

        OperationResult<LogonRequestView> result = await service.ApproveAsync(
            pending!,
            "secret".ToCharArray(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(dispatcher.LastRequireUnlock);
        Assert.Equal(1, dispatcher.DispatchCalls);
        Assert.Equal("approved", store.Peek(link.RequestId)!.Status);
    }

    private static string Ticket(LogonConsentLink link)
    {
        Uri uri = new(link.Url);
        string query = uri.Query.TrimStart('?');
        foreach (string part in query.Split('&'))
        {
            if (part.StartsWith("ticket=", StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(part["ticket=".Length..]);
            }
        }

        throw new InvalidOperationException("ticket missing");
    }

    private static InteractiveLogonService Interactive(
        ISessionPlatform sessions,
        IInteractiveLogonDispatcher dispatcher,
        bool allow = true,
        IWorkstationLockPlatform? lockPlatform = null)
    {
        VestigoOptions options = new() { System = { AllowInteractiveLogon = allow } };
        return new InteractiveLogonService(
            dispatcher,
            sessions,
            new FakeAutoLogon(),
            new LogonConsentStore(TimeProvider.System, options),
            options,
            NullLogger<InteractiveLogonService>.Instance,
            lockPlatform: lockPlatform);
    }

    private static AutoLogonHarness Service(FakeAutoLogon platform, bool allow)
    {
        VestigoOptions options = new() { System = { AllowAutoLogonChanges = allow } };
        LogonConsentStore store = new(TimeProvider.System, options);
        AutoLogonService service = new(platform, store, options, NullLogger<AutoLogonService>.Instance);
        return new AutoLogonHarness(service, store);
    }

    private sealed class AutoLogonHarness(AutoLogonService service, LogonConsentStore store)
    {
        public LogonConsentStore Store => store;

        public LogonConsentStore Requests => store;

        public OperationResult<LogonConsentLink> RequestEnable(string user, string? domain, int? count, bool force) =>
            service.RequestEnable(user, domain, count, force);

        public OperationResult<AutoLogonDisableResult> Disable(bool clear) => service.Disable(clear);

        public OperationResult<LogonRequestView> Approve(LogonConsentStore.PendingConsent pending, char[] password) =>
            service.Approve(pending, password);
    }

    private sealed class LockedConsole : ISessionPlatform
    {
        public SessionSnapshot Read() => throw new NotSupportedException();

        public SessionTokenRead ReadToken(uint sessionId) => throw new NotSupportedException();

        public ConsoleLockRead ReadConsoleLock() => ConsoleLockRead.Ok("locked", 1, true, "lab");
    }

    private sealed class MutableConsole(string state, uint? sessionId, string? userName) : ISessionPlatform
    {
        public string State { get; set; } = state;

        public int Reads { get; private set; }

        public Action<int>? BeforeRead { get; set; }

        public Action<int>? AfterRead { get; set; }

        public bool FailNext { get; set; }

        public SessionSnapshot Read() => throw new NotSupportedException();

        public SessionTokenRead ReadToken(uint sessionId) => throw new NotSupportedException();

        public ConsoleLockRead ReadConsoleLock()
        {
            Reads++;
            BeforeRead?.Invoke(Reads);
            if (FailNext)
            {
                AfterRead?.Invoke(Reads);
                return ConsoleLockRead.Fail("lock_state_unknown", "The console lock state could not be determined.");
            }

            ConsoleLockRead current = ConsoleLockRead.Ok(State, sessionId, userName is not null, userName);
            AfterRead?.Invoke(Reads);
            return current;
        }
    }

    private sealed class MutableDispatcher : IInteractiveLogonDispatcher
    {
        public int DispatchCalls { get; private set; }

        public bool LogonUiConnected { get; set; }

        public bool ProviderInstalled { get; set; } = true;

        public bool ProviderRegistered { get; set; } = true;

        public bool? LastRequireUnlock { get; private set; }

        public Task<OperationResult<string>> DispatchAsync(
            string userName,
            string? domain,
            char[] password,
            string? expectedSid,
            bool requireUnlock,
            CancellationToken cancellationToken)
        {
            LastRequireUnlock = requireUnlock;
            DispatchCalls++;
            return Task.FromResult(OperationResult<string>.Ok("submitted"));
        }
    }

    private sealed class FakeLock : IWorkstationLockPlatform
    {
        public int Calls { get; private set; }

        public Func<Task<OperationResult<bool>>>? OnLock { get; init; }

        public Task<OperationResult<bool>> LockAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return OnLock?.Invoke() ?? Task.FromResult(OperationResult<bool>.Ok(true));
        }
    }

    private sealed class ThrowingDispatcher : IInteractiveLogonDispatcher
    {
        public bool LogonUiConnected => true;

        public bool ProviderInstalled => true;

        public bool ProviderRegistered => true;

        public Task<OperationResult<string>> DispatchAsync(
            string userName,
            string? domain,
            char[] password,
            string? expectedSid,
            bool requireUnlock,
            CancellationToken cancellationToken) =>
            throw new InvalidDataException("truncated");
    }

    private sealed class FakeAutoLogon : IAutoLogonPlatform
    {
        public int EnableCalls { get; private set; }

        public int DisableCalls { get; private set; }

        public string? LastUser { get; private set; }

        public string? LastDomain { get; private set; }

        public int? LastCount { get; private set; }

        public bool LastForce { get; private set; }

        public AutoLogonState Read() =>
            new(false, null, null, null, false, false, false, null, ["The password is never returned."]);

        public OperationResult<AutoLogonChange> Enable(
            string userName,
            string? domain,
            ReadOnlySpan<char> password,
            int? logonCount,
            bool forceAutoLogon)
        {
            EnableCalls++;
            LastUser = userName;
            LastDomain = domain;
            LastCount = logonCount;
            LastForce = forceAutoLogon;
            return OperationResult<AutoLogonChange>.Ok(
                new AutoLogonChange(true, userName, domain, logonCount, forceAutoLogon, ["The password is never returned."]));
        }

        public OperationResult<AutoLogonChange> Disable(bool clearDefaultUser)
        {
            DisableCalls++;
            return OperationResult<AutoLogonChange>.Ok(new AutoLogonChange(false, null, null, null, false, []));
        }

        public OperationResult<bool> VerifyCredentials(string userName, string? domain, ReadOnlySpan<char> password) =>
            OperationResult<bool>.Ok(true);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan span) => Now += span;
    }
}

public class CredentialProviderDeploymentTests
{
    [Fact]
    public void Deploy_UnchangedHashLeavesRegistrationAlone()
    {
        MemoryStore store = new();
        byte[] dll = [1, 2, 3, 4];
        string install = Path.Combine(Path.GetTempPath(), "vestigo-cp-" + Guid.NewGuid().ToString("N"));
        string staged = CredentialProviderDeployment.StagingPath(install);
        store.Files[staged] = dll;
        string live = CredentialProviderDeployment.LivePath(install, CredentialProviderDeployment.HexSha256(dll));
        store.Files[live] = dll;
        store.Registered = live;

        CredentialProviderDeployResult result = CredentialProviderDeployment.Deploy(install, store);
        Assert.Equal("unchanged", result.Action);
        Assert.False(result.RegistrationUpdated);
        Assert.Equal(live, result.LivePath);
        Assert.Equal(1, store.WriteRegistrationCalls);
    }

    [Fact]
    public void Deploy_RotatesToANewHashedFileAndKeepsALockedPreviousCopy()
    {
        MemoryStore store = new();
        string install = Path.Combine(Path.GetTempPath(), "vestigo-cp-" + Guid.NewGuid().ToString("N"));
        string staged = CredentialProviderDeployment.StagingPath(install);
        store.Files[staged] = [9, 9, 9];
        string oldLive = CredentialProviderDeployment.LivePath(install, CredentialProviderDeployment.HexSha256([1]));
        store.Files[oldLive] = [1];
        store.Locked.Add(oldLive);
        store.Registered = oldLive;

        CredentialProviderDeployResult result = CredentialProviderDeployment.Deploy(install, store);
        Assert.Equal("rotated", result.Action);
        Assert.True(result.RegistrationUpdated);
        Assert.Contains(oldLive, result.LeftoverLocked);
        Assert.True(store.Files.ContainsKey(result.LivePath));
        Assert.Equal(result.LivePath, store.Registered);
    }

    [Fact]
    public void Remove_SkipsDuringUpgradeAndDeletesWhenNotLocked()
    {
        MemoryStore store = new();
        string install = Path.Combine(Path.GetTempPath(), "vestigo-cp-" + Guid.NewGuid().ToString("N"));
        store.Registered = @"C:\live.dll";
        Assert.Equal("upgrade-skip", CredentialProviderDeployment.Remove(install, upgrading: true, store).Action);
        Assert.Equal(@"C:\live.dll", store.Registered);

        string live = CredentialProviderDeployment.LivePath(install, "abcd");
        store.Files[live] = [1];
        CredentialProviderDeployResult removed = CredentialProviderDeployment.Remove(install, upgrading: false, store);
        Assert.Equal("removed", removed.Action);
        Assert.Null(store.Registered);
        Assert.False(store.Files.ContainsKey(live));
    }

    [Fact]
    public void Deploy_RejectsAReparsePoint()
    {
        MemoryStore store = new();
        string install = Path.Combine(Path.GetTempPath(), "vestigo-cp-" + Guid.NewGuid().ToString("N"));
        store.Files[install] = [];
        store.Reparse.Add(install);
        Assert.Throws<InvalidOperationException>(() => CredentialProviderDeployment.Deploy(install, store));
    }

    private sealed class MemoryStore : ICredentialProviderStore
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Reparse { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Registered { get; set; }

        public int WriteRegistrationCalls => _writes;

        private int _writes;

        public bool Exists(string path) => Files.ContainsKey(path) || Files.Keys.Any(key => key.StartsWith(path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        public bool IsReparsePoint(string path) => Reparse.Contains(path);

        public byte[] ReadAllBytes(string path) => Files[path];

        public void CreateDirectory(string path) => Files[Path.Combine(path, ".dir")] = [];

        public void WriteAllBytes(string path, byte[] content) => Files[path] = content;

        public bool TryDelete(string path)
        {
            if (Locked.Contains(path))
            {
                return false;
            }

            return Files.Remove(path);
        }

        public IReadOnlyList<string> EnumerateFiles(string directory, string pattern) =>
            Files.Keys.Where(path => path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) &&
                                     path.Contains("Vestigo.CredentialProvider.", StringComparison.OrdinalIgnoreCase) &&
                                     path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .ToArray();

        public string? ReadInprocServer32() => Registered;

        public void WriteRegistration(string dllPath)
        {
            _writes++;
            Registered = dllPath;
        }

        public void DeleteRegistration() => Registered = null;
    }
}
