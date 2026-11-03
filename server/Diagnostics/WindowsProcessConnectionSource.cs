using System.ComponentModel;
using System.Diagnostics;

namespace Vestigo.Diagnostics;

/// <summary>Reads <c>GetExtendedTcpTable</c> and <c>GetExtendedUdpTable</c> and keeps only the requested process.</summary>
public sealed class WindowsProcessConnectionSource : IProcessConnectionSource
{
    public ConnectionQuery Query(int pid, int limit)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ConnectionQuery.Fail("unsupported", "Process connection enumeration requires Windows.");
        }

        ProcessOpen opened = Open(pid);
        if (!opened.Success)
        {
            return ConnectionQuery.Fail(opened.ErrorCode!, opened.Error!);
        }

        TableRead table = IpHelperConnections.Read(pid, limit);
        if (!table.Success)
        {
            return ConnectionQuery.Fail(table.ErrorCode!, table.Error!);
        }

        if (opened.StartTime is { } started && !StillSameProcess(pid, started))
        {
            return ConnectionQuery.Fail("process_exited", "The process exited while its connections were being read.");
        }

        return ConnectionQuery.Ok(opened.StartTime, table.Partial, table.Truncated, table.Rows);
    }

    private static bool StillSameProcess(int pid, DateTimeOffset openedStart)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            DateTimeOffset? current = null;
            try
            {
                current = process.StartTime;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                current = null;
            }

            return WindowsProcessModuleSource.StartUnchanged(openedStart, current);
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static ProcessOpen Open(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            try
            {
                if (process.HasExited)
                {
                    return ProcessOpen.Fail("process_exited", "The process exited before its connections could be read.");
                }
            }
            catch (InvalidOperationException)
            {
                return ProcessOpen.Fail("process_exited", "The process exited before its connections could be read.");
            }

            DateTimeOffset? start = null;
            try
            {
                start = process.StartTime;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                start = null;
            }

            return ProcessOpen.Ok(start);
        }
        catch (ArgumentException)
        {
            return ProcessOpen.Fail("process_not_found", "The process is not running.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return ProcessOpen.Fail("access_denied", "The process could not be opened.");
        }
        catch (InvalidOperationException)
        {
            return ProcessOpen.Fail("process_exited", "The process exited before its connections could be read.");
        }
    }

    private readonly record struct ProcessOpen(bool Success, string? ErrorCode, string? Error, DateTimeOffset? StartTime)
    {
        public static ProcessOpen Ok(DateTimeOffset? startTime) => new(true, null, null, startTime);

        public static ProcessOpen Fail(string code, string error) => new(false, code, error, null);
    }
}
