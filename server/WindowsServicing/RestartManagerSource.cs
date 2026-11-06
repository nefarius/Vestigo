using System.ComponentModel;
using System.Runtime.InteropServices.ComTypes;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.RestartManager;

namespace Vestigo.WindowsServicing;

public sealed class RestartManagerSource : IFileLockerSource
{
    public FileLockers Query(string path, int limit)
    {
        Span<char> key = stackalloc char[checked((int)PInvoke.CCH_RM_SESSION_KEY + 1)];
        Check(PInvoke.RmStartSession(out uint session, key));
        try
        {
            Check(PInvoke.RmRegisterResources(session, [path], [], []));
            uint count = 0;
            WIN32_ERROR error = PInvoke.RmGetList(session, out uint needed, ref count, [], out uint reasons);
            for (int attempt = 0; error == WIN32_ERROR.ERROR_MORE_DATA && attempt < 3; attempt++)
            {
                // Native list must fit; never allocate in proportion to unbounded external counts.
                if (needed > 4096)
                {
                    return new([], reasons, true);
                }

                RM_PROCESS_INFO[] processes = new RM_PROCESS_INFO[needed];
                count = needed;
                error = PInvoke.RmGetList(session, out needed, ref count, processes, out reasons);
                if (error == WIN32_ERROR.ERROR_SUCCESS)
                {
                    return new(processes.Take((int)Math.Min(count, (uint)limit)).Select(p => new FileLocker(
                        checked((int)p.Process.dwProcessId), StartTime(p.Process.ProcessStartTime),
                        p.strAppName.ToString(), p.strServiceShortName.ToString(), p.TSSessionId)).ToArray(),
                        reasons, count > limit);
                }
            }

            if (error == WIN32_ERROR.ERROR_MORE_DATA)
            {
                return new([], reasons, true);
            }

            Check(error);
            return new([], reasons, false);
        }
        finally
        {
            PInvoke.RmEndSession(session);
        }
    }

    private static DateTimeOffset? StartTime(FILETIME time)
    {
        long value = ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
        try
        {
            return DateTimeOffset.FromFileTime(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void Check(WIN32_ERROR error)
    {
        if (error != WIN32_ERROR.ERROR_SUCCESS)
        {
            throw new Win32Exception((int)error);
        }
    }
}
