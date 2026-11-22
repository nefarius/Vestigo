using System.Runtime.InteropServices;
using System.Text.Json;

using Windows.Win32;

namespace Vestigo.Processes;

public static partial class SessionHelper
{
    /// <summary>Builds helper arguments for locking the console session that hosts this process.</summary>
    public static List<string> BuildLockWorkStationArguments() => [Flag, LockWorkStationCommand];

    /// <summary>Parses <c>lock-workstation</c> helper arguments. Extra options are rejected.</summary>
    public static bool TryParseLockWorkStation(string[] args, out string? error)
    {
        error = null;
        if (args.Length < 2 || args[0] != Flag || args[1] != LockWorkStationCommand)
        {
            error = $"Usage: {Flag} {LockWorkStationCommand}";
            return false;
        }

        if (args.Length > 2)
        {
            error = $"Unexpected argument: {args[2]}";
            return false;
        }

        return true;
    }

    private static int RunLockWorkStation(string[] args)
    {
        if (!TryParseLockWorkStation(args, out string? error))
        {
            return Reject(error!);
        }

        if (!OperatingSystem.IsWindows())
        {
            return Reject("Interactive launch requires Windows.", "unsupported");
        }

        bool locked = PInvoke.LockWorkStation();
        if (!locked)
        {
            int win32 = Marshal.GetLastWin32Error();
            Console.Out.WriteLine(JsonSerializer.Serialize(
                new
                {
                    error = "Windows could not lock the workstation.",
                    errorCode = "lock_failed",
                    win32Error = win32
                },
                Json));
            return 1;
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(new { locked = true }, Json));
        return 0;
    }
}
