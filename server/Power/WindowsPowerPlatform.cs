using System.Runtime.CompilerServices;
using System.Text;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;

namespace Vestigo.Power;

/// <summary>Power reads through CsWin32. Policy writes are not called.</summary>
public sealed class WindowsPowerPlatform : IPowerPlatform
{
    /// <inheritdoc />
    public PowerStateSample ReadState()
    {
        BatteryStatusRaw? battery = ReadBattery();
        (string? guid, string? name) = ReadScheme();
        ulong? now = ReadInterruptTime();
        return new PowerStateSample(
            battery,
            guid,
            name,
            ReadInterruptEvent(POWER_INFORMATION_LEVEL.LastSleepTime),
            ReadInterruptEvent(POWER_INFORMATION_LEVEL.LastWakeTime),
            now,
            ReadCapabilities());
    }

    /// <inheritdoc />
    public WakeSourceResult ReadLastWake() => WindowsWakeSource.Read();

    /// <inheritdoc />
    public SleepConfigurationSample ReadSleep()
    {
        Guid? scheme = ReadSchemeGuid();
        return new SleepConfigurationSample(
            Index(scheme, PowerSettingGuids.Sleep, PowerSettingGuids.LidClose, directCurrent: false),
            Index(scheme, PowerSettingGuids.Sleep, PowerSettingGuids.LidClose, directCurrent: true),
            Index(scheme, PowerSettingGuids.Sleep, PowerSettingGuids.StandbyTimeout, directCurrent: false),
            Index(scheme, PowerSettingGuids.Sleep, PowerSettingGuids.StandbyTimeout, directCurrent: true),
            Index(scheme, PowerSettingGuids.Sleep, PowerSettingGuids.HibernateTimeout, directCurrent: false),
            Index(scheme, PowerSettingGuids.Sleep, PowerSettingGuids.HibernateTimeout, directCurrent: true),
            Index(scheme, PowerSettingGuids.Video, PowerSettingGuids.MonitorTimeout, directCurrent: false),
            Index(scheme, PowerSettingGuids.Video, PowerSettingGuids.MonitorTimeout, directCurrent: true),
            Index(scheme, PowerSettingGuids.Sleep, PowerSettingGuids.WakeTimers, directCurrent: false),
            Index(scheme, PowerSettingGuids.Sleep, PowerSettingGuids.WakeTimers, directCurrent: true),
            Index(scheme, PowerSettingGuids.Sleep, PowerSettingGuids.HybridSleep, directCurrent: false),
            Index(scheme, PowerSettingGuids.Sleep, PowerSettingGuids.HybridSleep, directCurrent: true));
    }

    private static unsafe BatteryStatusRaw? ReadBattery()
    {
        SYSTEM_POWER_STATUS status = default;
        if (!OperatingSystem.IsWindows() || !PInvoke.GetSystemPowerStatus(&status))
        {
            return null;
        }

        return new BatteryStatusRaw(status.ACLineStatus, status.BatteryFlag, status.BatteryLifePercent, status.BatteryLifeTime);
    }

    private static (string? Guid, string? Name) ReadScheme()
    {
        Guid? guid = ReadSchemeGuid();
        return guid is Guid value ? (value.ToString("D"), ReadSchemeName(value)) : (null, null);
    }

    private static unsafe Guid? ReadSchemeGuid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        Guid* scheme = null;
        if (PInvoke.PowerGetActiveScheme(default, &scheme) != 0 || scheme == null)
        {
            return null;
        }

        try
        {
            return *scheme;
        }
        finally
        {
            PInvoke.LocalFree((HLOCAL)(nint)scheme);
        }
    }

    private static unsafe string? ReadSchemeName(Guid scheme)
    {
        uint size = 0;
        PInvoke.PowerReadFriendlyName(default, &scheme, null, null, null, &size);
        if (size < 2 || size > 2048)
        {
            return null;
        }

        byte[] buffer = new byte[size];
        fixed (byte* pointer = buffer)
        {
            if (PInvoke.PowerReadFriendlyName(default, &scheme, null, null, pointer, &size) != 0)
            {
                return null;
            }
        }

        string name = Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
        return name.Length == 0 ? null : name;
    }

    private static unsafe ulong? ReadInterruptTime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        ulong now = 0;
        PInvoke.QueryInterruptTime(&now);
        return now;
    }

    private static unsafe ulong? ReadInterruptEvent(POWER_INFORMATION_LEVEL level)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        ulong ticks = 0;
        NTSTATUS status = PInvoke.CallNtPowerInformation(level, null, 0, &ticks, (uint)sizeof(ulong));
        return status.Value == 0 ? ticks : null;
    }

    private static unsafe PowerCapabilityRaw? ReadCapabilities()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        SYSTEM_POWER_CAPABILITIES capabilities = default;
        NTSTATUS status = PInvoke.CallNtPowerInformation(
            POWER_INFORMATION_LEVEL.SystemPowerCapabilities,
            null,
            0,
            &capabilities,
            (uint)sizeof(SYSTEM_POWER_CAPABILITIES));
        if (status.Value != 0)
        {
            return null;
        }

        return new PowerCapabilityRaw(
            Flag(capabilities.AoAc),
            Flag(capabilities.SystemS1),
            Flag(capabilities.SystemS2),
            Flag(capabilities.SystemS3),
            Flag(capabilities.SystemS4),
            Flag(capabilities.LidPresent),
            Flag(capabilities.WakeAlarmPresent),
            Flag(capabilities.HiberFilePresent),
            Flag(capabilities.Hiberboot));
    }

    private static unsafe PowerIndex Index(Guid? scheme, Guid subgroup, Guid setting, bool directCurrent)
    {
        if (scheme is not Guid active || !OperatingSystem.IsWindows())
        {
            return new PowerIndex(null);
        }

        uint value = 0;
        bool ok = directCurrent
            ? Succeeded(PInvoke.PowerReadDCValueIndex(default, &active, &subgroup, &setting, &value))
            : Succeeded(PInvoke.PowerReadACValueIndex(default, &active, &subgroup, &setting, &value));
        return ok && value <= int.MaxValue ? new PowerIndex((int)value) : new PowerIndex(null);
    }

    private static bool Succeeded(WIN32_ERROR error) => error == WIN32_ERROR.ERROR_SUCCESS;

    private static bool Succeeded(uint error) => error == 0;

    private static bool Flag<T>(T value) where T : unmanaged => Unsafe.As<T, byte>(ref value) != 0;
}
