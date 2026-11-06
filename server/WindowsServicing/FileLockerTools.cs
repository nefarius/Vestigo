using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.WindowsServicing;

public sealed record FileLocker(int Pid, DateTimeOffset? StartTime, string Application, string Service, uint SessionId);
public sealed record FileLockers(IReadOnlyList<FileLocker> Processes, uint RebootReasons, bool Truncated,
    string Limitation = "Restart Manager is best-effort; kernel references and all causes of replacement failure are not enumerated.");

public interface IFileLockerSource { FileLockers Query(string path, int limit); }

[McpServerToolType]
public sealed class FileLockerTools(RootPolicy roots, IFileLockerSource source, NativeDiagnosticWorker worker, VestigoOptions options)
{
    [McpServerTool(Name = "get_file_lockers", ReadOnly = true, Destructive = false)]
    [Description("Best-effort Restart Manager query for one existing file in a Read root. Returns process identities/start times, application/service names and reboot-reason flags. Never shuts down/restarts processes. Not a complete kernel handle or minifilter inventory; an empty list does not prove a file can be replaced.")]
    public Task<OperationResult<FileLockers>> GetFileLockers(
        [Description("File path in a configured Read root, absolute or root:relative.")] string path,
        [Description("Maximum processes, clamped to WindowsServicing:MaxResults.")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        PathCheck check = roots.ResolveExisting(path, RootAccess.Read);
        if (!check.Success) return Task.FromResult(OperationResult<FileLockers>.Fail(check.ErrorCode!, check.Error!));
        if (!File.Exists(check.FullPath)) return Task.FromResult(OperationResult<FileLockers>.Fail("not_a_file", "An existing file is required."));
        return worker.Run(() => source.Query(check.FullPath!, Math.Clamp(limit, 1, options.WindowsServicing.MaxResults)),
            options.WindowsServicing.NativeCallTimeout, cancellationToken);
    }
}

public sealed class RestartManagerSource : IFileLockerSource
{
    public FileLockers Query(string path, int limit)
    {
        Check(RmStartSession(out uint session, 0, new StringBuilder(33)));
        try
        {
            Check(RmRegisterResources(session, 1, [path], 0, 0, 0, 0));
            uint count = 0, reasons = 0;
            int error = RmGetList(session, out uint needed, ref count, null, ref reasons);
            for (int attempt = 0; error == 234 && attempt < 3; attempt++)
            {
                // Native list must fit; never allocate in proportion to unbounded external counts.
                if (needed > 4096) return new([], reasons, true);
                ProcessInfo[] processes = new ProcessInfo[needed];
                count = needed;
                error = RmGetList(session, out needed, ref count, processes, ref reasons);
                if (error == 0)
                    return new(processes.Take((int)Math.Min(count, (uint)limit)).Select(p => new FileLocker(
                        checked((int)p.Process.Pid), StartTime(p.Process.Start), p.Application, p.Service, p.SessionId)).ToArray(),
                        reasons, count > limit);
            }
            if (error == 234) return new([], reasons, true);
            Check(error);
            return new([], reasons, false);
        }
        finally { RmEndSession(session); }
    }

    private static DateTimeOffset? StartTime(FILETIME time)
    {
        long value = ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
        try { return DateTimeOffset.FromFileTime(value); } catch (ArgumentOutOfRangeException) { return null; }
    }
    private static void Check(int error) { if (error != 0) throw new Win32Exception(error); }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess { public uint Pid; public FILETIME Start; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Application;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
        public int ApplicationType;
        public uint Status, SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[] files,
        uint applicationCount, nint applications, uint serviceCount, nint services);
    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? processes, ref uint reasons);
    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmEndSession(uint session);
}
