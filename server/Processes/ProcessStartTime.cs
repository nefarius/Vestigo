using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace Vestigo.Processes;

/// <summary>Reads a process creation time without the non-public CsWin32 <c>FILETIME</c>.</summary>
internal static partial class ProcessStartTime
{
    // CsWin32 generates GetProcessTimes with a non-public FILETIME when NativeMethods.json disables marshaling.
    // A Windows FILETIME is a 64-bit count of 100-nanosecond intervals since 1601-01-01 UTC, which DateTime accepts.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    public static bool TryRead(SafeFileHandle process, out DateTimeOffset start, out int error)
    {
        bool read = TryRead(process.DangerousGetHandle(), out start, out error);
        GC.KeepAlive(process);
        return read;
    }

    public static bool TryRead(nint process, out DateTimeOffset start, out int error)
    {
        start = default;
        error = 0;
        if (process == 0 || GetProcessTimes(process, out long creation, out _, out _, out _) == 0)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            start = new DateTimeOffset(DateTime.FromFileTimeUtc(creation), TimeSpan.Zero);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            error = 87;
            return false;
        }
    }
}
