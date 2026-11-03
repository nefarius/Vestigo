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
///     The file is deleted when the write fails, is cancelled, or exceeds the byte cap.
///     A delete that fails keeps the original dump error and says the partial file remains. Nothing is uploaded.
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
        if (!CreationMatches(owned, request.ExpectedStart))
        {
            return OperationResult<long>.Fail("process_identity_mismatch", "The process start time does not match expectedStartTime. The pid may have been reused.");
        }

        using SafeFileHandle file = CreateDump(request, out string? confine, out int createError);
        if (confine is not null)
        {
            return OperationResult<long>.Fail("invalid_path", confine);
        }

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
        DumpLimit limit = new(file, request.MaxBytes, cancellationToken);
        MiniDumpCallback callback = OnDumpCallback;
        GCHandle state = GCHandle.Alloc(limit);
        bool wrote;
        try
        {
            wrote = WriteDump(owned, (uint)request.Pid, file, flags, new MiniDumpCallbackInformation
            {
                CallbackRoutine = Marshal.GetFunctionPointerForDelegate(callback),
                CallbackParam = GCHandle.ToIntPtr(state)
            });
        }
        finally
        {
            state.Free();
            GC.KeepAlive(callback);
            GC.KeepAlive(owned);
            GC.KeepAlive(file);
        }

        if (limit.TooLarge || limit.Cancelled || limit.LengthFailed)
        {
            if (limit.TooLarge)
            {
                return FailAndRemove(
                    file,
                    "dump_too_large",
                    "The dump exceeded Vestigo:Diagnostics:MaxDumpBytes and was deleted.",
                    "The dump exceeded Vestigo:Diagnostics:MaxDumpBytes. " + PartialFileRemains);
            }

            return limit.Cancelled
                ? FailAndRemove(
                    file,
                    "cancelled",
                    "The dump was cancelled and the file was deleted.",
                    "The dump was cancelled. " + PartialFileRemains)
                : FailAndRemove(
                    file,
                    "dump_failed",
                    "The dump length could not be read. The file was deleted.",
                    "The dump length could not be read. " + PartialFileRemains);
        }

        if (!wrote)
        {
            int error = Marshal.GetLastPInvokeError();
            return error == 299
                ? FailAndRemove(
                    file,
                    "partial_copy",
                    "The process memory could not be read completely. The partial file was deleted.",
                    "The process memory could not be read completely. " + PartialFileRemains)
                : FailAndRemove(
                    file,
                    CodeFor(error),
                    $"The dump could not be written ({error}).",
                    $"The dump could not be written ({error}). " + PartialFileRemains);
        }

        long length;
        try
        {
            length = RandomAccess.GetLength(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return FailAndRemove(
                file,
                "dump_failed",
                "The dump length could not be read. The file was deleted.",
                "The dump length could not be read. " + PartialFileRemains);
        }

        if (length > request.MaxBytes)
        {
            return FailAndRemove(
                file,
                "dump_too_large",
                "The dump exceeded Vestigo:Diagnostics:MaxDumpBytes and was deleted.",
                "The dump exceeded Vestigo:Diagnostics:MaxDumpBytes. " + PartialFileRemains);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return FailAndRemove(
                file,
                "cancelled",
                "The dump was cancelled and the file was deleted.",
                "The dump was cancelled. " + PartialFileRemains);
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

    private static SafeFileHandle CreateDump(ProcessDumpRequest request, out string? confine, out int error)
    {
        confine = null;
        error = 0;
        string? directory = Path.GetDirectoryName(request.FullPath);
        string name = Path.GetFileName(request.FullPath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name) || name.Contains('\\') || name.Contains('/'))
        {
            confine = "The output directory is not inside the Write root.";
            return new SafeFileHandle(IntPtr.Zero, false);
        }

        if (WerDirectory.Open(request.RootPath, out WerDirectory.OpenedDirectory root) != WerDirectory.OpenStatus.Opened)
        {
            confine = "The output directory is not inside the Write root.";
            return new SafeFileHandle(IntPtr.Zero, false);
        }

        using (root)
        {
            switch (WerDirectory.Open(directory, out WerDirectory.OpenedDirectory opened))
            {
                case WerDirectory.OpenStatus.Missing:
                    confine = "The output directory does not exist.";
                    return new SafeFileHandle(IntPtr.Zero, false);
                case WerDirectory.OpenStatus.Failed or WerDirectory.OpenStatus.Reparse:
                    confine = "The output directory is not inside the Write root.";
                    return new SafeFileHandle(IntPtr.Zero, false);
            }

            using (opened)
            {
                if (!WerDirectory.SamePath(opened.FinalPath, root.FinalPath) && !WerDirectory.IsUnder(opened.FinalPath, root.FinalPath))
                {
                    confine = "The output directory is not inside the Write root.";
                    return new SafeFileHandle(IntPtr.Zero, false);
                }

                return DumpFileBinder.Create(opened.Handle, name, out error);
            }
        }
    }

    private const int CancelCallback = 6;

    /// <summary><c>MINIDUMP_CALLBACK_INPUT</c> is packed to 4 bytes: <c>ProcessId</c>, then <c>HANDLE</c>, then <c>CallbackType</c>.</summary>
    private static readonly int CallbackTypeOffset = 4 + IntPtr.Size;

    [StructLayout(LayoutKind.Sequential)]
    private struct MiniDumpCallbackInformation
    {
        public IntPtr CallbackRoutine;
        public IntPtr CallbackParam;
    }

    private sealed class DumpLimit(SafeFileHandle file, long maxBytes, CancellationToken cancellation)
    {
        public bool TooLarge { get; private set; }

        public bool Cancelled { get; private set; }

        public bool LengthFailed { get; private set; }

        public bool Stop => TooLarge || Cancelled || LengthFailed;

        public void Observe()
        {
            if (Stop)
            {
                return;
            }

            try
            {
                if (RandomAccess.GetLength(file) > maxBytes)
                {
                    TooLarge = true;
                }
                else if (cancellation.IsCancellationRequested)
                {
                    Cancelled = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ObjectDisposedException)
            {
                LengthFailed = true;
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MiniDumpCallback(IntPtr parameter, IntPtr input, IntPtr output);

    private static int OnDumpCallback(IntPtr parameter, IntPtr input, IntPtr output)
    {
        if (GCHandle.FromIntPtr(parameter).Target is DumpLimit limit)
        {
            limit.Observe();
            if (limit.Stop && input != IntPtr.Zero && output != IntPtr.Zero && Marshal.ReadInt32(input, CallbackTypeOffset) == CancelCallback)
            {
                Marshal.WriteInt32(output, 4, 1);
            }
        }

        return 1;
    }

    private static unsafe bool WriteDump(SafeFileHandle process, uint pid, SafeFileHandle file, uint flags, MiniDumpCallbackInformation information) =>
        MiniDumpWriteDump(
            process.DangerousGetHandle(),
            pid,
            file.DangerousGetHandle(),
            flags,
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr)(&information));

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

    /// <summary>Sentence added when the partial dump is still on disk. Job output reports <c>cleanup_failed</c> beside the original code.</summary>
    internal const string PartialFileRemains = "The partial file could not be deleted.";

    private static OperationResult<long> FailAndRemove(SafeFileHandle file, string code, string deleted, string remained) =>
        DumpFileBinder.TryDelete(file)
            ? OperationResult<long>.Fail(code, deleted)
            : OperationResult<long>.Fail(code, remained);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    private static bool CreationMatches(SafeFileHandle process, DateTimeOffset expected)
    {
        if (!GetProcessTimes(process.DangerousGetHandle(), out long creation, out _, out _, out _))
        {
            return WindowsProcessModuleSource.StartUnchanged(expected, null);
        }

        GC.KeepAlive(process);
        try
        {
            DateTimeOffset started = new(DateTime.FromFileTimeUtc(creation), TimeSpan.Zero);
            return WindowsProcessModuleSource.StartUnchanged(expected, started);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

}
