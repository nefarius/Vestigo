using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Threading;

namespace Vestigo.Diagnostics;

/// <summary>
///     Writes a minidump with <c>MiniDumpWriteDump</c> in the service process. A full dump contains process memory.
///     The file is deleted when the write fails, is cancelled, or exceeds the byte cap. Nothing is uploaded.
/// </summary>
internal sealed class MiniDumpWriter : IProcessDumpWriter
{
    public OperationResult<ProcessDumpTarget> Inspect(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<ProcessDumpTarget>.Fail("windows_required", "Process dumps require Windows.");
        }

        if (pid <= 0)
        {
            return OperationResult<ProcessDumpTarget>.Fail("invalid_request", "pid must be a positive process id.");
        }

        if (pid == Environment.ProcessId || pid == 4)
        {
            return OperationResult<ProcessDumpTarget>.Ok(new ProcessDumpTarget(pid, "", null, true));
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            string name;
            DateTimeOffset? start;
            try
            {
                name = process.ProcessName;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return OperationResult<ProcessDumpTarget>.Fail("access_denied", "The process could not be queried.");
            }

            try
            {
                start = process.StartTime;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                start = null;
            }

            bool refused = ProcessDumpPolicy.IsRefused(name) || IsProtected(pid);
            return OperationResult<ProcessDumpTarget>.Ok(new ProcessDumpTarget(pid, name, start, refused));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return OperationResult<ProcessDumpTarget>.Fail("process_not_found", "The process is not running.");
        }
        catch (Win32Exception)
        {
            return OperationResult<ProcessDumpTarget>.Fail("access_denied", "The process could not be queried.");
        }
    }

    public Task<OperationResult<long>> WriteAsync(ProcessDumpRequest request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(OperationResult<long>.Fail("windows_required", "Process dumps require Windows."));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(OperationResult<long>.Fail("cancelled", "The dump was cancelled."));
        }

        OperationResult<ProcessDumpTarget> inspected = Inspect(request.Pid);
        if (!inspected.Success)
        {
            return Task.FromResult(inspected.ErrorCode == "process_not_found"
                ? OperationResult<long>.Fail("process_exited", "The process exited before the dump was written.")
                : OperationResult<long>.Fail(inspected.ErrorCode!, inspected.Error!));
        }

        ProcessDumpTarget target = inspected.Value!;
        if (target.Protected)
        {
            return Task.FromResult(OperationResult<long>.Fail("protected_process", "This process is protected or on the refusal list."));
        }

        if (target.StartTime is null || !WindowsProcessModuleSource.StartUnchanged(request.ExpectedStart, target.StartTime))
        {
            return Task.FromResult(OperationResult<long>.Fail("process_identity_mismatch", "The process start time does not match expectedStartTime. The pid may have been reused."));
        }

        return Task.FromResult(Write(request, cancellationToken));
    }

    private static OperationResult<long> Write(ProcessDumpRequest request, CancellationToken cancellationToken)
    {
        PROCESS_ACCESS_RIGHTS access = PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION
            | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ
            | PROCESS_ACCESS_RIGHTS.PROCESS_DUP_HANDLE;
        HANDLE process = PInvoke.OpenProcess(access, false, (uint)request.Pid);
        if (process.IsNull)
        {
            int error = Marshal.GetLastPInvokeError();
            return OperationResult<long>.Fail(error == 5 ? "protected_process" : "access_denied", "The process could not be opened for a dump.");
        }

        using SafeFileHandle owned = new(process, ownsHandle: true);
        using SafeFileHandle file = CreateDump(request.FullPath, out int createError);
        if (file.IsInvalid)
        {
            return OperationResult<long>.Fail(createError == 80 ? "already_exists" : "dump_failed", createError == 80
                ? "The output file already exists."
                : "The dump file could not be created.");
        }

        uint flags = request.DumpType.Equals("full", StringComparison.OrdinalIgnoreCase)
            ? MiniDumpWithFullMemory | MiniDumpIgnoreInaccessibleMemory
            : MiniDumpWithIndirectlyReferencedMemory
                | MiniDumpWithThreadInfo
                | MiniDumpWithUnloadedModules
                | MiniDumpWithProcessThreadData
                | MiniDumpIgnoreInaccessibleMemory;
        bool wrote = MiniDumpWriteDump(
            owned.DangerousGetHandle(),
            (uint)request.Pid,
            file.DangerousGetHandle(),
            flags,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        GC.KeepAlive(owned);
        GC.KeepAlive(file);
        if (!wrote)
        {
            int error = Marshal.GetLastPInvokeError();
            Delete(request.FullPath);
            return OperationResult<long>.Fail(CodeFor(error), error == 299
                ? "The process memory could not be read completely. The partial file was deleted."
                : $"The dump could not be written ({error}).");
        }

        long length;
        try
        {
            length = new FileInfo(request.FullPath).Length;
        }
        catch (IOException)
        {
            Delete(request.FullPath);
            return OperationResult<long>.Fail("dump_failed", "The dump length could not be read. The file was deleted.");
        }

        if (length > request.MaxBytes)
        {
            Delete(request.FullPath);
            return OperationResult<long>.Fail("dump_too_large", "The dump exceeded Vestigo:Diagnostics:MaxDumpBytes and was deleted.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            Delete(request.FullPath);
            return OperationResult<long>.Fail("cancelled", "The dump was cancelled and the file was deleted.");
        }

        return OperationResult<long>.Ok(length);
    }

    private static unsafe bool IsProtected(int pid)
    {
        HANDLE handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle.IsNull)
        {
            return Marshal.GetLastPInvokeError() == 5;
        }

        using SafeFileHandle owned = new(handle, ownsHandle: true);
        PROCESS_PROTECTION_LEVEL_INFORMATION information = default;
        if (!PInvoke.GetProcessInformation(
                handle,
                PROCESS_INFORMATION_CLASS.ProcessProtectionLevelInfo,
                &information,
                (uint)sizeof(PROCESS_PROTECTION_LEVEL_INFORMATION)))
        {
            return Marshal.GetLastPInvokeError() == 5;
        }

        return information.ProtectionLevel != PROCESS_PROTECTION_LEVEL.PROTECTION_LEVEL_NONE;
    }

    private static unsafe SafeFileHandle CreateDump(string path, out int error)
    {
        SafeFileHandle handle = PInvoke.CreateFile(
            path,
            (uint)FILE_ACCESS_RIGHTS.FILE_GENERIC_WRITE,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null,
            FILE_CREATION_DISPOSITION.CREATE_NEW,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
            null);
        error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        return handle;
    }

    private const uint MiniDumpWithFullMemory = 0x00000002;
    private const uint MiniDumpWithUnloadedModules = 0x00000020;
    private const uint MiniDumpWithIndirectlyReferencedMemory = 0x00000040;
    private const uint MiniDumpWithProcessThreadData = 0x00000100;
    private const uint MiniDumpWithThreadInfo = 0x00001000;
    private const uint MiniDumpIgnoreInaccessibleMemory = 0x00020000;

    [DllImport("dbghelp.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool MiniDumpWriteDump(
        IntPtr process,
        uint processId,
        IntPtr file,
        uint dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    private static string CodeFor(int error) => error switch
    {
        5 => "access_denied",
        299 => "partial_copy",
        _ => "dump_failed"
    };

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The caller still reports the dump failure. The partial file is left only when delete itself fails.
        }
    }

}
