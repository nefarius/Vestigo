namespace Vestigo.Diagnostics;

/// <summary>Reads free bytes from the volume that contains a directory.</summary>
internal sealed class DriveVolumeSpace : IVolumeSpace
{
    public long? FreeBytes(string directory)
    {
        try
        {
            string full = Path.GetFullPath(directory);
            string root = Path.GetPathRoot(full) ?? full;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
