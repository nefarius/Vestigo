using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace Vestigo.Files;

/// <summary>Reboot-time file operations through <c>MoveFileEx</c>. Requires administrator rights.</summary>
public sealed class Win32RebootScheduler : IRebootScheduler
{
    public int ScheduleDelete(string path) =>
        Move(path, null, MOVE_FILE_FLAGS.MOVEFILE_DELAY_UNTIL_REBOOT);

    public int ScheduleReplace(string source, string destination) =>
        Move(source, destination, MOVE_FILE_FLAGS.MOVEFILE_DELAY_UNTIL_REBOOT | MOVE_FILE_FLAGS.MOVEFILE_REPLACE_EXISTING);

    private static int Move(string existing, string? destination, MOVE_FILE_FLAGS flags)
    {
        if (PInvoke.MoveFileEx(existing, destination, flags))
        {
            return 0;
        }

        return Marshal.GetLastPInvokeError();
    }
}
