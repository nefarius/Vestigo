using System.Security;

using Microsoft.Win32;

using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Vestigo.SystemInfo;

/// <summary>Boot-state reads through CsWin32 and the 64-bit registry view. Paths in the rename queues are not returned.</summary>
public sealed class WindowsBootStatePlatform : IBootStatePlatform
{
    /// <inheritdoc />
    public BootStateSample Read()
    {
        return new BootStateSample(ReadUptime(), ReadFastStartup(), ReadCleanBoot(), ReadPendingRenameCount());
    }

    private static ulong? ReadUptime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return PInvoke.GetTickCount64();
    }

    private static int? ReadCleanBoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CLEANBOOT);
    }

    private static bool? ReadFastStartup()
    {
        try
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = machine.OpenSubKey(BootState.FastStartupKey, writable: false);
            return BootState.FastStartup(key?.GetValue(BootState.FastStartupValue));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            return null;
        }
    }

    private static int? ReadPendingRenameCount()
    {
        try
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = machine.OpenSubKey(BootState.SessionManagerKey, writable: false);
            if (key is null)
            {
                return null;
            }

            int total = 0;
            foreach (string name in BootState.PendingRenameValueNames)
            {
                object? raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (raw is null)
                {
                    continue;
                }

                if (raw is not string[] values)
                {
                    return null;
                }

                total = checked(total + BootState.PairCount(values.Length));
            }

            return total;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException or OverflowException)
        {
            return null;
        }
    }
}
