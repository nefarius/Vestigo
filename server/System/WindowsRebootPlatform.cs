using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Shutdown;

namespace Vestigo.SystemInfo;

/// <summary>
///     Local <c>InitiateShutdown</c> / <c>AbortSystemShutdown</c>. The machine name is always null.
///     Privilege enablement checks <c>ERROR_NOT_ALL_ASSIGNED</c> after a successful <c>AdjustTokenPrivileges</c>.
/// </summary>
public sealed class WindowsRebootPlatform : IRebootPlatform
{
    /// <inheritdoc />
    public RebootPlatformOutcome InitiateReboot(uint delaySeconds, bool force, bool planned, string? message)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new RebootPlatformOutcome(false, 1, "windows_required");
        }

        RebootPlatformOutcome privilege = EnableShutdownPrivilege();
        if (!privilege.Success)
        {
            return privilege;
        }

        uint error = CallInitiateShutdown(delaySeconds, force, planned, message);
        return error == 0
            ? new RebootPlatformOutcome(true, 0, "initiate")
            : new RebootPlatformOutcome(false, (int)error, "initiate");
    }

    /// <inheritdoc />
    public RebootPlatformOutcome AbortReboot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new RebootPlatformOutcome(false, 1, "windows_required");
        }

        RebootPlatformOutcome privilege = EnableShutdownPrivilege();
        if (!privilege.Success)
        {
            return privilege;
        }

        if (PInvoke.AbortSystemShutdown(default))
        {
            return new RebootPlatformOutcome(true, 0, "abort");
        }

        return new RebootPlatformOutcome(false, Marshal.GetLastWin32Error(), "abort");
    }

    private static unsafe uint CallInitiateShutdown(uint delaySeconds, bool force, bool planned, string? message)
    {
        SHUTDOWN_FLAGS flags = (SHUTDOWN_FLAGS)RebootPolicy.Flags(delaySeconds, force);
        SHUTDOWN_REASON reason = (SHUTDOWN_REASON)RebootPolicy.Reason(planned);
        if (string.IsNullOrEmpty(message))
        {
            return PInvoke.InitiateShutdown(null, default, delaySeconds, flags, reason);
        }

        fixed (char* pointer = message)
        {
            return PInvoke.InitiateShutdown(null, new PWSTR(pointer), delaySeconds, flags, reason);
        }
    }

    private static unsafe RebootPlatformOutcome EnableShutdownPrivilege()
    {
        using SafeFileHandle process = new(PInvoke.GetCurrentProcess(), ownsHandle: false);
        if (!PInvoke.OpenProcessToken(
                process,
                TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES | TOKEN_ACCESS_MASK.TOKEN_QUERY,
                out SafeFileHandle token))
        {
            int openError = Marshal.GetLastWin32Error();
            token.Dispose();
            return new RebootPlatformOutcome(false, openError, "privilege");
        }

        using (token)
        {
            if (!PInvoke.LookupPrivilegeValue(null, PInvoke.SE_SHUTDOWN_NAME, out LUID luid))
            {
                return new RebootPlatformOutcome(false, Marshal.GetLastWin32Error(), "privilege");
            }

            TOKEN_PRIVILEGES state = default;
            state.PrivilegeCount = 1;
            state.Privileges[0].Luid = luid;
            state.Privileges[0].Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED;
            HANDLE handle = (HANDLE)token.DangerousGetHandle();
            if (!PInvoke.AdjustTokenPrivileges(handle, false, &state, 0, null, null))
            {
                return new RebootPlatformOutcome(false, Marshal.GetLastWin32Error(), "privilege");
            }

            int assigned = Marshal.GetLastWin32Error();
            return assigned == RebootPolicy.ErrorNotAllAssigned
                ? new RebootPlatformOutcome(false, assigned, "privilege")
                : new RebootPlatformOutcome(true, 0, "privilege");
        }
    }
}
