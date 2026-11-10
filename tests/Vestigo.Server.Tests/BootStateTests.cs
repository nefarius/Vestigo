using Vestigo.Configuration;
using Vestigo.SystemInfo;
using Vestigo.WindowsServicing;

namespace Vestigo.Server.Tests;

public class BootStateTests
{
    [Fact]
    public void FastStartup_IsNullWhenAbsentAndFalseOnlyForZero()
    {
        Assert.Null(BootState.FastStartup(null));
        Assert.Null(BootState.FastStartup("1"));
        Assert.False(BootState.FastStartup(0));
        Assert.True(BootState.FastStartup(1));
        Assert.True(BootState.FastStartup(2));
    }

    [Fact]
    public void SafeMode_MapsCleanBootAndLeavesOtherValuesNull()
    {
        Assert.Equal(BootState.Normal, BootState.SafeMode(0));
        Assert.Equal(BootState.Safe, BootState.SafeMode(1));
        Assert.Equal(BootState.Network, BootState.SafeMode(2));
        Assert.Null(BootState.SafeMode(3));
        Assert.Null(BootState.SafeMode(null));
    }

    [Fact]
    public void Uptime_SubtractsTheTickCountFromTheSuppliedClock()
    {
        DateTimeOffset now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(1, BootState.UptimeSeconds(1500));
        Assert.Equal(now.AddMilliseconds(-1500), BootState.ApproximateBootTime(1500, now));
        Assert.Null(BootState.UptimeSeconds(null));
        Assert.Null(BootState.ApproximateBootTime(null, now));
        Assert.Null(BootState.ApproximateBootTime(ulong.MaxValue, now));
        Assert.Equal((long)(ulong.MaxValue / 1000), BootState.UptimeSeconds(ulong.MaxValue));
    }

    [Fact]
    public void PairCount_MatchesTheServicingQueueParser()
    {
        string[][] samples =
        [
            [],
            [@"\??\C:\old"],
            [@"\??\C:\old", ""],
            [@"\??\C:\old", @"\??\C:\new", @"\??\C:\extra"]
        ];

        foreach (string[] values in samples)
        {
            Assert.Equal(
                RebootPendingService.Parse("PendingFileRenameOperations", values, int.MaxValue).Count,
                BootState.PairCount(values.Length));
        }

        Assert.Equal(RebootPendingService.QueueNames, BootState.PendingRenameValueNames);
    }

    [Fact]
    public void GetBootState_ProjectsTheSampleAndNullsAFailedRead()
    {
        DateTimeOffset now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        SystemService service = Create(new BootStateSample(90_000, false, 2, 3));

        BootStateResult value = service.GetBootState(now).Value!;

        Assert.Equal(90, value.UptimeSeconds);
        Assert.Equal(now.AddSeconds(-90), value.LastBootUpTimeUtc);
        Assert.False(value.FastStartupEnabled);
        Assert.Equal(BootState.Network, value.SafeMode);
        Assert.Equal(3, value.PendingFileRenameCount);

        BootStateResult failed = Create(new BootStateSample(1, true, 0, 1), throwOnRead: true).GetBootState(now).Value!;
        Assert.Null(failed.UptimeSeconds);
        Assert.Null(failed.LastBootUpTimeUtc);
        Assert.Null(failed.FastStartupEnabled);
        Assert.Null(failed.SafeMode);
        Assert.Null(failed.PendingFileRenameCount);
    }

    [Fact]
    public void WindowsPlatform_ReadsUptimeAndSafeMode()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Requires Windows.");
        }

        BootStateResult state = new SystemService(new WindowsVersionPlatform(), new VestigoOptions(), new WindowsBootStatePlatform())
            .GetBootState()
            .Value!;

        Assert.NotNull(state.UptimeSeconds);
        Assert.True(state.UptimeSeconds > 0);
        Assert.NotNull(state.LastBootUpTimeUtc);
        Assert.Equal(TimeSpan.Zero, state.LastBootUpTimeUtc!.Value.Offset);
        Assert.Contains(state.SafeMode, new[] { BootState.Normal, BootState.Safe, BootState.Network });
        if (state.PendingFileRenameCount is int count)
        {
            Assert.True(count >= 0);
        }
    }

    private static SystemService Create(BootStateSample sample, bool throwOnRead = false) =>
        new(new UnusedPlatform(), new VestigoOptions(), new FakeBoot(sample, throwOnRead));

    private sealed class FakeBoot(BootStateSample sample, bool throwOnRead) : IBootStatePlatform
    {
        public BootStateSample Read() => throwOnRead ? throw new IOException("The boot-state registry could not be read.") : sample;
    }

    private sealed class UnusedPlatform : ISystemPlatform
    {
        public string MachineName => "unused";
        public string Name => "unused";
        public string Edition => "unused";
        public string DisplayVersion => "unused";
        public string VersionString => "unused";
        public int MajorVersion => 0;
        public int MinorVersion => 0;
        public int? BuildVersion => null;
        public int RevisionVersion => 0;
        public bool IsWindows11 => false;
        public bool IsWindowsServer => false;
        public bool IsGrandfathered => false;
        public bool IsUacDisabled => false;
        public string OsBits => "64";
        public string ProcessorBits => "64";
        public bool IsArm64 => false;
        public bool IsRunningInUefiMode => false;
        public bool? IsSecureBootEnabled => null;
        public bool TestSigningConfigured => false;
        public bool TestSigningActive => false;
        public bool WhqlDeveloperTestMode => false;
        public void SetTestSigning(bool enabled) => throw new NotSupportedException();
        public void SetWhqlDeveloperTestMode(bool enabled) => throw new NotSupportedException();
        public UacSettings ReadUac() => throw new NotSupportedException();
        public void WriteUac(UacSettings settings) => throw new NotSupportedException();
    }
}
