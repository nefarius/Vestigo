using System.Runtime.InteropServices;
using System.Security;

using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;

namespace Vestigo.RegistryProbe;

/// <summary>Registry reads through the explicit 32-bit or 64-bit view.</summary>
public sealed class WindowsRegistryStore : IRegistryStore
{
    /// <inheritdoc />
    public ConsoleUserLookup LookupConsoleUser()
    {
        try
        {
            return new ConsoleUserLookup(EnvVars.RegistryEnvironmentStore.ActiveConsoleUserSid(), null, null);
        }
        catch (EnvVars.UserNotAvailableException)
        {
            return new ConsoleUserLookup(null, "no_interactive_session", "No user is logged on at the console.");
        }
        catch (UnauthorizedAccessException)
        {
            return new ConsoleUserLookup(null, "access_denied", "Querying the console user requires the service account.");
        }
        catch (IOException exception)
        {
            return new ConsoleUserLookup(null, "io_error", exception.Message);
        }
    }

    /// <inheritdoc />
    public RegistryOpen Open(RegistryViewKind view, RegistryHiveKind hive, string subKey)
    {
        RegistryKey? root = null;
        try
        {
            root = RegistryKey.OpenBaseKey(ToHive(hive), ToView(view));
            if (subKey.Length == 0)
            {
                return new RegistryOpen(new WindowsRegistryKey(root, root), null, null);
            }

            RegistryKey? key = root.OpenSubKey(subKey, writable: false);
            if (key is null)
            {
                root.Dispose();
                return new RegistryOpen(null, "key_not_found", "Registry key was not found.");
            }

            return new RegistryOpen(new WindowsRegistryKey(root, key), null, null);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
        {
            root?.Dispose();
            return new RegistryOpen(null, "access_denied", "Windows denied access to the registry key.");
        }
        catch (IOException exception)
        {
            root?.Dispose();
            return new RegistryOpen(null, "io_error", exception.Message);
        }
    }

    private static HKEY HandleOf(RegistryKey key) => new(key.Handle.DangerousGetHandle());

    private static RegistryHive ToHive(RegistryHiveKind hive) =>
        hive == RegistryHiveKind.Users ? RegistryHive.Users : RegistryHive.LocalMachine;

    private static RegistryView ToView(RegistryViewKind view) =>
        view == RegistryViewKind.Registry32 ? RegistryView.Registry32 : RegistryView.Registry64;

    private sealed class WindowsRegistryKey(RegistryKey root, RegistryKey key) : IRegistryKey
    {
        private bool _loaded;
        private DateTimeOffset? _lastWrite;
        private int _subKeys;
        private int _values;

        public DateTimeOffset? LastWriteUtc
        {
            get
            {
                EnsureInfo();
                return _lastWrite;
            }
        }

        public int SubKeyCount
        {
            get
            {
                EnsureInfo();
                return _subKeys;
            }
        }

        public int ValueCount
        {
            get
            {
                EnsureInfo();
                return _values;
            }
        }

        public RegistryNamePage SubKeyNames(int limit) => EnumNames(limit, values: false);

        public RegistryNamePage ValueNames(int limit) => EnumNames(limit, values: true);

        public int? SubKeyValueCount(string name)
        {
            try
            {
                using RegistryKey? child = key.OpenSubKey(name, writable: false);
                return child?.ValueCount;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
            {
                return null;
            }
        }

        public RegistryRawValue Read(string? name, int maxBinaryBytes, int maxStringBytes)
        {
            if (string.IsNullOrEmpty(name))
            {
                name = null;
            }

            REG_VALUE_TYPE type = default;
            uint size = 0;
            WIN32_ERROR error = Query(name, null, ref type, ref size);
            if (error == WIN32_ERROR.ERROR_FILE_NOT_FOUND)
            {
                return Missing();
            }

            if (error != WIN32_ERROR.ERROR_SUCCESS)
            {
                return Io(error);
            }

            string typeName = TypeName(type);
            int cap = Cap(type, maxBinaryBytes, maxStringBytes);
            if (size > (uint)cap)
            {
                return new RegistryRawValue(true, null, null, typeName, checked((int)size), null, true);
            }

            byte[] data = new byte[size];
            uint got = size;
            error = data.Length == 0
                ? WIN32_ERROR.ERROR_SUCCESS
                : Query(name, data, ref type, ref got);
            if (error == WIN32_ERROR.ERROR_FILE_NOT_FOUND)
            {
                return Missing();
            }

            if (error != WIN32_ERROR.ERROR_SUCCESS)
            {
                return Io(error);
            }

            if (got < data.Length)
            {
                Array.Resize(ref data, (int)got);
            }

            return new RegistryRawValue(true, null, null, TypeName(type), data.Length, data, false);
        }

        public void Dispose()
        {
            if (!ReferenceEquals(root, key))
            {
                key.Dispose();
            }

            root.Dispose();
        }

        private void EnsureInfo()
        {
            if (_loaded)
            {
                return;
            }

            uint subKeys = 0;
            uint values = 0;
            System.Runtime.InteropServices.ComTypes.FILETIME fileTime = default;
            unsafe
            {
                WIN32_ERROR error = PInvoke.RegQueryInfoKey(
                    HandleOf(key),
                    lpClass: default,
                    lpcchClass: null,
                    lpReserved: null,
                    &subKeys,
                    lpcbMaxSubKeyLen: null,
                    lpcbMaxClassLen: null,
                    &values,
                    lpcbMaxValueNameLen: null,
                    lpcbMaxValueLen: null,
                    lpcbSecurityDescriptor: null,
                    &fileTime);
                if (error != WIN32_ERROR.ERROR_SUCCESS)
                {
                    throw new IOException($"Reading registry key information failed with Win32 error {(int)error}.");
                }
            }

            long ticks = ((long)fileTime.dwHighDateTime << 32) | (uint)fileTime.dwLowDateTime;
            _lastWrite = ticks == 0 ? null : DateTimeOffset.FromFileTime(ticks);
            _subKeys = checked((int)subKeys);
            _values = checked((int)values);
            _loaded = true;
        }

        private RegistryNamePage EnumNames(int limit, bool values)
        {
            if (limit <= 0)
            {
                return new RegistryNamePage([], false);
            }

            List<string> names = [];
            char[] buffer = new char[512];
            uint index = 0;
            while (names.Count < limit)
            {
                WIN32_ERROR error = EnumOne(values, index, buffer, out string? name, out bool grow);
                if (grow)
                {
                    buffer = new char[Math.Min(buffer.Length * 2, 16384)];
                    continue;
                }

                if (error == WIN32_ERROR.ERROR_NO_MORE_ITEMS)
                {
                    return new RegistryNamePage(names, false);
                }

                if (error != WIN32_ERROR.ERROR_SUCCESS || name is null)
                {
                    throw new IOException($"Enumerating registry names failed with Win32 error {(int)error}.");
                }

                names.Add(name);
                index++;
            }

            WIN32_ERROR extra = EnumOne(values, index, buffer, out _, out _);
            return new RegistryNamePage(names, extra == WIN32_ERROR.ERROR_SUCCESS);
        }

        private WIN32_ERROR EnumOne(bool values, uint index, char[] buffer, out string? name, out bool grow)
        {
            name = null;
            grow = false;
            uint chars = (uint)buffer.Length - 1;
            unsafe
            {
                fixed (char* pointer = buffer)
                {
                    WIN32_ERROR error = values
                        ? PInvoke.RegEnumValue(HandleOf(key), index, pointer, &chars, null, null, null, null)
                        : PInvoke.RegEnumKeyEx(HandleOf(key), index, pointer, &chars, null, default, null, null);
                    if (error == WIN32_ERROR.ERROR_MORE_DATA)
                    {
                        grow = true;
                        return error;
                    }

                    if (error == WIN32_ERROR.ERROR_SUCCESS)
                    {
                        name = new string(pointer, 0, (int)chars);
                    }

                    return error;
                }
            }
        }

        private WIN32_ERROR Query(string? name, byte[]? data, ref REG_VALUE_TYPE type, ref uint size)
        {
            REG_VALUE_TYPE localType = type;
            uint localSize = size;
            unsafe
            {
                fixed (byte* pointer = data)
                {
                    fixed (char* valueName = name)
                    {
                        WIN32_ERROR error = PInvoke.RegQueryValueEx(
                            HandleOf(key),
                            name is null ? default : valueName,
                            null,
                            &localType,
                            data is null ? null : pointer,
                            &localSize);
                        type = localType;
                        size = localSize;
                        return error;
                    }
                }
            }
        }

        private static int Cap(REG_VALUE_TYPE type, int maxBinaryBytes, int maxStringBytes) => type switch
        {
            REG_VALUE_TYPE.REG_SZ or REG_VALUE_TYPE.REG_EXPAND_SZ or REG_VALUE_TYPE.REG_MULTI_SZ => maxStringBytes,
            REG_VALUE_TYPE.REG_DWORD or REG_VALUE_TYPE.REG_DWORD_BIG_ENDIAN or REG_VALUE_TYPE.REG_QWORD or REG_VALUE_TYPE.REG_NONE => 16,
            _ => maxBinaryBytes
        };

        private static string TypeName(REG_VALUE_TYPE type) => type switch
        {
            REG_VALUE_TYPE.REG_SZ => "REG_SZ",
            REG_VALUE_TYPE.REG_EXPAND_SZ => "REG_EXPAND_SZ",
            REG_VALUE_TYPE.REG_MULTI_SZ => "REG_MULTI_SZ",
            REG_VALUE_TYPE.REG_DWORD => "REG_DWORD",
            REG_VALUE_TYPE.REG_DWORD_BIG_ENDIAN => "REG_DWORD_BIG_ENDIAN",
            REG_VALUE_TYPE.REG_QWORD => "REG_QWORD",
            REG_VALUE_TYPE.REG_BINARY => "REG_BINARY",
            REG_VALUE_TYPE.REG_NONE => "REG_NONE",
            _ => "REG_UNKNOWN"
        };

        private static RegistryRawValue Missing() =>
            new(false, "value_not_found", "Registry value was not found.", "REG_NONE", 0, null, false);

        private static RegistryRawValue Io(WIN32_ERROR error) =>
            error == WIN32_ERROR.ERROR_ACCESS_DENIED
                ? new RegistryRawValue(false, "access_denied", "Windows denied access to the registry value.", "REG_NONE", 0, null, false)
                : new RegistryRawValue(false, "io_error", $"Reading the registry value failed with Win32 error {(int)error}.", "REG_NONE", 0, null, false);
    }
}
