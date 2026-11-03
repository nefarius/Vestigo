using System.ComponentModel;
using System.Diagnostics;

using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.ToolHelp;

namespace Vestigo.Diagnostics;

/// <summary>
///     Lists modules with Toolhelp (<c>TH32CS_SNAPMODULE</c> and <c>TH32CS_SNAPMODULE32</c>).
///     Base addresses are omitted when <c>PROCESS_VM_READ</c> is denied. Metadata that cannot be read is null.
/// </summary>
public sealed class WindowsProcessModuleSource : IProcessModuleSource
{
    public ProcessModuleQuery Query(int pid, int limit)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ProcessModuleQuery.Fail("unsupported", "Process module enumeration requires Windows.");
        }

        ProcessQuery opened = Open(pid);
        if (!opened.Success)
        {
            return ProcessModuleQuery.Fail(opened.ErrorCode!, opened.Error!);
        }

        bool addresses = CanReadMemory(pid);
        List<LoadedModuleRecord> modules = [];
        int skipped = 0;
        bool partial = false;
        bool truncated = false;
        try
        {
            HANDLE rawSnapshot = PInvoke.CreateToolhelp32Snapshot(
                CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPMODULE | CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPMODULE32,
                (uint)pid);
            if (rawSnapshot.IsNull)
            {
                int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                if (error == 299)
                {
                    return ProcessModuleQuery.Ok(opened.StartTime, partial: true, truncated: false, skipped: 0, []);
                }

                return error == 5
                    ? ProcessModuleQuery.Fail("access_denied", "The process modules could not be read.")
                    : ProcessModuleQuery.Fail("process_not_found", "The process is not running.");
            }

            using SafeFileHandle snapshot = new(rawSnapshot, ownsHandle: true);
            unsafe
            {
            MODULEENTRY32 entry = new() { dwSize = (uint)sizeof(MODULEENTRY32) };
            if (!PInvoke.Module32First(snapshot, ref entry))
            {
                int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                if (error is 18 or 299)
                {
                    return ProcessModuleQuery.Ok(opened.StartTime, partial: error == 299, truncated: false, skipped: 0, []);
                }

                return error == 5
                    ? ProcessModuleQuery.Fail("access_denied", "The process modules could not be read.")
                    : ProcessModuleQuery.Fail("process_exited", "The process exited while its modules were being read.");
            }

            do
            {
                if (modules.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                if (!TryRecord(entry, addresses, out LoadedModuleRecord? record))
                {
                    skipped++;
                    partial = true;
                    continue;
                }

                modules.Add(record!);
            }
            while (Next(snapshot, ref entry, ref partial, ref skipped));
            }
        }
        catch (Win32Exception)
        {
            return ProcessModuleQuery.Fail("access_denied", "The process modules could not be read.");
        }

        return ProcessModuleQuery.Ok(opened.StartTime, partial, truncated, skipped, modules);
    }

    private static unsafe bool Next(SafeFileHandle snapshot, ref MODULEENTRY32 entry, ref bool partial, ref int skipped)
    {
        if (PInvoke.Module32Next(snapshot, ref entry))
        {
            return true;
        }

        int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        if (error == 18)
        {
            return false;
        }

        partial = true;
        skipped++;
        return false;
    }

    private static unsafe bool TryRecord(MODULEENTRY32 entry, bool addresses, out LoadedModuleRecord? record)
    {
        record = null;
        string name = entry.szModule.ToString().TrimEnd('\0').Trim();
        string path = entry.szExePath.ToString().TrimEnd('\0').Trim();
        if (name.Length == 0 && path.Length == 0)
        {
            return false;
        }

        if (name.Length == 0)
        {
            name = Path.GetFileName(path);
        }

        ulong? baseAddress = addresses && entry.modBaseAddr != null ? (ulong)entry.modBaseAddr : null;
        (string? architecture, string? fileVersion, string? productVersion) = PeImage.TryMetadata(path);
        record = new LoadedModuleRecord(
            name,
            baseAddress,
            entry.modBaseSize == 0 ? null : (int)entry.modBaseSize,
            architecture,
            fileVersion,
            productVersion,
            string.IsNullOrWhiteSpace(path) ? null : path);
        return true;
    }

    private static bool CanReadMemory(int pid)
    {
        HANDLE handle = PInvoke.OpenProcess(Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ, false, (uint)pid);
        if (handle.IsNull)
        {
            return false;
        }

        using SafeFileHandle owned = new(handle, ownsHandle: true);
        return true;
    }

    private static ProcessQuery Open(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            try
            {
                if (process.HasExited)
                {
                    return ProcessQuery.Fail("process_exited", "The process exited before its modules could be read.");
                }
            }
            catch (InvalidOperationException)
            {
                return ProcessQuery.Fail("process_exited", "The process exited before its modules could be read.");
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

            return ProcessQuery.Ok(start);
        }
        catch (ArgumentException)
        {
            return ProcessQuery.Fail("process_not_found", "The process is not running.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return ProcessQuery.Fail("access_denied", "The process could not be opened.");
        }
        catch (InvalidOperationException)
        {
            return ProcessQuery.Fail("process_exited", "The process exited before its modules could be read.");
        }
    }

    private readonly record struct ProcessQuery(bool Success, string? ErrorCode, string? Error, DateTimeOffset? StartTime)
    {
        public static ProcessQuery Ok(DateTimeOffset? startTime) => new(true, null, null, startTime);

        public static ProcessQuery Fail(string code, string error) => new(false, code, error, null);
    }
}
