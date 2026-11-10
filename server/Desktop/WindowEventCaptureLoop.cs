using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;

using Microsoft.Win32.SafeHandles;

using Vestigo.Configuration;
using Vestigo.Processes;
using Vestigo.Security;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Vestigo.Desktop;

/// <summary>
///     One out-of-context WinEvent hook and a message loop. The helper process hosts a single capture.
///     WinEvent does not report how many events it discarded before delivery.
/// </summary>
internal static class WindowEventCaptureLoop
{
    private const uint StillActive = 259;
    private static readonly object Gate = new();
    private static Sink? Current;

    public static int Run(WindowEventStart start, TextWriter output, CancellationToken cancellationToken)
    {
        lock (Gate)
        {
            if (Current is not null)
            {
                Write(output, new WindowEventMessage("error", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, "capture_busy", "This process already has a window-event capture.", null, null, null, null));
                return 2;
            }

            Current = new Sink(start, output);
        }

        try
        {
            return Current.Run(cancellationToken);
        }
        finally
        {
            lock (Gate)
            {
                Current = null;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Hook(HWINEVENTHOOK hook, uint ev, HWND hwnd, int idObject, int idChild, uint threadId, uint time)
    {
        try
        {
            Current?.OnHook(ev, hwnd, idObject);
        }
        catch (Exception)
        {
            // A hook callback must not let a managed exception cross the native boundary.
        }
    }

    private static void Write(TextWriter output, WindowEventMessage message)
    {
        output.WriteLine(JsonSerializer.Serialize(message, SessionHelper.Json));
        output.Flush();
    }

    private sealed class Sink(WindowEventStart start, TextWriter output)
    {
        private readonly Channel<WindowEventMessage> _messages = Channel.CreateBounded<WindowEventMessage>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true
        });

        private readonly WindowsWindowInspectionSource _windows = new();
        private long _dropped;
        private int _written;
        private HWINEVENTHOOK _hook;
        private int _processId;
        private DateTimeOffset? _processStart;
        private bool _startReadable;

        public int Run(CancellationToken cancellationToken)
        {
            if (!TryResolve(out string? errorCode, out string? error))
            {
                Write(output, Error(errorCode!, error!));
                return 2;
            }

            using SafeFileHandle? process = OpenWatch(_processId);
            Write(output, Ready());
            if (!TryHook(process))
            {
                Write(output, Error("capture_failed", "SetWinEventHook failed."));
                return 2;
            }

            try
            {
                DateTimeOffset deadline = DateTimeOffset.UtcNow + start.Duration;
                string reason = "completed";
                while (!cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow < deadline && _written < start.MaxEvents)
                {
                    reason = Wait(process, deadline, cancellationToken);
                    Drain();
                    if (reason is "process_exited")
                    {
                        break;
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    reason = "stopped";
                }
                else if (DateTimeOffset.UtcNow >= deadline)
                {
                    reason = "duration_elapsed";
                }
                else if (_written >= start.MaxEvents)
                {
                    reason = "limit_reached";
                }

                Drain();
                Write(output, new WindowEventMessage("completed", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null, null, _dropped, reason, null, null, null, null, null, null));
                return 0;
            }
            finally
            {
                if (!_hook.IsNull)
                {
                    PInvoke.UnhookWinEvent(_hook);
                }
            }
        }

        private bool TryResolve(out string? errorCode, out string? error)
        {
            errorCode = null;
            error = null;
            if (start.Handle is not null)
            {
                if (!_windows.TryParseHandle(start.Handle, out nint handle) || !_windows.IsWindow(handle))
                {
                    errorCode = "window_not_found";
                    error = "The window handle is not a window.";
                    return false;
                }

                OperationResult<WindowProcessIdentity> identity = _windows.ReadIdentity(handle);
                if (!identity.Success)
                {
                    errorCode = identity.ErrorCode;
                    error = identity.Error;
                    return false;
                }

                OperationResult<bool> check = WindowIdentityGuard.Check(identity.Value!, start.ProcessId, start.ExpectedStartTime);
                if (!check.Success)
                {
                    errorCode = check.ErrorCode;
                    error = check.Error;
                    return false;
                }

                _processId = identity.Value!.ProcessId;
                _processStart = identity.Value.StartTime;
                _startReadable = identity.Value.StartTimeReadable;
                return true;
            }

            _processId = start.ProcessId!.Value;
            (bool readable, DateTimeOffset? started, int? native) = ReadStart(_processId);
            _startReadable = readable;
            _processStart = started;
            if (start.ExpectedStartTime is not null && (!readable || started?.UtcDateTime != start.ExpectedStartTime.Value.UtcDateTime))
            {
                errorCode = readable ? "process_identity_mismatch" : "process_identity_unreadable";
                error = "The process start time does not match expectedStartTime.";
                _ = native;
                return false;
            }

            return true;
        }

        private unsafe bool TryHook(SafeFileHandle? process)
        {
            HMODULE module = default;
            _hook = PInvoke.SetWinEventHook(
                PInvoke.EVENT_SYSTEM_FOREGROUND,
                PInvoke.EVENT_OBJECT_LOCATIONCHANGE,
                module,
                &Hook,
                (uint)_processId,
                0,
                PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
            _ = process;
            return !_hook.IsNull;
        }

        public void OnHook(uint ev, HWND hwnd, int idObject)
        {
            if ((OBJECT_IDENTIFIER)idObject != OBJECT_IDENTIFIER.OBJID_WINDOW && ev != PInvoke.EVENT_SYSTEM_FOREGROUND)
            {
                return;
            }

            string? name = ev switch
            {
                PInvoke.EVENT_OBJECT_CREATE => "created",
                PInvoke.EVENT_OBJECT_DESTROY => "destroyed",
                PInvoke.EVENT_OBJECT_SHOW => "shown",
                PInvoke.EVENT_OBJECT_HIDE => "hidden",
                PInvoke.EVENT_SYSTEM_FOREGROUND => "foreground",
                PInvoke.EVENT_OBJECT_LOCATIONCHANGE => "location-changed",
                _ => null
            };
            if (name is null || hwnd.IsNull)
            {
                return;
            }

            if (start.Handle is not null && !string.Equals(WindowInspection.FormatHandle(hwnd), Normalize(start.Handle), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            uint pid;
            unsafe
            {
                PInvoke.GetWindowThreadProcessId(hwnd, &pid);
            }

            if (pid != (uint)_processId)
            {
                return;
            }

            WindowGeometrySample sample = name == "destroyed"
                ? new WindowGeometrySample(null, null, null, null, [])
                : _windows.Sample((nint)hwnd);
            WindowEventMessage message = new(
                "observation",
                DateTimeOffset.UtcNow,
                name,
                WindowInspection.FormatHandle(hwnd),
                (int)pid,
                _processStart,
                _startReadable,
                UntrustedText.Trim(WindowInspection.ClassName(hwnd), 128),
                UntrustedText.Trim(WindowInspection.Title(hwnd), 256),
                sample.CoordinateSpace,
                sample.Raw,
                sample.Client,
                sample.Dpi,
                sample.Errors.Count == 0 ? null : [..sample.Errors],
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
            if (!_messages.Writer.TryWrite(message))
            {
                Interlocked.Increment(ref _dropped);
            }
        }

        private string Wait(SafeFileHandle? process, DateTimeOffset deadline, CancellationToken cancellationToken)
        {
            uint timeout = (uint)Math.Clamp((deadline - DateTimeOffset.UtcNow).TotalMilliseconds, 1, 200);
            WAIT_EVENT wake = process is null
                ? WaitMessages(timeout)
                : WaitProcess(process, timeout);

            if (process is not null && (uint)wake == 0)
            {
                EmitExit(process);
                return "process_exited";
            }

            Dispatch();
            if (process is not null && Exited(process, out int code))
            {
                EmitExit(process, code);
                return "process_exited";
            }

            return cancellationToken.IsCancellationRequested ? "stopped" : "running";
        }

        private static WAIT_EVENT WaitProcess(SafeFileHandle process, uint timeout)
        {
            HANDLE native = (HANDLE)process.DangerousGetHandle();
            return PInvoke.MsgWaitForMultipleObjectsEx(
                [native],
                timeout,
                QUEUE_STATUS_FLAGS.QS_ALLINPUT,
                MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_INPUTAVAILABLE);
        }

        private static WAIT_EVENT WaitMessages(uint timeout) =>
            PInvoke.MsgWaitForMultipleObjectsEx(
                ReadOnlySpan<HANDLE>.Empty,
                timeout,
                QUEUE_STATUS_FLAGS.QS_ALLINPUT,
                MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_INPUTAVAILABLE);

        private static void Dispatch()
        {
            while (PInvoke.PeekMessage(out MSG message, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                PInvoke.TranslateMessage(in message);
                PInvoke.DispatchMessage(in message);
            }
        }

        private void Drain()
        {
            while (_messages.Reader.TryRead(out WindowEventMessage? message))
            {
                Write(output, message);
                _written++;
            }

            if (_dropped > 0)
            {
                Write(output, new WindowEventMessage("hook-dropped", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null, null, _dropped, null, null, null, null, null, null, null));
            }
        }

        private void EmitExit(SafeFileHandle process, int? code = null)
        {
            int exit = code ?? (Exited(process, out int read) ? read : 0);
            bool known = code is not null || Exited(process, out exit);
            _messages.Writer.TryWrite(new WindowEventMessage(
                "observation", DateTimeOffset.UtcNow, "process-exited", start.Handle, _processId, _processStart, _startReadable,
                null, null, null, null, null, null, null, known ? exit : null, null, null, null, null, null, null, null, null));
        }

        private static bool Exited(SafeFileHandle process, out int code)
        {
            code = 0;
            unsafe
            {
                uint native;
                if (!PInvoke.GetExitCodeProcess((HANDLE)process.DangerousGetHandle(), &native))
                {
                    return false;
                }

                if (native == StillActive)
                {
                    return false;
                }

                code = unchecked((int)native);
                return true;
            }
        }

        private WindowEventMessage Ready() => new(
            "ready",
            DateTimeOffset.UtcNow,
            null,
            start.Handle,
            _processId,
            _processStart,
            _startReadable,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Environment.ProcessId,
            ReadOwnStart(),
            start.IncludesNewWindows,
            false);

        private static WindowEventMessage Error(string code, string error) =>
            new("error", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, code, error, null, null, null, null);

        private static SafeFileHandle? OpenWatch(int pid)
        {
            HANDLE handle = PInvoke.OpenProcess(
                PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE,
                false,
                (uint)pid);
            return handle.IsNull ? null : new SafeFileHandle(handle, ownsHandle: true);
        }

        private static (bool Readable, DateTimeOffset? Start, int? Error) ReadStart(int pid)
        {
            using SafeFileHandle? process = OpenWatch(pid);
            if (process is null || !ProcessStartTime.TryRead(process, out DateTimeOffset start, out int error))
            {
                return (false, null, Marshal.GetLastWin32Error());
            }

            _ = error;
            return (true, start, null);
        }

        private static DateTimeOffset? ReadOwnStart()
        {
            using SafeFileHandle? process = OpenWatch(Environment.ProcessId);
            return process is not null && ProcessStartTime.TryRead(process, out DateTimeOffset start, out _) ? start : null;
        }

        private static string Normalize(string handle) =>
            handle.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? handle : "0x" + handle;
    }
}

/// <summary>Stops a helper that outlived the service, only when its start time still matches.</summary>
internal static class WindowEventProcessCleanup
{
    private static readonly object Gate = new();

    public static void Remember(int? pid, DateTimeOffset? start)
    {
        if (pid is not int processId || start is null || !OperatingSystem.IsWindows())
        {
            return;
        }

        lock (Gate)
        {
            List<OwnedWindowEventProcess> items = Load();
            items.RemoveAll(item => item.ProcessId == processId);
            items.Add(new OwnedWindowEventProcess(processId, start.Value, Environment.ProcessPath ?? string.Empty));
            Save(items);
        }
    }

    public static void Clear(int? pid)
    {
        if (pid is null)
        {
            return;
        }

        lock (Gate)
        {
            List<OwnedWindowEventProcess> items = Load();
            if (items.RemoveAll(item => item.ProcessId == pid) > 0)
            {
                Save(items);
            }
        }
    }

    public static void Recover()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (Gate)
        {
            List<OwnedWindowEventProcess> remaining = [];
            foreach (OwnedWindowEventProcess item in Load())
            {
                if (!StopIfOwned(item))
                {
                    remaining.Add(item);
                }
            }

            Save(remaining);
        }
    }

    private static bool StopIfOwned(OwnedWindowEventProcess item)
    {
        if (!string.Equals(item.ExecutablePath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        HANDLE handle = PInvoke.OpenProcess(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE,
            false,
            (uint)item.ProcessId);
        if (handle.IsNull)
        {
            return true;
        }

        using SafeFileHandle process = new(handle, ownsHandle: true);
        if (!ProcessStartTime.TryRead(process, out DateTimeOffset start, out _) || start.UtcDateTime != item.StartTime.UtcDateTime)
        {
            return true;
        }

        return PInvoke.TerminateProcess(process, 1);
    }

    private static List<OwnedWindowEventProcess> Load()
    {
        try
        {
            return File.Exists(VestigoPaths.OwnedWindowEventsPath)
                ? JsonSerializer.Deserialize<List<OwnedWindowEventProcess>>(File.ReadAllText(VestigoPaths.OwnedWindowEventsPath)) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void Save(List<OwnedWindowEventProcess> items)
    {
        StateDirectories.EnsureSubdirectory(VestigoPaths.SettingsDirectory);
        File.WriteAllText(VestigoPaths.OwnedWindowEventsPath, JsonSerializer.Serialize(items));
    }

    private sealed record OwnedWindowEventProcess(int ProcessId, DateTimeOffset StartTime, string ExecutablePath);
}
