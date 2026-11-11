using Microsoft.Win32;

using Windows.Win32;
using Windows.Win32.System.WindowsProgramming;

namespace Vestigo.Diagnostics;

/// <summary>Documented HVCI scenario value and Windows lockdown mode. Neither value is a load-failure cause.</summary>
internal interface ICodeIntegrityProbe
{
    CodeIntegrityPresence Read();
}

/// <summary>HVCI <c>Enabled</c> and <c>WldpQueryWindowsLockdownMode</c>. Unknown numbers stay unnamed.</summary>
internal sealed record CodeIntegrityPresence(
    int? HvciEnabledValue,
    bool? HvciEnabled,
    string? HvciNote,
    int? WindowsLockdownMode,
    string? WindowsLockdownModeName,
    string? LockdownNote);

/// <summary>
///     Win32 metadata includes <c>SYSTEM_CODEINTEGRITY_INFORMATION</c> but not <c>NtQuerySystemInformation</c>,
///     so HVCI is the documented DeviceGuard scenario value and lockdown mode is <c>WldpQueryWindowsLockdownMode</c>
///     (S mode). This does not inventory WDAC policy files.
/// </summary>
internal sealed class WindowsCodeIntegrity : ICodeIntegrityProbe
{
    private const string HvciKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";

    public CodeIntegrityPresence Read()
    {
        int? hvci = null;
        bool? enabled = null;
        string? hvciNote = null;
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(HvciKey, writable: false);
            object? value = key?.GetValue("Enabled");
            if (value is int number)
            {
                hvci = number;
                enabled = CodeIntegrityProjection.HvciEnabled(number);
            }
            else
            {
                hvciNote = key is null || value is null ? "not_found" : "unexpected_type";
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            hvciNote = "unavailable";
        }

        int? mode = null;
        string? modeName = null;
        string? lockdownNote = null;
        try
        {
            Windows.Win32.Foundation.HRESULT result = PInvoke.WldpQueryWindowsLockdownMode(out WLDP_WINDOWS_LOCKDOWN_MODE lockdown);
            if (result.Failed)
            {
                lockdownNote = "0x" + result.Value.ToString("X8");
            }
            else
            {
                mode = (int)lockdown;
                modeName = CodeIntegrityProjection.LockdownName(mode.Value);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            lockdownNote = "unavailable";
        }

        return new CodeIntegrityPresence(hvci, enabled, hvciNote, mode, modeName, lockdownNote);
    }
}
