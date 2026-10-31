using System.Security.Principal;

using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Vestigo.EnvVars;

/// <summary>
///     Registry-backed store. Machine scope uses the Session Manager key; user scope uses the hive of the user
///     on the active console session; process scope uses the current process block. Never logs values.
/// </summary>
public sealed class RegistryEnvironmentStore : IEnvironmentStore
{
    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const uint NoSession = 0xFFFFFFFF;

    public EnvironmentValue? Get(EnvironmentScope scope, string name)
    {
        if (scope == EnvironmentScope.Process)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return value is null ? null : new EnvironmentValue(value, false);
        }

        using RegistryKey? key = Open(scope, writable: false, create: false);
        return key is null ? null : ReadValue(key, name);
    }

    public IReadOnlyDictionary<string, EnvironmentValue> GetAll(EnvironmentScope scope)
    {
        Dictionary<string, EnvironmentValue> result = new(StringComparer.OrdinalIgnoreCase);
        if (scope == EnvironmentScope.Process)
        {
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                result[(string)entry.Key] = new EnvironmentValue((string?)entry.Value ?? string.Empty, false);
            }

            return result;
        }

        using RegistryKey? key = Open(scope, writable: false, create: false);
        if (key is null)
        {
            return result;
        }

        foreach (string name in key.GetValueNames())
        {
            EnvironmentValue? value = ReadValue(key, name);
            if (value is not null)
            {
                result[name] = value;
            }
        }

        return result;
    }

    public void Set(EnvironmentScope scope, string name, EnvironmentValue value)
    {
        if (scope == EnvironmentScope.Process)
        {
            Environment.SetEnvironmentVariable(name, value.Value);
            return;
        }

        using RegistryKey key = Open(scope, writable: true, create: true)!;
        key.SetValue(name, value.Value, value.Expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
    }

    public bool Remove(EnvironmentScope scope, string name)
    {
        if (scope == EnvironmentScope.Process)
        {
            bool existed = Environment.GetEnvironmentVariable(name) is not null;
            Environment.SetEnvironmentVariable(name, null);
            return existed;
        }

        using RegistryKey? key = Open(scope, writable: true, create: false);
        if (key is null || key.GetValue(name) is null)
        {
            return false;
        }

        key.DeleteValue(name, throwOnMissingValue: false);
        return true;
    }

    public unsafe bool BroadcastChange()
    {
        fixed (char* area = "Environment")
        {
            LRESULT result = PInvoke.SendMessageTimeout(
                new HWND(0xFFFF),
                PInvoke.WM_SETTINGCHANGE,
                default,
                new LPARAM((nint)area),
                SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG,
                5000,
                out _);
            return result.Value != 0;
        }
    }

    private static RegistryKey? Open(EnvironmentScope scope, bool writable, bool create)
    {
        if (scope == EnvironmentScope.Machine)
        {
            return create
                ? Registry.LocalMachine.CreateSubKey(MachineKey, writable)
                : Registry.LocalMachine.OpenSubKey(MachineKey, writable);
        }

        string path = ActiveConsoleUserSid() + @"\Environment";
        return create ? Registry.Users.CreateSubKey(path, writable) : Registry.Users.OpenSubKey(path, writable);
    }

    private static EnvironmentValue? ReadValue(RegistryKey key, string name)
    {
        object? raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is not string text)
        {
            return null;
        }

        return new EnvironmentValue(text, key.GetValueKind(name) == RegistryValueKind.ExpandString);
    }

    /// <summary>SID of the user on the active console session, or <see cref="UserNotAvailableException" />.</summary>
    internal static unsafe string ActiveConsoleUserSid()
    {
        uint session = PInvoke.WTSGetActiveConsoleSessionId();
        if (session == NoSession)
        {
            throw new UserNotAvailableException("No user is logged on at the console.");
        }

        HANDLE rawToken = default;
        if (!PInvoke.WTSQueryUserToken(session, ref rawToken))
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            // ERROR_NO_TOKEN: the session exists (for example the logon screen) but nobody is signed in.
            if (error == 1008)
            {
                throw new UserNotAvailableException("No user is logged on at the console.");
            }

            if (error == 5)
            {
                throw new UnauthorizedAccessException("Querying the console user token requires the SYSTEM account (SeTcbPrivilege).");
            }

            throw new IOException($"Querying the console user failed with Win32 error {error}.");
        }

        using (SafeFileHandle token = new(rawToken, ownsHandle: true))
        {
            uint needed = 0;
            PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, null, 0, out needed);
            if (needed == 0)
            {
                throw new IOException("Querying the console user SID failed.");
            }

            byte[] buffer = new byte[needed];
            fixed (byte* pointer = buffer)
            {
                if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, pointer, needed, out needed))
                {
                    throw new IOException("Querying the console user SID failed.");
                }

                TOKEN_USER* user = (TOKEN_USER*)pointer;
                return new SecurityIdentifier((IntPtr)user->User.Sid.Value).Value;
            }
        }
    }
}
