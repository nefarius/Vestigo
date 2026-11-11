using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.StationsAndDesktops;

namespace Vestigo.Desktop;

/// <summary>
///     Owns <c>HDESK</c> values for Vestigo test desktops on the calling process's window station. The host process
///     is launched on <c>winsta0\default</c> and never calls <c>SetThreadDesktop</c>, so its threads stay on the
///     desktop where the process started. <c>SwitchDesktop</c> changes the input desktop only when a tool asks.
///     <c>CloseDesktop</c> releases this process's handle. It does not stop processes, and Windows keeps the desktop
///     while any thread or handle still uses it.
/// </summary>
internal sealed class WindowsDesktopStation : IDisposable
{
    private const uint DesktopAccess = 0x01FF;

    private readonly Dictionary<string, CloseDesktopSafeHandle> _owned = new(StringComparer.OrdinalIgnoreCase);
    private CloseDesktopSafeHandle? _original;
    private string? _originalName;
    private string? _inputOwner;
    private bool _disposed;

    public DesktopCommandResult Execute(DesktopCommand command) => command.Op switch
    {
        DesktopHostOperations.Create => Create(command),
        DesktopHostOperations.Activate => Activate(command),
        DesktopHostOperations.Restore => Restore(command),
        DesktopHostOperations.Close => Close(command),
        DesktopHostOperations.Enumerate => Enumerate(command.RequestId),
        DesktopHostOperations.Shutdown => Shutdown(command.RequestId),
        _ => DesktopCommandResult.Fail(command.RequestId, "invalid_request", $"Unknown desktop-host operation '{command.Op}'.")
    };

    public static DesktopCommandResult Recover(DesktopCommand command)
    {
        if (!DesktopSessionPolicy.IsOwnedName(command.DesktopName))
        {
            return DesktopCommandResult.Fail(command.RequestId, "desktop_not_owned", "Recovery only closes a Vestigo- test desktop.");
        }

        if (DesktopSessionPolicy.IsSecure(command.OriginalInputDesktop) || string.IsNullOrWhiteSpace(command.OriginalInputDesktop))
        {
            return DesktopCommandResult.Fail(command.RequestId, "secure_desktop_active", "Refusing to switch the input desktop to a secure desktop.");
        }

        if (command.Op == DesktopHostOperations.RestorePhase)
        {
            return RestoreInput(command.RequestId, command.DesktopName, command.OriginalInputDesktop);
        }

        if (command.Op == DesktopHostOperations.ClosePhase)
        {
            bool retained = ProbeAndRelease(command.DesktopName, out int? native, out string? error);
            return native is null
                ? new DesktopCommandResult(command.RequestId, true, null, null, null, command.SessionId, command.DesktopName, DesktopSessionPolicy.WindowStation, CurrentSessionId(), command.OriginalInputDesktop, TryReadInputName(), false, true, retained, false, Environment.ProcessId, null)
                : DesktopCommandResult.Fail(command.RequestId, "desktop_unavailable", error ?? "The test desktop could not be opened.", native);
        }

        return DesktopCommandResult.Fail(command.RequestId, "invalid_request", "Recovery phase must be restore or close.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_inputOwner is not null && _original is { IsInvalid: false })
        {
            using CloseDesktopSafeHandle.LeaseScope lease = _original.Lease();
            PInvoke.SwitchDesktop(lease.Value);
            _inputOwner = null;
        }

        foreach (CloseDesktopSafeHandle handle in _owned.Values)
        {
            handle.Dispose();
        }

        _owned.Clear();
        _original?.Dispose();
        _original = null;
    }

    internal static unsafe string? CurrentWindowStation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        HWINSTA station = PInvoke.GetProcessWindowStation();
        return station.IsNull ? null : ReadName((HANDLE)station.Value);
    }

    private DesktopCommandResult Create(DesktopCommand command)
    {
        if (!DesktopSessionPolicy.IsOwnedName(command.DesktopName))
        {
            return DesktopCommandResult.Fail(command.RequestId, "desktop_not_owned", "Refusing to create a desktop outside the Vestigo- test namespace.");
        }

        if (_owned.ContainsKey(command.DesktopName!))
        {
            return DesktopCommandResult.Fail(command.RequestId, "desktop_exists", "This process already holds that test desktop.");
        }

        if (!TryReadInput(out string? inputName, out string? code, out string? error, out int? native))
        {
            return DesktopCommandResult.Fail(command.RequestId, code!, error!, native);
        }

        if (!EnsureOriginal(inputName!, out string? originalError, out int? originalNative))
        {
            return DesktopCommandResult.Fail(command.RequestId, "desktop_unavailable", originalError!, originalNative);
        }

        CloseDesktopSafeHandle? created = CreateOwned(command.DesktopName!, out int? createNative);
        if (created is null)
        {
            return DesktopCommandResult.Fail(
                command.RequestId,
                "desktop_unavailable",
                "CreateDesktop failed: " + new Win32Exception(createNative ?? 0).Message,
                createNative);
        }

        _owned[command.DesktopName!] = created;
        return Success(command, inputName, activated: false, inputRestored: false, handleReleased: false, retained: false);
    }

    private DesktopCommandResult Activate(DesktopCommand command)
    {
        if (!TryGetOwned(command, out CloseDesktopSafeHandle desktop, out DesktopCommandResult? missing))
        {
            return missing!;
        }

        if (!TryReadInput(out string? inputName, out string? code, out string? error, out int? native))
        {
            return DesktopCommandResult.Fail(command.RequestId, code!, error!, native);
        }

        if (string.Equals(inputName, command.DesktopName, StringComparison.OrdinalIgnoreCase))
        {
            _inputOwner = command.DesktopName;
            return Success(command, inputName, activated: true, inputRestored: false, handleReleased: false, retained: false);
        }

        using (CloseDesktopSafeHandle.LeaseScope lease = desktop.Lease())
        {
            if (!PInvoke.SwitchDesktop(lease.Value))
            {
                int win32 = Marshal.GetLastWin32Error();
                return DesktopCommandResult.Fail(command.RequestId, "desktop_unavailable", "SwitchDesktop failed: " + new Win32Exception(win32).Message, win32);
            }
        }

        if (!TryReadInput(out string? actual, out code, out error, out native))
        {
            return DesktopCommandResult.Fail(command.RequestId, code!, error!, native);
        }

        bool activated = string.Equals(actual, command.DesktopName, StringComparison.OrdinalIgnoreCase);
        if (activated)
        {
            _inputOwner = command.DesktopName;
        }

        return activated
            ? Success(command, actual, activated: true, inputRestored: false, handleReleased: false, retained: false)
            : DesktopCommandResult.Fail(command.RequestId, "desktop_unavailable", $"The input desktop is '{actual}', not '{command.DesktopName}'.");
    }

    private DesktopCommandResult Restore(DesktopCommand command)
    {
        if (_original is null || _original.IsInvalid || _originalName is null)
        {
            return DesktopCommandResult.Fail(command.RequestId, "desktop_unavailable", "The original input desktop was not recorded.");
        }

        if (!TryReadInput(out string? inputName, out string? code, out string? error, out int? native))
        {
            return DesktopCommandResult.Fail(command.RequestId, code!, error!, native);
        }

        if (string.Equals(inputName, _originalName, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(_inputOwner, command.DesktopName, StringComparison.OrdinalIgnoreCase))
            {
                _inputOwner = null;
            }

            return Success(command, inputName, activated: false, inputRestored: true, handleReleased: false, retained: false);
        }

        if (!string.Equals(inputName, command.DesktopName, StringComparison.OrdinalIgnoreCase))
        {
            return DesktopCommandResult.Fail(
                command.RequestId,
                "input_desktop_changed",
                $"The input desktop is '{inputName}'. It was not switched back, because this session is not the current input desktop.");
        }

        using (CloseDesktopSafeHandle.LeaseScope lease = _original.Lease())
        {
            if (!PInvoke.SwitchDesktop(lease.Value))
            {
                int win32 = Marshal.GetLastWin32Error();
                return DesktopCommandResult.Fail(command.RequestId, "desktop_unavailable", "SwitchDesktop back to the original desktop failed: " + new Win32Exception(win32).Message, win32);
            }
        }

        _inputOwner = null;
        TryReadInput(out string? restored, out _, out _, out _);
        return Success(command, restored ?? _originalName, activated: false, inputRestored: true, handleReleased: false, retained: false);
    }

    private DesktopCommandResult Close(DesktopCommand command)
    {
        if (!TryGetOwned(command, out CloseDesktopSafeHandle desktop, out DesktopCommandResult? missing))
        {
            return missing!;
        }

        bool restored = false;
        if (string.Equals(_inputOwner, command.DesktopName, StringComparison.OrdinalIgnoreCase))
        {
            DesktopCommandResult restore = Restore(command);
            if (!restore.Success && restore.ErrorCode != "input_desktop_changed")
            {
                return restore;
            }

            restored = restore.InputRestored;
        }

        _owned.Remove(command.DesktopName!);
        desktop.Dispose();

        bool retained = StillExists(command.DesktopName!);
        TryReadInput(out string? input, out _, out _, out _);
        return new DesktopCommandResult(
            command.RequestId,
            true,
            null,
            null,
            null,
            command.SessionId,
            command.DesktopName,
            DesktopSessionPolicy.WindowStation,
            CurrentSessionId(),
            _originalName,
            input,
            restored,
            true,
            retained,
            false,
            Environment.ProcessId,
            null);
    }

    private unsafe DesktopCommandResult Enumerate(string requestId)
    {
        List<string> names = [];
        GCHandle handle = GCHandle.Alloc(names);
        try
        {
            HWINSTA station = PInvoke.GetProcessWindowStation();
            if (station.IsNull)
            {
                int win32 = Marshal.GetLastWin32Error();
                return DesktopCommandResult.Fail(requestId, "desktop_unavailable", "The process window station is not available.", win32);
            }

            if (!PInvoke.EnumDesktops(station, &EnumDesktopCallback, (LPARAM)GCHandle.ToIntPtr(handle)))
            {
                int win32 = Marshal.GetLastWin32Error();
                return DesktopCommandResult.Fail(requestId, "desktop_unavailable", "EnumDesktops failed: " + new Win32Exception(win32).Message, win32);
            }
        }
        finally
        {
            handle.Free();
        }

        TryReadInput(out string? input, out _, out _, out _);
        List<EnumeratedDesktop> desktops = names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new EnumeratedDesktop(name, DesktopSessionPolicy.Classify(name), _owned.ContainsKey(name)))
            .ToList();
        return new DesktopCommandResult(requestId, true, null, null, null, null, null, DesktopSessionPolicy.WindowStation, CurrentSessionId(), _originalName, input, false, false, false, false, Environment.ProcessId, desktops);
    }

    private DesktopCommandResult Shutdown(string requestId)
    {
        string? original = _originalName;
        Dispose();
        return new DesktopCommandResult(requestId, true, null, null, null, null, null, DesktopSessionPolicy.WindowStation, CurrentSessionId(), original, TryReadInputName(), true, true, false, false, Environment.ProcessId, null);
    }

    private DesktopCommandResult Success(DesktopCommand command, string? input, bool activated, bool inputRestored, bool handleReleased, bool retained) =>
        new(
            command.RequestId,
            true,
            null,
            null,
            null,
            command.SessionId,
            command.DesktopName,
            DesktopSessionPolicy.WindowStation,
            CurrentSessionId(),
            _originalName,
            input,
            inputRestored,
            handleReleased,
            retained,
            activated,
            Environment.ProcessId,
            null);

    private bool TryGetOwned(DesktopCommand command, out CloseDesktopSafeHandle desktop, out DesktopCommandResult? failure)
    {
        desktop = null!;
        failure = null;
        if (!DesktopSessionPolicy.IsOwnedName(command.DesktopName) || !_owned.TryGetValue(command.DesktopName!, out desktop))
        {
            failure = DesktopCommandResult.Fail(command.RequestId, "desktop_session_not_found", "This host does not hold that test desktop.");
            return false;
        }

        return true;
    }

    private bool EnsureOriginal(string inputName, out string? error, out int? native)
    {
        error = null;
        native = null;
        if (_original is { IsInvalid: false })
        {
            return true;
        }

        if (DesktopSessionPolicy.IsSecure(inputName) || DesktopSessionPolicy.IsOwnedName(inputName))
        {
            error = "The current input desktop cannot be used as the restore target.";
            return false;
        }

        CloseDesktopSafeHandle? opened = OpenStationDesktop(inputName, out int? openNative);
        if (opened is null)
        {
            native = openNative;
            error = "The original input desktop could not be opened: " + new Win32Exception(openNative ?? 0).Message;
            return false;
        }

        _original = opened;
        _originalName = inputName;
        return true;
    }

    private static DesktopCommandResult RestoreInput(string requestId, string desktopName, string originalName)
    {
        if (!TryReadInput(out string? input, out string? code, out string? error, out int? native))
        {
            return DesktopCommandResult.Fail(requestId, code!, error!, native);
        }

        if (!string.Equals(input, desktopName, StringComparison.OrdinalIgnoreCase))
        {
            return new DesktopCommandResult(requestId, true, "input_desktop_changed", $"The input desktop is '{input}' and was left unchanged.", null, null, desktopName, DesktopSessionPolicy.WindowStation, CurrentSessionId(), originalName, input, false, false, false, false, Environment.ProcessId, null);
        }

        using CloseDesktopSafeHandle? original = OpenStationDesktop(originalName, out int? openNative);
        if (original is null)
        {
            return DesktopCommandResult.Fail(requestId, "desktop_unavailable", "The recorded input desktop could not be opened: " + new Win32Exception(openNative ?? 0).Message, openNative);
        }

        using (CloseDesktopSafeHandle.LeaseScope lease = original.Lease())
        {
            if (!PInvoke.SwitchDesktop(lease.Value))
            {
                int win32 = Marshal.GetLastWin32Error();
                return DesktopCommandResult.Fail(requestId, "desktop_unavailable", "SwitchDesktop failed: " + new Win32Exception(win32).Message, win32);
            }
        }

        TryReadInput(out string? actual, out _, out _, out _);
        bool restored = string.Equals(actual, originalName, StringComparison.OrdinalIgnoreCase);
        return new DesktopCommandResult(requestId, restored, restored ? null : "desktop_unavailable", restored ? null : $"The input desktop is '{actual}'.", null, null, desktopName, DesktopSessionPolicy.WindowStation, CurrentSessionId(), originalName, actual, restored, false, false, false, Environment.ProcessId, null);
    }

    private static bool ProbeAndRelease(string desktopName, out int? native, out string? error)
    {
        native = null;
        error = null;
        CloseDesktopSafeHandle? opened = OpenStationDesktop(desktopName, out int? openNative);
        if (opened is null)
        {
            // Already gone, or never visible to this process. That is a released desktop, not a failure.
            return false;
        }

        opened.Dispose();
        return StillExists(desktopName);
    }

    private static bool StillExists(string desktopName)
    {
        using CloseDesktopSafeHandle? probe = OpenStationDesktop(desktopName, out _);
        return probe is not null;
    }

    private static unsafe CloseDesktopSafeHandle? CreateOwned(string name, out int? native)
    {
        native = null;
        fixed (char* pointer = name)
        {
            HDESK handle = PInvoke.CreateDesktop(pointer, null, null, default, DesktopAccess, null);
            if (handle.IsNull)
            {
                native = Marshal.GetLastWin32Error();
                return null;
            }

            return new CloseDesktopSafeHandle((IntPtr)handle.Value, ownsHandle: true);
        }
    }

    private static CloseDesktopSafeHandle? OpenStationDesktop(string name, out int? native)
    {
        native = null;
        CloseDesktopSafeHandle handle = PInvoke.OpenDesktop(name, default, false, DesktopAccess);
        if (handle.IsInvalid)
        {
            native = Marshal.GetLastWin32Error();
            handle.Dispose();
            return null;
        }

        return handle;
    }

    private static unsafe bool TryReadInput(out string? name, out string? code, out string? error, out int? native)
    {
        name = null;
        code = null;
        error = null;
        native = null;
        HDESK input = PInvoke.OpenInputDesktop(0, false, (DESKTOP_ACCESS_FLAGS)DesktopAccess);
        if (input.IsNull)
        {
            native = Marshal.GetLastWin32Error();
            code = native == 5 ? "secure_desktop_active" : "desktop_unavailable";
            error = native == 5
                ? "The input desktop is a secure desktop (lock screen, UAC, or Ctrl+Alt+Del). It was not opened or switched."
                : "The input desktop could not be opened: " + new Win32Exception(native ?? 0).Message;
            return false;
        }

        try
        {
            name = ReadName((HANDLE)input.Value);
        }
        finally
        {
            PInvoke.CloseDesktop(input);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            code = "desktop_unavailable";
            error = "The input desktop name could not be read.";
            return false;
        }

        if (DesktopSessionPolicy.IsSecure(name))
        {
            code = "secure_desktop_active";
            error = "The input desktop is a secure desktop (lock screen, UAC, or Ctrl+Alt+Del). It was not opened or switched.";
            name = null;
            return false;
        }

        return true;
    }

    private static string? TryReadInputName() =>
        TryReadInput(out string? name, out _, out _, out _) ? name : null;

    private static unsafe string? ReadName(HANDLE handle)
    {
        uint needed = 0;
        PInvoke.GetUserObjectInformation(handle, USER_OBJECT_INFORMATION_INDEX.UOI_NAME, null, 0, &needed);
        if (needed == 0)
        {
            return null;
        }

        byte[] buffer = new byte[needed];
        fixed (byte* pointer = buffer)
        {
            if (!PInvoke.GetUserObjectInformation(handle, USER_OBJECT_INFORMATION_INDEX.UOI_NAME, pointer, needed, &needed))
            {
                return null;
            }
        }

        return buffer.Length < 2 ? null : MemoryMarshal.Cast<byte, char>(buffer.AsSpan()).ToString().TrimEnd('\0');
    }

    private static unsafe int? CurrentSessionId()
    {
        uint session;
        unsafe
        {
            if (!PInvoke.ProcessIdToSessionId((uint)Environment.ProcessId, &session))
            {
                return null;
            }
        }

        return (int)session;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL EnumDesktopCallback(PWSTR name, LPARAM parameter)
    {
        List<string> names = (List<string>)GCHandle.FromIntPtr((nint)parameter.Value).Target!;
        string? value = name.ToString();
        if (!string.IsNullOrWhiteSpace(value))
        {
            names.Add(value);
        }

        return true;
    }
}
