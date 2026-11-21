using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Security.Authentication.Identity;

namespace Vestigo.SystemInfo;

/// <summary>
///     Winlogon AutoLogon keys plus the LSA <c>DefaultPassword</c> private secret. The password is never
///     converted to a <see cref="string" /> and is zeroed after use.
/// </summary>
public sealed class WindowsAutoLogonPlatform : IAutoLogonPlatform
{
    internal const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    internal const string PasswordLessKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device";
    internal const string DefaultPasswordSecret = "DefaultPassword";

    private const uint PolicyCreateSecret = 0x00000020;
    private const uint PolicyGetPrivateInformation = 0x00000004;

    /// <inheritdoc />
    public AutoLogonState Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new AutoLogonState(false, null, null, null, false, false, false, null, ["Windows is required."]);
        }

        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? winlogon = machine.OpenSubKey(WinlogonKey, writable: false);
        string? auto = ReadString(winlogon, "AutoAdminLogon");
        bool enabled = string.Equals(auto, "1", StringComparison.Ordinal);
        string? userName = ReadString(winlogon, "DefaultUserName");
        string? domain = ReadString(winlogon, "DefaultDomainName");
        int? count = ReadInt(winlogon, "AutoLogonCount");
        bool force = string.Equals(ReadString(winlogon, "ForceAutoLogon"), "1", StringComparison.Ordinal);
        bool plaintext = HasValue(winlogon, "DefaultPassword");
        bool lsa = HasLsaSecret();
        bool? passwordless = ReadPasswordless(machine);

        List<string> notes = [];
        if (plaintext)
        {
            notes.Add("A legacy plaintext DefaultPassword registry value is present. disable_autologon removes it.");
        }

        if (passwordless == true)
        {
            notes.Add("Windows 11 passwordless device policy is enabled; AutoLogon may be ignored for Microsoft accounts.");
        }

        if (enabled && !lsa && !plaintext)
        {
            notes.Add("AutoAdminLogon is on but no LSA DefaultPassword secret was found.");
        }

        notes.Add("The password is never returned.");
        return new AutoLogonState(enabled, EmptyToNull(userName), EmptyToNull(domain), count, force, lsa, plaintext, passwordless, notes);
    }

    /// <inheritdoc />
    public OperationResult<AutoLogonChange> Enable(
        string userName,
        string? domain,
        ReadOnlySpan<char> password,
        int? logonCount,
        bool forceAutoLogon)
    {
        OperationResult<bool> verified = VerifyCredentials(userName, domain, password);
        if (!verified.Success)
        {
            return verified.FailAs<AutoLogonChange>();
        }

        try
        {
            StoreLsaSecret(password);
            WriteWinlogon(userName, domain, logonCount, forceAutoLogon, enabled: true);
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult<AutoLogonChange>.Fail(
                "access_denied",
                "Windows denied access to AutoLogon settings.");
        }
        catch (Exception exception)
        {
            return OperationResult<AutoLogonChange>.Fail("autologon_write_failed", exception.Message);
        }

        AutoLogonState after = Read();
        return OperationResult<AutoLogonChange>.Ok(new AutoLogonChange(
            after.Enabled,
            after.UserName,
            after.Domain,
            after.LogonCount,
            after.ForceAutoLogon,
            after.Notes));
    }

    /// <inheritdoc />
    public OperationResult<AutoLogonChange> Disable(bool clearDefaultUser)
    {
        try
        {
            DeleteLsaSecret();
            ClearWinlogon(clearDefaultUser);
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult<AutoLogonChange>.Fail(
                "access_denied",
                "Windows denied access to AutoLogon settings.");
        }
        catch (Exception exception)
        {
            return OperationResult<AutoLogonChange>.Fail("autologon_write_failed", exception.Message);
        }

        AutoLogonState after = Read();
        return OperationResult<AutoLogonChange>.Ok(new AutoLogonChange(
            after.Enabled,
            after.UserName,
            after.Domain,
            after.LogonCount,
            after.ForceAutoLogon,
            after.Notes));
    }

    /// <inheritdoc />
    public OperationResult<bool> VerifyCredentials(string userName, string? domain, ReadOnlySpan<char> password)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<bool>.Fail("windows_required", "Windows is required.");
        }

        char[] buffer = new char[password.Length + 1];
        password.CopyTo(buffer);
        nint token = 0;
        try
        {
            bool ok;
            unsafe
            {
                fixed (char* secret = buffer)
                {
                    ok = LogonUserW(
                        userName,
                        string.IsNullOrEmpty(domain) ? null : domain,
                        secret,
                        (uint)LOGON32_LOGON.LOGON32_LOGON_INTERACTIVE,
                        (uint)LOGON32_PROVIDER.LOGON32_PROVIDER_DEFAULT,
                        out token);
                }
            }

            int error = Marshal.GetLastWin32Error();
            if (!ok)
            {
                return MapLogonError(error);
            }

            return OperationResult<bool>.Ok(true);
        }
        finally
        {
            if (token != 0)
            {
                using SafeFileHandle owned = new(token, ownsHandle: true);
            }

            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));
        }
    }

    /// <summary>
    ///     CsWin32 generates <c>LogonUser</c> with a <see cref="string" /> password under the current
    ///     NativeMethods.json settings, which would copy the secret. This fallback pins the buffer.
    /// </summary>
    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern unsafe bool LogonUserW(
        string lpszUsername,
        string? lpszDomain,
        char* lpszPassword,
        uint dwLogonType,
        uint dwLogonProvider,
        out nint phToken);

    private static OperationResult<bool> MapLogonError(int error) => error switch
    {
        1326 => OperationResult<bool>.Fail("invalid_credentials", "The user name or password is incorrect."),
        1331 => OperationResult<bool>.Fail("account_disabled", "The account is disabled."),
        1330 => OperationResult<bool>.Fail("password_expired", "The password has expired."),
        1909 => OperationResult<bool>.Fail("account_locked", "The account is locked out."),
        1327 => OperationResult<bool>.Fail("account_restriction", "The account restriction prevents this logon."),
        5 => OperationResult<bool>.Fail("access_denied", "Windows denied the logon check."),
        _ => OperationResult<bool>.Fail("logon_failed", $"Windows rejected the credentials (Win32 {error}).")
    };

    private static void WriteWinlogon(string userName, string? domain, int? logonCount, bool forceAutoLogon, bool enabled)
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey winlogon = machine.CreateSubKey(WinlogonKey, writable: true)
            ?? throw new InvalidOperationException("Winlogon key could not be opened.");
        winlogon.SetValue("AutoAdminLogon", enabled ? "1" : "0", RegistryValueKind.String);
        winlogon.SetValue("DefaultUserName", userName, RegistryValueKind.String);
        if (string.IsNullOrWhiteSpace(domain))
        {
            DeleteValue(winlogon, "DefaultDomainName");
        }
        else
        {
            winlogon.SetValue("DefaultDomainName", domain, RegistryValueKind.String);
        }

        if (logonCount is { } count)
        {
            winlogon.SetValue("AutoLogonCount", count, RegistryValueKind.DWord);
        }
        else
        {
            DeleteValue(winlogon, "AutoLogonCount");
        }

        if (forceAutoLogon)
        {
            winlogon.SetValue("ForceAutoLogon", "1", RegistryValueKind.String);
        }
        else
        {
            DeleteValue(winlogon, "ForceAutoLogon");
        }

        DeleteValue(winlogon, "DefaultPassword");
    }

    private static void ClearWinlogon(bool clearDefaultUser)
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? winlogon = machine.OpenSubKey(WinlogonKey, writable: true);
        if (winlogon is null)
        {
            return;
        }

        winlogon.SetValue("AutoAdminLogon", "0", RegistryValueKind.String);
        DeleteValue(winlogon, "DefaultPassword");
        DeleteValue(winlogon, "AutoLogonCount");
        DeleteValue(winlogon, "ForceAutoLogon");
        if (clearDefaultUser)
        {
            DeleteValue(winlogon, "DefaultUserName");
            DeleteValue(winlogon, "DefaultDomainName");
        }
    }

    private static unsafe bool HasLsaSecret()
    {
        if (!TryOpenPolicy(PolicyGetPrivateInformation, out LsaCloseSafeHandle? policy))
        {
            return false;
        }

        nint nameBuffer = 0;
        LSA_UNICODE_STRING* data = null;
        try
        {
            LSA_UNICODE_STRING name = MakeUnicode(DefaultPasswordSecret, out nameBuffer);
            NTSTATUS status = PInvoke.LsaRetrievePrivateData(policy, in name, out data);
            return status.Value == 0 && data is not null;
        }
        finally
        {
            if (data is not null)
            {
                PInvoke.LsaFreeMemory(data);
            }

            FreeBuffer(nameBuffer);
            policy.Dispose();
        }
    }

    private static unsafe void StoreLsaSecret(ReadOnlySpan<char> password)
    {
        if (!TryOpenPolicy(PolicyCreateSecret | PolicyGetPrivateInformation, out LsaCloseSafeHandle? policy))
        {
            throw new UnauthorizedAccessException("LSA policy could not be opened.");
        }

        nint nameBuffer = 0;
        nint dataBuffer = 0;
        try
        {
            LSA_UNICODE_STRING name = MakeUnicode(DefaultPasswordSecret, out nameBuffer);
            LSA_UNICODE_STRING data = MakeUnicode(password, out dataBuffer);
            NTSTATUS status = PInvoke.LsaStorePrivateData(policy, in name, data);
            if (status.Value != 0)
            {
                throw new InvalidOperationException(
                    $"LsaStorePrivateData failed (Win32 {PInvoke.LsaNtStatusToWinError(status)}).");
            }
        }
        finally
        {
            ZeroAndFree(dataBuffer, password.Length + 1);
            FreeBuffer(nameBuffer);
            policy.Dispose();
        }
    }

    private static unsafe void DeleteLsaSecret()
    {
        if (!TryOpenPolicy(PolicyCreateSecret, out LsaCloseSafeHandle? policy))
        {
            throw new UnauthorizedAccessException("LSA policy could not be opened.");
        }

        nint nameBuffer = 0;
        try
        {
            LSA_UNICODE_STRING name = MakeUnicode(DefaultPasswordSecret, out nameBuffer);
            NTSTATUS status = PInvoke.LsaStorePrivateData(policy, in name, null);
            if (status.Value != 0)
            {
                int win32 = (int)PInvoke.LsaNtStatusToWinError(status);
                if (win32 is not 2 and not 87)
                {
                    throw new InvalidOperationException($"LsaStorePrivateData delete failed (Win32 {win32}).");
                }
            }
        }
        finally
        {
            FreeBuffer(nameBuffer);
            policy.Dispose();
        }
    }

    private static bool TryOpenPolicy(uint access, [NotNullWhen(true)] out LsaCloseSafeHandle? policy)
    {
        LSA_OBJECT_ATTRIBUTES attributes = default;
        NTSTATUS status = PInvoke.LsaOpenPolicy(null, in attributes, access, out policy);
        if (status.Value == 0 && policy is { IsInvalid: false })
        {
            return true;
        }

        policy?.Dispose();
        policy = null;
        return false;
    }

    private static unsafe LSA_UNICODE_STRING MakeUnicode(string value, out nint buffer)
    {
        buffer = Marshal.StringToHGlobalUni(value);
        ushort bytes = (ushort)checked(value.Length * sizeof(char));
        return new LSA_UNICODE_STRING
        {
            Length = bytes,
            MaximumLength = (ushort)(bytes + sizeof(char)),
            Buffer = new PWSTR((char*)buffer)
        };
    }

    private static unsafe LSA_UNICODE_STRING MakeUnicode(ReadOnlySpan<char> value, out nint buffer)
    {
        int chars = checked(value.Length + 1);
        buffer = Marshal.AllocHGlobal(checked(chars * sizeof(char)));
        Span<char> dest = new((void*)buffer, chars);
        value.CopyTo(dest);
        dest[value.Length] = '\0';
        ushort bytes = (ushort)checked(value.Length * sizeof(char));
        return new LSA_UNICODE_STRING
        {
            Length = bytes,
            MaximumLength = (ushort)(bytes + sizeof(char)),
            Buffer = new PWSTR((char*)buffer)
        };
    }

    private static void FreeBuffer(nint buffer)
    {
        if (buffer != 0)
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static unsafe void ZeroAndFree(nint buffer, int charCount)
    {
        if (buffer == 0)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(new Span<byte>((void*)buffer, checked(charCount * sizeof(char))));
        Marshal.FreeHGlobal(buffer);
    }

    private static string? ReadString(RegistryKey? key, string name)
    {
        object? value = key?.GetValue(name);
        return value is string text ? text : value?.ToString();
    }

    private static int? ReadInt(RegistryKey? key, string name)
    {
        object? value = key?.GetValue(name);
        return value switch
        {
            int number => number,
            string text when int.TryParse(text, out int parsed) => parsed,
            _ => null
        };
    }

    private static bool HasValue(RegistryKey? key, string name) =>
        key?.GetValue(name) is not null;

    private static void DeleteValue(RegistryKey key, string name)
    {
        try
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (ArgumentException)
        {
        }
    }

    private static bool? ReadPasswordless(RegistryKey machine)
    {
        using RegistryKey? key = machine.OpenSubKey(PasswordLessKey, writable: false);
        object? value = key?.GetValue("DevicePasswordLessBuildVersion");
        return value is int number ? number >= 2 : null;
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
