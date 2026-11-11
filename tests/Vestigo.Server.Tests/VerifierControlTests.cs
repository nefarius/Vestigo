using Vestigo.Configuration;
using Vestigo.Diagnostics;
using Vestigo.Installer.Actions.Logic;

namespace Vestigo.Server.Tests;

public sealed class VerifierControlTests
{
    [Fact]
    public void Enable_UsesStandardFlagsOrANamedMaskAndRefusesTheKernel()
    {
        OperationResult<IReadOnlyList<string>> standard = VerifierControlPlanner.Enable(["contoso.sys"], null, true);
        Assert.Equal(["/standard", "/driver", "contoso.sys"], standard.Value);

        OperationResult<IReadOnlyList<string>> named = VerifierControlPlanner.Enable(["contoso.sys"], ["specialPool", "ioVerification"], true);
        Assert.Equal(["/flags", "0x00000011", "/driver", "contoso.sys"], named.Value);

        Assert.Equal("verifier_acknowledgement_required", VerifierControlPlanner.Enable(["contoso.sys"], null, false).ErrorCode);
        Assert.Equal("protected_driver", VerifierControlPlanner.Enable(["win32k.sys"], null, true).ErrorCode);
        Assert.Equal("invalid_request", VerifierControlPlanner.Enable(["..\\ntoskrnl.sys"], null, true).ErrorCode);
        Assert.Equal("invalid_request", VerifierControlPlanner.Enable(["contoso.sys"], ["lowResources"], true).ErrorCode);
        Assert.False(VerifierControlPlanner.IsAllowed(["/all"]));
        Assert.False(VerifierControlPlanner.IsAllowed(["/standard", "/driver", "win32k.sys"]));
        Assert.Equal(["/reset"], VerifierControlPlanner.Reset());
    }

    [Fact]
    public async Task Service_KeepsResetAvailableAndReadsBackAfterEnable()
    {
        FakeControl control = new();
        FakeQuery query = new()
        {
            Running = new VerifierCommandResult(0, "No drivers are currently verified.", false, false),
            Settings = new VerifierCommandResult(0, "Verifier Flags: 0x00000011\r\nVerified Drivers:\r\ncontoso.sys\r\n", false, false),
        };
        VerifierStateService state = new(query);
        VerifierControlService disabled = new(control, state, new VestigoOptions());
        Assert.Equal("driver_verifier_disabled", (await disabled.EnableAsync(["contoso.sys"], null, true, CancellationToken.None)).ErrorCode);
        Assert.Empty(control.Calls);

        OperationResult<DriverVerifierChange> reset = await disabled.ResetAsync(CancellationToken.None);
        Assert.True(reset.Success);
        Assert.Equal(["/reset"], reset.Value!.Arguments);
        Assert.NotNull(reset.Value.ReadBack);

        VerifierControlService enabled = new(control, state, new VestigoOptions { Diagnostics = { AllowDriverVerifier = true } });
        OperationResult<DriverVerifierChange> change = await enabled.EnableAsync(["contoso.sys"], ["specialPool"], true, CancellationToken.None);
        Assert.True(change.Success);
        Assert.Equal(["/flags", "0x00000001", "/driver", "contoso.sys"], change.Value!.Arguments);
        Assert.Equal(0x11u, change.Value.ReadBack!.Configured.Flags);
        Assert.Contains("contoso.sys", change.Value.ReadBack.Configured.Drivers);
        Assert.Contains("does not reboot", change.Value.Reboot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WindowsControl_RejectsAnUnplannedArgumentList()
    {
        VerifierQueryException ex = await Assert.ThrowsAsync<VerifierQueryException>(() =>
            new WindowsVerifierControl().RunAsync(["/all"], CancellationToken.None));
        Assert.Equal("verifier_failed", ex.Code);
    }

    [Fact]
    public void Installer_NestsTheVerifierGateUnderDiagnosticsAndLeavesItOff()
    {
        Assert.Equal("Diagnostics", ModuleSelection.Find(ModuleSelection.DiagnosticsDriverVerifier)?.Parent);
        Assert.False(ModuleSelection.Defaults()[ModuleSelection.DiagnosticsDriverVerifier]);
    }

    private sealed class FakeControl : IVerifierControl
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<VerifierCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            return Task.FromResult(new VerifierCommandResult(0, string.Empty, false, false));
        }
    }

    private sealed class FakeQuery : IVerifierQuery
    {
        public VerifierCommandResult Running { get; set; } = new(0, string.Empty, false, false);

        public VerifierCommandResult Settings { get; set; } = new(0, string.Empty, false, false);

        public Task<VerifierCommandResult> QueryAsync(CancellationToken cancellationToken) => Task.FromResult(Running);

        public Task<VerifierCommandResult> QuerySettingsAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);
    }
}
