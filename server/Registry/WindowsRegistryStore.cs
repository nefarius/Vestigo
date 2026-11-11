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
        catch (EnvVars.UserNotAvailableException exception)
        {
            return new ConsoleUserLookup(null, "no_interactive_session", exception.Message, exception.Reason);
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
    public RegistryOpen Open(RegistryViewKind view, RegistryHiveKind hive, string subKey, RegistryLinkConfinement confine)
    {
        string hiveName = hive == RegistryHiveKind.Users ? "HKU" : "HKLM";
        string canonical = subKey.Length == 0 ? hiveName : hiveName + "\\" + subKey;
        RegistryPathDecision requested = new(canonical, hive, subKey);
        using RelativeProbe walk = new(view);
        OperationResult<RegistryPathDecision> resolved = RegistryLinkWalker.Resolve(requested, walk.Probe, confine);
        if (!resolved.Success || resolved.Value is null)
        {
            return new RegistryOpen(null, resolved.ErrorCode, resolved.Error);
        }

        try
        {
            if (resolved.Value.SubKey.Length == 0)
            {
                RegistryKey root = RegistryKey.OpenBaseKey(ToHive(resolved.Value.Hive), ToView(view));
                RegistryLinkProbe rootLink = ReadLink(root.Handle);
                if (rootLink.ErrorCode is not null)
                {
                    root.Dispose();
                    return new RegistryOpen(null, rootLink.ErrorCode, rootLink.Error);
                }

                if (rootLink.IsLink)
                {
                    root.Dispose();
                    return new RegistryOpen(null, "invalid_request", "Registry symbolic link was not followed.");
                }

                return new RegistryOpen(new WindowsRegistryKey(root, this, view, requested, confine), null, null, resolved.Value);
            }

            if (!walk.TryTake(resolved.Value.SubKey, out SafeRegistryHandle? opened))
            {
                return new RegistryOpen(null, "invalid_request", "Registry symbolic link was not followed.");
            }

            return new RegistryOpen(
                new WindowsRegistryKey(opened!, this, view, requested, confine),
                null,
                null,
                resolved.Value);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
        {
            return new RegistryOpen(null, "access_denied", "Windows denied access to the registry key.");
        }
        catch (IOException exception)
        {
            return new RegistryOpen(null, "io_error", exception.Message);
        }
    }

    /// <summary>Opens each path segment from the previous handle so an intermediate link cannot be swapped in.</summary>
    private sealed class RelativeProbe(RegistryViewKind view) : IDisposable
    {
        private RegistryHiveKind _hive;
        private RegistryKey? _root;
        private SafeRegistryHandle? _parent;
        private string _parentSub = string.Empty;
        private bool _parentIsRoot;
        private bool _started;

        public RegistryLinkProbe Probe(RegistryHiveKind hive, string subKey)
        {
            try
            {
                if (!_started || hive != _hive || !IsNext(_parentSub, subKey, out string segment))
                {
                    Reset();
                    Start(hive);
                    if (!IsNext(string.Empty, subKey, out segment))
                    {
                        return RegistryLinkProbe.Fail("invalid_request", "Registry symbolic link was not followed.");
                    }
                }

                if (!TryOpen(_parent!, segment, view, queryOnly: false, out SafeRegistryHandle? child, out string? code, out string? error))
                {
                    return RegistryLinkProbe.Fail(code!, error!);
                }

                RegistryLinkProbe link = ReadLink(child!);
                if (link.ErrorCode is not null || link.IsLink)
                {
                    child!.Dispose();
                    return link;
                }

                if (!_parentIsRoot)
                {
                    _parent!.Dispose();
                }

                _parent = child;
                _parentSub = subKey;
                _parentIsRoot = false;
                return link;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
            {
                return RegistryLinkProbe.Fail("access_denied", "Windows denied access to the registry key.");
            }
            catch (IOException exception)
            {
                return RegistryLinkProbe.Fail("io_error", exception.Message);
            }
        }

        public bool TryTake(string subKey, out SafeRegistryHandle? handle)
        {
            if (_parent is not null && !_parentIsRoot && subKey.Equals(_parentSub, StringComparison.OrdinalIgnoreCase))
            {
                handle = _parent;
                _parent = null;
                return true;
            }

            handle = null;
            return false;
        }

        public void Dispose() => Reset();

        private void Start(RegistryHiveKind hive)
        {
            _root = RegistryKey.OpenBaseKey(ToHive(hive), ToView(view));
            _parent = _root.Handle;
            _parentIsRoot = true;
            _parentSub = string.Empty;
            _hive = hive;
            _started = true;
        }

        private void Reset()
        {
            if (_parent is not null && !_parentIsRoot)
            {
                _parent.Dispose();
            }

            _parent = null;
            _root?.Dispose();
            _root = null;
            _parentIsRoot = false;
            _parentSub = string.Empty;
            _started = false;
        }

        private static bool IsNext(string parentSub, string subKey, out string segment)
        {
            if (parentSub.Length == 0)
            {
                segment = subKey;
                return subKey.Length > 0 && !subKey.Contains('\\');
            }

            string prefix = parentSub + "\\";
            if (!subKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                segment = string.Empty;
                return false;
            }

            segment = subKey[prefix.Length..];
            return segment.Length > 0 && !segment.Contains('\\');
        }
    }

    private static RegistryLinkProbe ReadLink(SafeRegistryHandle handle)
    {
        REG_VALUE_TYPE type = default;
        uint size = 0;
        WIN32_ERROR error = QueryValue(handle, "SymbolicLinkValue", null, ref type, ref size);
        if (error == WIN32_ERROR.ERROR_FILE_NOT_FOUND || type != REG_VALUE_TYPE.REG_LINK)
        {
            return error is WIN32_ERROR.ERROR_FILE_NOT_FOUND or WIN32_ERROR.ERROR_SUCCESS
                ? RegistryLinkProbe.NotALink()
                : ProbeIo(error);
        }

        OperationResult<int> length = RegistryPathPolicy.LinkTargetLength(size);
        if (!length.Success || length.Value == 0)
        {
            return RegistryLinkProbe.Fail(length.ErrorCode!, length.Error!);
        }

        byte[] data = new byte[length.Value];
        uint got = size;
        error = QueryValue(handle, "SymbolicLinkValue", data, ref type, ref got);
        if (error != WIN32_ERROR.ERROR_SUCCESS || type != REG_VALUE_TYPE.REG_LINK)
        {
            return ProbeIo(error);
        }

        if (got < data.Length)
        {
            Array.Resize(ref data, (int)got);
        }

        return RegistryLinkProbe.Link(data);
    }

    private static bool TryOpen(
        SafeRegistryHandle parent,
        string subKey,
        RegistryViewKind view,
        bool queryOnly,
        out SafeRegistryHandle? opened,
        out string? code,
        out string? error)
    {
        opened = null;
        REG_SAM_FLAGS access = (queryOnly ? REG_SAM_FLAGS.KEY_QUERY_VALUE : REG_SAM_FLAGS.KEY_READ)
            | (view == RegistryViewKind.Registry32 ? REG_SAM_FLAGS.KEY_WOW64_32KEY : REG_SAM_FLAGS.KEY_WOW64_64KEY);
        unsafe
        {
            fixed (char* name = subKey)
            {
                HKEY raw;
                WIN32_ERROR result = PInvoke.RegOpenKeyEx(
                    HandleOf(parent),
                    name,
                    (uint)REG_OPEN_CREATE_OPTIONS.REG_OPTION_OPEN_LINK,
                    access,
                    &raw);
                if (result == WIN32_ERROR.ERROR_SUCCESS)
                {
                    opened = new SafeRegistryHandle((IntPtr)raw.Value, ownsHandle: true);
                    code = null;
                    error = null;
                    return true;
                }

                if (result == WIN32_ERROR.ERROR_FILE_NOT_FOUND)
                {
                    code = "key_not_found";
                    error = "Registry key was not found.";
                    return false;
                }

                if (result == WIN32_ERROR.ERROR_ACCESS_DENIED)
                {
                    code = "access_denied";
                    error = "Windows denied access to the registry key.";
                    return false;
                }

                code = "io_error";
                error = $"Opening the registry key failed with Win32 error {(int)result}.";
                return false;
            }
        }
    }

    private static HKEY HandleOf(SafeRegistryHandle handle) => new(handle.DangerousGetHandle());

    private static WIN32_ERROR QueryValue(SafeRegistryHandle handle, string? name, byte[]? data, ref REG_VALUE_TYPE type, ref uint size)
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
                        HandleOf(handle),
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

    private static RegistryLinkProbe ProbeIo(WIN32_ERROR error) =>
        error == WIN32_ERROR.ERROR_ACCESS_DENIED
            ? RegistryLinkProbe.Fail("access_denied", "Windows denied access to the registry key.")
            : RegistryLinkProbe.Fail("io_error", $"Reading the registry link failed with Win32 error {(int)error}.");

    private static RegistryHive ToHive(RegistryHiveKind hive) =>
        hive == RegistryHiveKind.Users ? RegistryHive.Users : RegistryHive.LocalMachine;

    private static RegistryView ToView(RegistryViewKind view) =>
        view == RegistryViewKind.Registry32 ? RegistryView.Registry32 : RegistryView.Registry64;

    private sealed class WindowsRegistryKey : IRegistryKey
    {
        private readonly SafeRegistryHandle _handle;
        private readonly RegistryKey? _ownedRoot;
        private readonly bool _ownsHandle;
        private readonly WindowsRegistryStore _store;
        private readonly RegistryViewKind _view;
        private readonly RegistryPathDecision _path;
        private readonly RegistryLinkConfinement _confine;
        private bool _loaded;
        private DateTimeOffset? _lastWrite;
        private int _subKeys;
        private int _values;

        public WindowsRegistryKey(
            RegistryKey ownedRoot,
            WindowsRegistryStore store,
            RegistryViewKind view,
            RegistryPathDecision path,
            RegistryLinkConfinement confine)
        {
            _ownedRoot = ownedRoot;
            _handle = ownedRoot.Handle;
            _ownsHandle = false;
            _store = store;
            _view = view;
            _path = path;
            _confine = confine;
        }

        public WindowsRegistryKey(
            SafeRegistryHandle handle,
            WindowsRegistryStore store,
            RegistryViewKind view,
            RegistryPathDecision path,
            RegistryLinkConfinement confine)
        {
            _handle = handle;
            _ownsHandle = true;
            _store = store;
            _view = view;
            _path = path;
            _confine = confine;
        }

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
                string child = _path.SubKey.Length == 0 ? name : _path.SubKey + "\\" + name;
                RegistryOpen opened = _store.Open(_view, _path.Hive, child, _confine);
                if (opened.Key is null)
                {
                    return null;
                }

                using IRegistryKey key = opened.Key;
                return key.ValueCount;
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
            if (_ownsHandle)
            {
                _handle.Dispose();
            }

            _ownedRoot?.Dispose();
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
                    HandleOf(_handle),
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
                        ? PInvoke.RegEnumValue(HandleOf(_handle), index, pointer, &chars, null, null, null, null)
                        : PInvoke.RegEnumKeyEx(HandleOf(_handle), index, pointer, &chars, null, default, null, null);
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

        private WIN32_ERROR Query(string? name, byte[]? data, ref REG_VALUE_TYPE type, ref uint size) =>
            QueryValue(_handle, name, data, ref type, ref size);

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
