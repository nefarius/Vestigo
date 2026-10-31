using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

using Microsoft.Win32.SafeHandles;

using Vestigo.Jobs;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.JobObjects;
using Windows.Win32.System.Threading;

namespace Vestigo.Processes;

/// <summary>
///     Starts a process as the active console user on <c>winsta0\default</c>. The approach (console session token,
///     optional linked elevated token, environment block, inheritable spool handles, NUL stdin) is ported from
///     SignRelay's <c>InteractiveUserProcessLauncher</c> (MIT, same author) using CsWin32 instead of hand-written
///     P/Invoke. A job object with KILL_ON_JOB_CLOSE provides tree kill on cancellation.
/// </summary>
internal static class InteractiveProcessRunner
{
    private const uint NoSession = 0xFFFFFFFF;
    private const uint ErrorNoToken = 1008;
    private const int TokenElevationTypeDefault = 1;
    private const int TokenElevationTypeFull = 2;
    private const int TokenElevationTypeLimited = 3;
    private const uint ProcThreadAttributeHandleList = 0x00020002;

    public static OperationResult<bool> CheckAvailability(bool elevated)
    {
        try
        {
            using SafeFileHandle token = OpenUserToken(elevated);
            return OperationResult<bool>.Ok(true);
        }
        catch (ProcessLaunchException ex)
        {
            return OperationResult<bool>.Fail(ex.Code, ex.Message[(ex.Code.Length + 2)..]);
        }
    }

    public static async Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken)
    {
        using SafeFileHandle token = OpenUserToken(request.Elevated);
        using SafeFileHandle spool = OpenSpool(output.OutputPath);
        using SafeFileHandle nul = OpenNul();
        using SafeFileHandle job = CreateKillOnCloseJob();

        Dictionary<string, string> environment = ReadUserEnvironment(token);
        char[] block = EnvironmentBlock.Build(environment, request.Environment);
        PROCESS_INFORMATION info = StartSuspended(token, request, spool, nul, block);

        using SafeFileHandle process = new(info.hProcess, ownsHandle: true);
        using SafeFileHandle thread = new(info.hThread, ownsHandle: true);
        try
        {
            if (!PInvoke.AssignProcessToJobObject(job, process))
            {
                int error = Marshal.GetLastWin32Error();
                PInvoke.TerminateProcess(process, 1);
                throw new ProcessLaunchException("start_failed", "Could not assign the process to a job object: " + new Win32Exception(error).Message);
            }

            if (PInvoke.ResumeThread(thread) == uint.MaxValue)
            {
                int error = Marshal.GetLastWin32Error();
                PInvoke.TerminateJobObject(job, 1);
                throw new ProcessLaunchException("start_failed", new Win32Exception(error).Message);
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(() => PInvoke.TerminateJobObject(job, 1));
            using CancellationTokenSource monitorStop = new();
            Task monitor = MonitorOutputLimitAsync(output, job, monitorStop.Token);
            await Task.Run(() => PInvoke.WaitForSingleObject(process, PInvoke.INFINITE), CancellationToken.None).ConfigureAwait(false);
            await monitorStop.CancelAsync().ConfigureAwait(false);
            await monitor.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return PInvoke.GetExitCodeProcess(process, out uint exitCode) ? unchecked((int)exitCode) : -1;
        }
        finally
        {
            // KILL_ON_JOB_CLOSE: closing the job (by disposing `job`) ends any descendants still running.
            PInvoke.TerminateJobObject(job, 1);
        }
    }

    /// <summary>
    ///     The child writes the spool file directly, bypassing <see cref="JobContext.Write" />, so poll its size and
    ///     end the whole job tree once it passes the output limit.
    /// </summary>
    private static async Task MonitorOutputLimitAsync(JobContext output, SafeFileHandle job, CancellationToken stop)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                if (output.LimitExceeded)
                {
                    output.WriteTruncationMarker();
                    PInvoke.TerminateJobObject(job, 1);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The process ended first.
        }
    }

    private static unsafe PROCESS_INFORMATION StartSuspended(
        SafeFileHandle token,
        LaunchRequest request,
        SafeFileHandle spool,
        SafeFileHandle nul,
        char[] block)
    {
        char[] commandLine = (CommandLineBuilder.Build(request.Executable, request.Arguments) + '\0').ToCharArray();
        HANDLE spoolHandle = (HANDLE)spool.DangerousGetHandle();
        HANDLE nulHandle = (HANDLE)nul.DangerousGetHandle();
        HANDLE* inherited = stackalloc HANDLE[2] { spoolHandle, nulHandle };

        // Only these two handles are inherited, so concurrent launches cannot leak each other's spool files.
        nuint attributeSize = 0;
        PInvoke.InitializeProcThreadAttributeList(default, 1, 0, &attributeSize);
        byte[] attributeStorage = new byte[attributeSize];
        PROCESS_INFORMATION info;
        fixed (char* environmentPointer = block)
        fixed (char* commandLinePointer = commandLine)
        fixed (char* desktopPointer = @"winsta0\default")
        fixed (char* workingDirectory = request.WorkingDirectory)
        fixed (byte* attributeList = attributeStorage)
        {
            LPPROC_THREAD_ATTRIBUTE_LIST list = new(attributeList);
            if (!PInvoke.InitializeProcThreadAttributeList(list, 1, 0, &attributeSize))
            {
                throw new ProcessLaunchException("start_failed", new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }

            try
            {
                if (!PInvoke.UpdateProcThreadAttribute(
                        list,
                        0,
                        ProcThreadAttributeHandleList,
                        inherited,
                        (nuint)(sizeof(HANDLE) * 2),
                        null,
                        null))
                {
                    throw new ProcessLaunchException("start_failed", new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }

                STARTUPINFOEXW startup = default;
                startup.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);
                startup.StartupInfo.lpDesktop = desktopPointer;
                startup.StartupInfo.dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES;
                startup.StartupInfo.hStdInput = nulHandle;
                startup.StartupInfo.hStdOutput = spoolHandle;
                startup.StartupInfo.hStdError = spoolHandle;
                startup.lpAttributeList = list;

                PROCESS_CREATION_FLAGS flags = PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT
                                               | PROCESS_CREATION_FLAGS.CREATE_SUSPENDED
                                               | PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW
                                               | PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT;
                fixed (char* application = request.Executable)
                {
                    if (!PInvoke.CreateProcessAsUser(
                            (HANDLE)token.DangerousGetHandle(),
                            new PCWSTR(application),
                            new PWSTR(commandLinePointer),
                            null,
                            null,
                            true,
                            flags,
                            environmentPointer,
                            new PCWSTR(workingDirectory),
                            (STARTUPINFOW*)&startup,
                            &info))
                    {
                        throw new ProcessLaunchException("start_failed", new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    }
                }
            }
            finally
            {
                PInvoke.DeleteProcThreadAttributeList(list);
            }
        }

        return info;
    }
    internal static unsafe SafeFileHandle OpenUserToken(bool elevated)
    {
        uint session = PInvoke.WTSGetActiveConsoleSessionId();
        if (session is NoSession or 0)
        {
            throw new ProcessLaunchException("no_interactive_session", "No user is logged on at the console.");
        }

        HANDLE rawToken = default;
        if (!PInvoke.WTSQueryUserToken(session, &rawToken))
        {
            int error = Marshal.GetLastWin32Error();
            throw error == ErrorNoToken
                ? new ProcessLaunchException("no_interactive_session", "No user is logged on at the console.")
                : new ProcessLaunchException(
                    "token_unavailable",
                    "Could not get the console user token (the service needs SYSTEM): " + new Win32Exception(error).Message);
        }

        SafeFileHandle token = new(rawToken, ownsHandle: true);
        if (!elevated)
        {
            return token;
        }

        try
        {
            return ElevateToken(token);
        }
        catch
        {
            token.Dispose();
            throw;
        }
    }

    private static unsafe SafeFileHandle ElevateToken(SafeFileHandle token)
    {
        int type = 0;
        uint length;
        if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevationType, &type, sizeof(int), out length))
        {
            throw new ProcessLaunchException("elevation_unavailable", new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        switch (type)
        {
            case TokenElevationTypeFull:
                return token;
            case TokenElevationTypeDefault:
            {
                using WindowsIdentity identity = new(token.DangerousGetHandle());
                if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                {
                    return token;
                }

                throw new ProcessLaunchException("elevation_unavailable", "The console user is not an administrator.");
            }
            case TokenElevationTypeLimited:
            {
                TOKEN_LINKED_TOKEN linked = default;
                if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenLinkedToken, &linked, (uint)sizeof(TOKEN_LINKED_TOKEN), out length))
                {
                    throw new ProcessLaunchException("elevation_unavailable", new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }

                using SafeFileHandle linkedToken = new(linked.LinkedToken, ownsHandle: true);
                if (!PInvoke.DuplicateTokenEx(
                        linkedToken,
                        TOKEN_ACCESS_MASK.TOKEN_ALL_ACCESS,
                        null,
                        SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                        TOKEN_TYPE.TokenPrimary,
                        out SafeFileHandle primary))
                {
                    throw new ProcessLaunchException("elevation_unavailable", new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }

                token.Dispose();
                return primary;
            }
            default:
                throw new ProcessLaunchException("elevation_unavailable", $"Unexpected token elevation type {type}.");
        }
    }

    private static unsafe Dictionary<string, string> ReadUserEnvironment(SafeFileHandle token)
    {
        void* block;
        if (!PInvoke.CreateEnvironmentBlock(out block, token, false))
        {
            throw new ProcessLaunchException("start_failed", "CreateEnvironmentBlock failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        try
        {
            // The block is terminated by an empty entry; scan to the double NUL to find its length.
            char* start = (char*)block;
            int length = 0;
            while (start[length] != '\0' || start[length + 1] != '\0')
            {
                length++;
            }

            return EnvironmentBlock.Parse(new ReadOnlySpan<char>(start, length + 2));
        }
        finally
        {
            PInvoke.DestroyEnvironmentBlock(block);
        }
    }

    private static unsafe SafeFileHandle OpenSpool(string path)
    {
        SECURITY_ATTRIBUTES attributes = new() { nLength = (uint)sizeof(SECURITY_ATTRIBUTES), bInheritHandle = true };
        // FILE_APPEND_DATA only: every write lands at the end, next to JobContext.Write appends.
        SafeFileHandle handle = PInvoke.CreateFile(
            path,
            (uint)FILE_ACCESS_RIGHTS.FILE_APPEND_DATA,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            attributes,
            FILE_CREATION_DISPOSITION.OPEN_ALWAYS,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
            null);
        if (handle.IsInvalid)
        {
            throw new ProcessLaunchException("start_failed", "Could not open the job spool: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        return handle;
    }

    private static unsafe SafeFileHandle OpenNul()
    {
        SECURITY_ATTRIBUTES attributes = new() { nLength = (uint)sizeof(SECURITY_ATTRIBUTES), bInheritHandle = true };
        SafeFileHandle handle = PInvoke.CreateFile(
            "NUL",
            (uint)(FILE_ACCESS_RIGHTS.FILE_GENERIC_READ | FILE_ACCESS_RIGHTS.FILE_GENERIC_WRITE),
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
            attributes,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
            null);
        if (handle.IsInvalid)
        {
            throw new ProcessLaunchException("start_failed", "Could not open NUL: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        return handle;
    }

    private static unsafe SafeFileHandle CreateKillOnCloseJob()
    {
        SafeFileHandle job = PInvoke.CreateJobObject(null, (string?)null);
        if (job.IsInvalid)
        {
            throw new ProcessLaunchException("start_failed", "Could not create a job object: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = default;
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!PInvoke.SetInformationJobObject(
                (HANDLE)job.DangerousGetHandle(),
                JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                &limits,
                (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
        {
            int error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new ProcessLaunchException("start_failed", "Could not configure the job object: " + new Win32Exception(error).Message);
        }

        return job;
    }
}
