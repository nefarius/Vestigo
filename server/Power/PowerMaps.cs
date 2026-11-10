namespace Vestigo.Power;

/// <summary>Raw <c>SYSTEM_POWER_STATUS</c> fields. Battery charging is separate from AC line status.</summary>
public readonly record struct BatteryStatusRaw(byte AcLineStatus, byte BatteryFlag, byte BatteryLifePercent, uint BatteryLifeTime);

/// <summary>Platform capability flags from <c>SYSTEM_POWER_CAPABILITIES</c>. Null when that call failed.</summary>
public readonly record struct PowerCapabilityRaw(
    bool ModernStandby,
    bool SleepS1,
    bool SleepS2,
    bool SleepS3,
    bool Hibernate,
    bool LidPresent,
    bool WakeAlarm,
    bool HiberFilePresent,
    bool FastStartupSupported);

/// <summary>One power-state sample. Null fields were not available.</summary>
public readonly record struct PowerStateSample(
    BatteryStatusRaw? Battery,
    string? ActiveSchemeGuid,
    string? ActiveSchemeName,
    ulong? LastSleepInterruptTime,
    ulong? LastWakeInterruptTime,
    ulong? InterruptTimeNow,
    PowerCapabilityRaw? Capabilities);

/// <summary>One AC or DC index. Null means Windows did not return a value.</summary>
public readonly record struct PowerIndex(int? Value);

/// <summary>AC and DC indices for the documented sleep and display settings.</summary>
public readonly record struct SleepConfigurationSample(
    PowerIndex LidCloseAc,
    PowerIndex LidCloseDc,
    PowerIndex StandbyAc,
    PowerIndex StandbyDc,
    PowerIndex HibernateAc,
    PowerIndex HibernateDc,
    PowerIndex MonitorAc,
    PowerIndex MonitorDc,
    PowerIndex WakeTimersAc,
    PowerIndex WakeTimersDc,
    PowerIndex HybridSleepAc,
    PowerIndex HybridSleepDc);

/// <summary>
///     AC line, battery, active scheme, approximate last sleep and wake, and platform capabilities.
///     <paramref name="LastSleepTimeUtc" /> and <paramref name="LastWakeTimeUtc" /> are UTC now minus the
///     interrupt-time counter Windows returns. A clock change moves them. Zero means Windows reported no such event.
/// </summary>
public sealed record PowerStateResult(
    string? AcLineStatus,
    bool? BatteryPresent,
    int? BatteryPercent,
    bool? BatteryCharging,
    int? BatteryTimeRemainingSeconds,
    string? ActiveSchemeGuid,
    string? ActiveSchemeName,
    DateTimeOffset? LastSleepTimeUtc,
    DateTimeOffset? LastWakeTimeUtc,
    PowerCapabilities? Capabilities);

/// <summary>Flags from <c>SYSTEM_POWER_CAPABILITIES</c>. <c>FastStartupSupported</c> is not the configured <c>HiberbootEnabled</c> value.</summary>
public sealed record PowerCapabilities(
    bool ModernStandby,
    bool SleepS1,
    bool SleepS2,
    bool SleepS3,
    bool Hibernate,
    bool LidPresent,
    bool WakeAlarm,
    bool HiberFilePresent,
    bool FastStartupSupported);

/// <summary>
///     Effective AC and DC policy values. Null means the index was not returned.
///     A timeout of 0 means never. <c>doNothing</c> is only the lid index 0, never a missing value.
/// </summary>
public sealed record SleepConfigurationResult(
    string? LidCloseActionAc,
    string? LidCloseActionDc,
    int? StandbyTimeoutAcSeconds,
    int? StandbyTimeoutDcSeconds,
    int? HibernateTimeoutAcSeconds,
    int? HibernateTimeoutDcSeconds,
    int? MonitorTimeoutAcSeconds,
    int? MonitorTimeoutDcSeconds,
    bool? WakeTimersEnabledAc,
    bool? WakeTimersEnabledDc,
    bool? HybridSleepAc,
    bool? HybridSleepDc);

/// <summary>Reads power state and sleep policy. Tests replace the Windows implementation.</summary>
public interface IPowerPlatform
{
    PowerStateSample ReadState();

    SleepConfigurationSample ReadSleep();
}

/// <summary>Pure mapping from documented Windows power values. The clock is supplied by the caller.</summary>
public static class PowerMaps
{
    public const byte AcOffline = 0;
    public const byte AcOnline = 1;
    public const byte BatteryChargingFlag = 8;
    public const byte BatteryMissingFlag = 128;
    public const byte UnknownByte = 255;
    public const uint UnknownSeconds = uint.MaxValue;

    public static PowerStateResult State(PowerStateSample sample, DateTimeOffset utcNow)
    {
        (string? ac, bool? present, int? percent, bool? charging, int? seconds) = Battery(sample.Battery);
        return new PowerStateResult(
            ac,
            present,
            percent,
            charging,
            seconds,
            sample.ActiveSchemeGuid,
            sample.ActiveSchemeName,
            InterruptTimeToUtc(sample.LastSleepInterruptTime, sample.InterruptTimeNow, utcNow),
            InterruptTimeToUtc(sample.LastWakeInterruptTime, sample.InterruptTimeNow, utcNow),
            Capabilities(sample.Capabilities));
    }

    public static SleepConfigurationResult Sleep(SleepConfigurationSample sample) => new(
        LidAction(sample.LidCloseAc.Value),
        LidAction(sample.LidCloseDc.Value),
        Seconds(sample.StandbyAc.Value),
        Seconds(sample.StandbyDc.Value),
        Seconds(sample.HibernateAc.Value),
        Seconds(sample.HibernateDc.Value),
        Seconds(sample.MonitorAc.Value),
        Seconds(sample.MonitorDc.Value),
        Enabled(sample.WakeTimersAc.Value),
        Enabled(sample.WakeTimersDc.Value),
        Enabled(sample.HybridSleepAc.Value),
        Enabled(sample.HybridSleepDc.Value));

    /// <summary>0 offline, 1 online, anything else unknown. Null when status itself was not read.</summary>
    public static (string? Ac, bool? Present, int? Percent, bool? Charging, int? SecondsRemaining) Battery(BatteryStatusRaw? status)
    {
        if (status is not BatteryStatusRaw raw)
        {
            return (null, null, null, null, null);
        }

        string ac = raw.AcLineStatus switch
        {
            AcOffline => "offline",
            AcOnline => "online",
            _ => "unknown"
        };

        if (raw.BatteryFlag == UnknownByte || (raw.BatteryFlag & BatteryMissingFlag) != 0)
        {
            bool? present = raw.BatteryFlag == UnknownByte ? null : false;
            return (ac, present, null, null, null);
        }

        int? percent = raw.BatteryLifePercent == UnknownByte ? null : raw.BatteryLifePercent <= 100 ? raw.BatteryLifePercent : null;
        int? seconds = raw.BatteryLifeTime == UnknownSeconds || raw.BatteryLifeTime > int.MaxValue
            ? null
            : (int)raw.BatteryLifeTime;
        return (ac, true, percent, (raw.BatteryFlag & BatteryChargingFlag) != 0, seconds);
    }

    /// <summary><c>POWER_ACTION</c>: 0 do nothing, 2 sleep, 3 hibernate, 4 shutdown. Other indices are unknown. Null stays null.</summary>
    public static string? LidAction(int? index) => index switch
    {
        null => null,
        0 => "doNothing",
        2 => "sleep",
        3 => "hibernate",
        4 => "shutdown",
        _ => "unknown"
    };

    /// <summary>0 is false and 1 is true. Any other index is null.</summary>
    public static bool? Enabled(int? index) => index switch
    {
        0 => false,
        1 => true,
        _ => null
    };

    /// <summary>Seconds, including 0 (never). Null when the index was absent or does not fit.</summary>
    public static int? Seconds(int? index) => index is >= 0 ? index : null;

    /// <summary>UTC now minus the 100-nanosecond interrupt-time delta. Zero, a missing clock, or a future counter is null.</summary>
    public static DateTimeOffset? InterruptTimeToUtc(ulong? eventTime, ulong? now, DateTimeOffset utcNow)
    {
        if (eventTime is not ulong occurred || occurred == 0 || now is not ulong current || occurred > current)
        {
            return null;
        }

        ulong delta = current - occurred;
        return delta > long.MaxValue ? null : utcNow - TimeSpan.FromTicks((long)delta);
    }

    private static PowerCapabilities? Capabilities(PowerCapabilityRaw? raw) => raw is not PowerCapabilityRaw value
        ? null
        : new PowerCapabilities(
            value.ModernStandby,
            value.SleepS1,
            value.SleepS2,
            value.SleepS3,
            value.Hibernate,
            value.LidPresent,
            value.WakeAlarm,
            value.HiberFilePresent,
            value.FastStartupSupported);
}
