using System.Diagnostics;

namespace Vestigo.Diagnostics;

internal static class ProcessNameLookup
{
    public static IReadOnlyDictionary<int, string> Snapshot()
    {
        Dictionary<int, string> names = [];
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return names;
        }

        foreach (Process process in processes)
        {
            try
            {
                names[process.Id] = process.ProcessName;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return names;
    }
}
