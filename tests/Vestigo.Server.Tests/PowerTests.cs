using Vestigo.Power;

namespace Vestigo.Server.Tests;

public class PowerTests
{
    [Fact]
    public void Battery_KeepsChargingSeparateFromAcStatus()
    {
        (string? ac, bool? present, int? percent, bool? charging, int? seconds) = PowerMaps.Battery(
            new BatteryStatusRaw(PowerMaps.AcOffline, PowerMaps.BatteryChargingFlag, 40, 120));

        Assert.Equal("offline", ac);
        Assert.True(present);
        Assert.Equal(40, percent);
        Assert.True(charging);
        Assert.Equal(120, seconds);
    }

    [Fact]
    public void Battery_OmitsDetailsWhenNoBatteryIsPresentOrTheStatusIsUnknown()
    {
        (string? online, bool? missing, int? missingPercent, bool? missingCharging, int? missingSeconds) = PowerMaps.Battery(
            new BatteryStatusRaw(PowerMaps.AcOnline, PowerMaps.BatteryMissingFlag, PowerMaps.UnknownByte, PowerMaps.UnknownSeconds));
        Assert.Equal("online", online);
        Assert.False(missing);
        Assert.Null(missingPercent);
        Assert.Null(missingCharging);
        Assert.Null(missingSeconds);

        (string? unknownAc, bool? unknown, int? percent, bool? charging, int? seconds) = PowerMaps.Battery(
            new BatteryStatusRaw(PowerMaps.UnknownByte, PowerMaps.UnknownByte, 10, 10));
        Assert.Equal("unknown", unknownAc);
        Assert.Null(unknown);
        Assert.Null(percent);
        Assert.Null(charging);
        Assert.Null(seconds);
        Assert.Null(PowerMaps.Battery(null).Ac);
    }

    [Fact]
    public void LidAction_DoesNotTreatAMissingIndexAsDoNothing()
    {
        Assert.Null(PowerMaps.LidAction(null));
        Assert.Equal("doNothing", PowerMaps.LidAction(0));
        Assert.Equal("unknown", PowerMaps.LidAction(1));
        Assert.Equal("sleep", PowerMaps.LidAction(2));
        Assert.Equal("hibernate", PowerMaps.LidAction(3));
        Assert.Equal("shutdown", PowerMaps.LidAction(4));
        Assert.Equal("unknown", PowerMaps.LidAction(8));
    }

    [Fact]
    public void PolicyIndices_KeepZeroAndLeaveOtherEnableValuesNull()
    {
        Assert.Equal(0, PowerMaps.Seconds(0));
        Assert.Equal(600, PowerMaps.Seconds(600));
        Assert.Null(PowerMaps.Seconds(null));
        Assert.False(PowerMaps.Enabled(0));
        Assert.True(PowerMaps.Enabled(1));
        Assert.Null(PowerMaps.Enabled(2));
        Assert.Null(PowerMaps.Enabled(null));
    }

    [Fact]
    public void InterruptTime_SubtractsFromTheSuppliedClock()
    {
        DateTimeOffset now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        ulong ago = (ulong)TimeSpan.FromSeconds(30).Ticks;

        Assert.Equal(now.AddSeconds(-30), PowerMaps.InterruptTimeToUtc(1000, 1000 + ago, now));
        Assert.Null(PowerMaps.InterruptTimeToUtc(0, 1000, now));
        Assert.Null(PowerMaps.InterruptTimeToUtc(50, 10, now));
        Assert.Null(PowerMaps.InterruptTimeToUtc(10, null, now));
    }

    [Fact]
    public void Tools_ReturnPowerApiFailedWhenThePlatformThrows()
    {
        PowerService service = new(new ThrowingPlatform());

        Assert.Equal("power_api_failed", service.GetPowerState().ErrorCode);
        Assert.Equal("power_api_failed", service.GetSleepConfiguration().ErrorCode);
    }

    [Fact]
    public void WindowsPlatform_ReadsAcSchemeAndPolicy()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Requires Windows.");
        }

        WindowsPowerPlatform platform = new();
        PowerStateResult state = PowerMaps.State(platform.ReadState(), DateTimeOffset.UtcNow);
        Assert.Contains(state.AcLineStatus, new[] { "online", "offline", "unknown" });
        if (state.BatteryPresent != true)
        {
            Assert.Null(state.BatteryPercent);
            Assert.Null(state.BatteryCharging);
            Assert.Null(state.BatteryTimeRemainingSeconds);
        }

        if (state.ActiveSchemeGuid is string guid)
        {
            Assert.True(Guid.TryParse(guid, out _));
        }

        if (state.LastWakeTimeUtc is DateTimeOffset wake)
        {
            Assert.Equal(TimeSpan.Zero, wake.Offset);
        }

        SleepConfigurationResult sleep = PowerMaps.Sleep(platform.ReadSleep());
        AssertLid(sleep.LidCloseActionAc);
        AssertLid(sleep.LidCloseActionDc);
        AssertTimeout(sleep.StandbyTimeoutAcSeconds);
        AssertTimeout(sleep.MonitorTimeoutDcSeconds);
    }

    [Fact]
    public void WindowsPlatform_ReadsABatteryWhenPresent()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Requires Windows.");
        }

        PowerStateResult state = PowerMaps.State(new WindowsPowerPlatform().ReadState(), DateTimeOffset.UtcNow);
        if (state.BatteryPresent != true)
        {
            Assert.Skip("Requires a battery. Desktop machines and CI runners have none, so battery fields stay null.");
        }

        Assert.NotNull(state.BatteryCharging);
        if (state.BatteryPercent is int percent)
        {
            Assert.InRange(percent, 0, 100);
        }
    }

    private static void AssertLid(string? action)
    {
        if (action is not null)
        {
            Assert.Contains(action, new[] { "doNothing", "sleep", "hibernate", "shutdown", "unknown" });
        }
    }

    private static void AssertTimeout(int? seconds)
    {
        if (seconds is int value)
        {
            Assert.True(value >= 0);
        }
    }

    private sealed class ThrowingPlatform : IPowerPlatform
    {
        public PowerStateSample ReadState() => throw new IOException("Power status could not be read.");

        public SleepConfigurationSample ReadSleep() => throw new IOException("Sleep policy could not be read.");
    }
}
