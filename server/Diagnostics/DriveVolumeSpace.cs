using System.Runtime.InteropServices;

namespace Vestigo.Diagnostics;

/// <summary>Reads free bytes from the volume that contains a directory.</summary>
internal sealed class DriveVolumeSpace : IVolumeSpace
{
    public long? FreeBytes(string directory)
    {
        try
        {
            string full = Path.GetFullPath(directory);
            if (OperatingSystem.IsWindows())
            {
                return WindowsFreeBytes(full);
            }

            string root = Path.GetPathRoot(full) ?? full;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? WindowsFreeBytes(string directory)
    {
        if (!Path.EndsInDirectorySeparator(directory))
        {
            directory += Path.DirectorySeparatorChar;
        }

        if (!GetDiskFreeSpaceEx(directory, out ulong available, out _, out _))
        {
            return null;
        }

        return available > long.MaxValue ? null : (long)available;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDiskFreeSpaceExW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetDiskFreeSpaceEx(
        string directory,
        out ulong freeBytesAvailable,
        out ulong totalBytes,
        out ulong totalFreeBytes);
}
