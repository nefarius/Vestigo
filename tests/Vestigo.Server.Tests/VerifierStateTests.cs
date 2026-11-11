using Vestigo.Diagnostics;

namespace Vestigo.Server.Tests;

public sealed class VerifierStateTests
{
    private const string IdleSettings = """
        Verifier Flags: 0x00000000

          Standard Flags:

            [ ] 0x00000001 Special pool.
            [ ] 0x00000002 Force IRQL checking.
            [ ] 0x00000008 Pool tracking.
            [ ] 0x00000010 I/O verification.
            [ ] 0x00000020 Deadlock detection.
            [ ] 0x00000080 DMA checking.
            [ ] 0x00000100 Security checks.
            [ ] 0x00000800 Miscellaneous checks.
            [ ] 0x00020000 DDI compliance checking.

          Additional Flags:

            [ ] 0x00000004 Randomized low resources simulation.
            [ ] 0x00000200 Force pending I/O requests.
            [ ] 0x00000400 IRP logging.
            [ ] 0x00002000 Invariant MDL checking for stack.
            [ ] 0x00004000 Invariant MDL checking for driver.
            [ ] 0x00008000 Power framework delay fuzzing.
            [ ] 0x00010000 Port/miniport interface checking.
            [ ] 0x00040000 Systematic low resources simulation.
            [ ] 0x00080000 DDI compliance checking (additional).
            [ ] 0x00200000 NDIS/WIFI verification.
            [ ] 0x00800000 Kernel synchronization delay fuzzing.
            [ ] 0x01000000 VM switch verification.
            [ ] 0x02000000 Code integrity checks.

            [X] Indicates flag is enabled.

          Active Rule Classes:

            None

          Boot Mode:

            Persistent

          Rules:

            All rules are using default settings

          Verified Drivers:

            None

        """;

    [Fact]
    public void Settings_ParseTheObservedIdleShapeAndKeepTheLegend()
    {
        DriverVerifierSettings settings = VerifierStateParser.ParseSettings(IdleSettings);
        Assert.True(settings.Recognized);
        Assert.Equal(0u, settings.Flags);
        Assert.Equal(9, settings.StandardFlags.Count);
        Assert.Equal(13, settings.AdditionalFlags.Count);
        Assert.All(settings.StandardFlags.Concat(settings.AdditionalFlags), flag => Assert.False(flag.Enabled));
        Assert.Equal("Special pool.", settings.StandardFlags[0].Name);
        Assert.Equal(1u, settings.StandardFlags[0].Value);
        Assert.Contains("[X] Indicates flag is enabled.", settings.Unparsed);
        Assert.Equal("Persistent", settings.BootMode);
        Assert.Equal(["None"], settings.Drivers);
        Assert.Equal(["None"], settings.RuleClasses);
    }

    [Fact]
    public void Settings_KeepAnUnknownLineAndAnEnabledFlag()
    {
        string text = IdleSettings.Replace("[ ] 0x00000001 Special pool.", "[X] 0x00000001 Special pool.\nFuture flag without a value", StringComparison.Ordinal)
            .Replace("Verifier Flags: 0x00000000", "Verifier Flags: 0x00000001", StringComparison.Ordinal);
        DriverVerifierSettings settings = VerifierStateParser.ParseSettings(text);
        Assert.Equal(1u, settings.Flags);
        Assert.True(settings.StandardFlags[0].Enabled);
        Assert.Contains("Future flag without a value", settings.Unparsed);
        Assert.Equal(["example.sys"], VerifierStateParser.ParseSettings(text.Replace("None", "example.sys", StringComparison.Ordinal)).Drivers);
    }

    [Fact]
    public void Running_RecognizesOnlyTheIdleSentence()
    {
        DriverVerifierQuery idle = VerifierStateParser.ParseRunning("No drivers are currently verified.\r\n");
        Assert.True(idle.Recognized);
        Assert.False(idle.DriversVerified);

        DriverVerifierQuery active = VerifierStateParser.ParseRunning("Verified drivers:\r\n\r\nexample.sys\r\n");
        Assert.False(active.Recognized);
        Assert.Null(active.DriversVerified);
        Assert.Contains("example.sys", active.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_ReturnsTimeoutWithoutMixingTheTwoQueries()
    {
        FakeQuery query = new()
        {
            Running = new VerifierCommandResult(null, string.Empty, false, true),
            Settings = new VerifierCommandResult(0, IdleSettings, false, false),
        };
        OperationResult<DriverVerifierState> timedOut = await new VerifierStateService(query).ReadAsync(CancellationToken.None);
        Assert.Equal("verifier_timeout", timedOut.ErrorCode);

        query.Running = new VerifierCommandResult(0, "No drivers are currently verified.", false, false);
        OperationResult<DriverVerifierState> state = await new VerifierStateService(query).ReadAsync(CancellationToken.None);
        Assert.True(state.Success);
        Assert.False(state.Value!.Running.DriversVerified);
        Assert.Equal(["None"], state.Value.Configured.Drivers);
        Assert.DoesNotContain("Verifier Flags", state.Value.Running.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsQuery_ReadsConfiguredFlagsWithoutEnablingVerifier()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "verifier.exe is a Windows tool.");
        WindowsVerifierQuery query = new();
        VerifierCommandResult settings = await query.QuerySettingsAsync(CancellationToken.None);
        Assert.False(settings.TimedOut);
        DriverVerifierSettings parsed = VerifierStateParser.ParseSettings(settings.Text);
        Assert.True(parsed.Recognized);
        Assert.NotNull(parsed.Flags);
        VerifierCommandResult running = await query.QueryAsync(CancellationToken.None);
        Assert.False(running.TimedOut);
        Assert.False(string.IsNullOrWhiteSpace(running.Text));
    }

    private sealed class FakeQuery : IVerifierQuery
    {
        public VerifierCommandResult Running { get; set; } = new(0, string.Empty, false, false);

        public VerifierCommandResult Settings { get; set; } = new(0, string.Empty, false, false);

        public Task<VerifierCommandResult> QueryAsync(CancellationToken cancellationToken) => Task.FromResult(Running);

        public Task<VerifierCommandResult> QuerySettingsAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);
    }
}
