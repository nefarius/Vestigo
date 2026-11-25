using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace Vestigo.Processes;

/// <summary>Outcome of one process after WM_CLOSE. Vestigo never kills the process.</summary>
public static class CloseOutcome
{
    public const string Exited = "exited";

    public const string StillRunning = "stillRunning";

    public const string Unknown = "unknown";
}

/// <summary>A process that matched a close request.</summary>
public sealed record MatchedProcess(int Pid, string Name, int WindowsPosted);

/// <summary>A process that was still running after the grace period. Vestigo never kills it.</summary>
public sealed record SurvivingProcess(int Pid, string Name);

/// <summary>Verification of one process that matched a close request.</summary>
public sealed record CloseProcessOutcome(
    int Pid,
    string Name,
    string Outcome,
    int WindowsPosted,
    string? PostError = null,
    string? VerificationError = null,
    bool PidReused = false);

/// <summary>Result of a graceful close attempt. Outcomes is omitted by older helper JSON.</summary>
public sealed record CloseReport(
    IReadOnlyList<MatchedProcess> Matched,
    IReadOnlyList<SurvivingProcess> StillRunning,
    int WindowsPosted,
    IReadOnlyList<CloseProcessOutcome>? Outcomes = null);

/// <summary>Filters applied together (AND) to decide which windows receive WM_CLOSE.</summary>
public sealed record CloseSelector(IReadOnlyList<int> Pids, IReadOnlyList<string> ProcessNames, string? TitlePattern)
{
    public bool IsEmpty => Pids.Count == 0 && ProcessNames.Count == 0 && string.IsNullOrEmpty(TitlePattern);

    /// <summary>Tests one window. Pids and names are alternatives of each other; the title narrows them.</summary>
    public bool Matches(int pid, string processName, string title, Regex? titleRegex)
    {
        bool byProcess = Pids.Count == 0 && ProcessNames.Count == 0
                         || Pids.Contains(pid)
                         || ProcessNames.Any(name => NamesEqual(name, processName));
        return byProcess && (titleRegex is null || titleRegex.IsMatch(title));
    }

    public static bool NamesEqual(string requested, string actual) =>
        string.Equals(Normalize(requested), Normalize(actual), StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    public static Regex? CompileTitle(string? pattern) =>
        string.IsNullOrEmpty(pattern)
            ? null
            : new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
}

internal interface IWindowClosePlatform
{
    IReadOnlyList<VisibleWindow> EnumerateVisibleWindows();

    bool TryPostClose(nint hwnd, out int error);

    /// <summary>Called before any WM_CLOSE for this pid.</summary>
    ProcessIdentity CaptureIdentity(int pid);

    CloseWaitResult Wait(ProcessIdentity identity, TimeSpan remaining);
}

internal sealed record VisibleWindow(nint Hwnd, int Pid, string Name, string Title);

internal sealed record ProcessIdentity(int Pid, nint Handle, DateTimeOffset? StartTime, int CaptureError);

internal enum CloseWaitKind
{
    Exited,
    StillRunning,
    Unknown,
    Reused
}

internal readonly record struct CloseWaitResult(CloseWaitKind Kind, string? Error);

/// <summary>
///     Posts WM_CLOSE to top-level windows on the calling thread's desktop and reports whether each owner
///     exited, is still running, or could not be verified. Window enumeration only sees the caller's desktop,
///     which is why interactive closes run through the session helper
///     (<c>Vestigo.exe --session-helper close-window</c>) inside the console user's session.
/// </summary>
public static class WindowCloser
{
    private const int ErrorInvalidParameter = 87;

    public static CloseReport Close(CloseSelector selector, TimeSpan grace, int excludePid) =>
        Close(selector, grace, excludePid, new WindowsWindowClosePlatform());

    internal static CloseReport Close(CloseSelector selector, TimeSpan grace, int excludePid, IWindowClosePlatform platform)
    {
        Regex? titleRegex = CloseSelector.CompileTitle(selector.TitlePattern);
        Dictionary<int, List<VisibleWindow>> matchedWindows = [];
        Dictionary<int, string> names = [];
        foreach (VisibleWindow window in platform.EnumerateVisibleWindows())
        {
            if (window.Pid == 0 || window.Pid == excludePid || !selector.Matches(window.Pid, window.Name, window.Title, titleRegex))
            {
                continue;
            }

            if (!matchedWindows.TryGetValue(window.Pid, out List<VisibleWindow>? windows))
            {
                windows = [];
                matchedWindows[window.Pid] = windows;
                names[window.Pid] = window.Name;
            }
            else if (names[window.Pid].Length == 0 && window.Name.Length > 0)
            {
                names[window.Pid] = window.Name;
            }

            windows.Add(window);
        }

        List<int> pids = matchedWindows.Keys.OrderBy(pid => pid).ToList();
        Dictionary<int, ProcessIdentity> identities = [];
        foreach (int pid in pids)
        {
            identities[pid] = platform.CaptureIdentity(pid);
        }

        Dictionary<int, int> posted = [];
        Dictionary<int, string> postErrors = [];
        foreach (int pid in pids)
        {
            foreach (VisibleWindow window in matchedWindows[pid])
            {
                if (platform.TryPostClose(window.Hwnd, out int error))
                {
                    posted[pid] = posted.GetValueOrDefault(pid) + 1;
                }
                else if (!postErrors.ContainsKey(pid))
                {
                    postErrors[pid] = error.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        DateTime deadline = DateTime.UtcNow + (grace > TimeSpan.Zero ? grace : TimeSpan.Zero);
        List<MatchedProcess> matched = [];
        List<SurvivingProcess> surviving = [];
        List<CloseProcessOutcome> outcomes = [];
        foreach (int pid in pids)
        {
            int windowsPosted = posted.GetValueOrDefault(pid);
            string name = names[pid];
            ProcessIdentity identity = identities[pid];
            string outcome;
            string? verificationError = null;
            bool pidReused = false;
            if (identity.Handle == 0 && identity.CaptureError == ErrorInvalidParameter)
            {
                outcome = CloseOutcome.Exited;
            }
            else
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                CloseWaitResult wait = platform.Wait(identity, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                switch (wait.Kind)
                {
                    case CloseWaitKind.Exited:
                        outcome = CloseOutcome.Exited;
                        break;
                    case CloseWaitKind.StillRunning:
                        outcome = CloseOutcome.StillRunning;
                        surviving.Add(new SurvivingProcess(pid, name));
                        break;
                    case CloseWaitKind.Reused:
                        outcome = CloseOutcome.Exited;
                        pidReused = true;
                        verificationError = "pid_reused";
                        break;
                    case CloseWaitKind.Unknown:
                    default:
                        outcome = CloseOutcome.Unknown;
                        verificationError = string.IsNullOrEmpty(wait.Error) ? "verification_failed" : wait.Error;
                        break;
                }
            }

            matched.Add(new MatchedProcess(pid, name, windowsPosted));
            outcomes.Add(new CloseProcessOutcome(
                pid,
                name,
                outcome,
                windowsPosted,
                postErrors.GetValueOrDefault(pid),
                verificationError,
                pidReused));
        }

        return new CloseReport(matched, surviving, matched.Sum(item => item.WindowsPosted), outcomes);
    }
}

internal sealed class WindowsWindowClosePlatform : IWindowClosePlatform
{
    private const int ErrorInvalidParameter = 87;

    private const uint WaitObject0 = 0;

    private const uint WaitTimeout = 258;

    private const int StartTimePollMilliseconds = 50;

    public IReadOnlyList<VisibleWindow> EnumerateVisibleWindows()
    {
        List<VisibleWindow> windows = [];
        EnumState state = new(windows);
        GCHandle handle = GCHandle.Alloc(state);
        try
        {
            unsafe
            {
                PInvoke.EnumWindows(&EnumCallback, (LPARAM)GCHandle.ToIntPtr(handle));
            }
        }
        finally
        {
            handle.Free();
        }

        return windows;
    }

    public bool TryPostClose(nint hwnd, out int error)
    {
        error = 0;
        if (PInvoke.PostMessage((HWND)hwnd, PInvoke.WM_CLOSE, default, default))
        {
            return true;
        }

        error = Marshal.GetLastWin32Error();
        return false;
    }

    public ProcessIdentity CaptureIdentity(int pid)
    {
        HANDLE handle = PInvoke.OpenProcess(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE,
            false,
            (uint)pid);
        if (handle.IsNull)
        {
            int error = Marshal.GetLastWin32Error();
            return error == ErrorInvalidParameter
                ? new ProcessIdentity(pid, 0, null, error)
                : CaptureStartTime(pid);
        }

        nint raw;
        unsafe
        {
            raw = (nint)handle.Value;
        }
        return ProcessStartTime.TryRead(raw, out DateTimeOffset start, out int readError)
            ? new ProcessIdentity(pid, raw, start, 0)
            : new ProcessIdentity(pid, raw, null, readError);
    }

    public CloseWaitResult Wait(ProcessIdentity identity, TimeSpan remaining)
    {
        if (identity.Handle != 0)
        {
            using SafeFileHandle handle = new(identity.Handle, ownsHandle: true);
            uint timeout = (uint)Math.Clamp(remaining.TotalMilliseconds, 0, int.MaxValue);
            uint status = unchecked((uint)PInvoke.WaitForSingleObject(handle, timeout));
            if (status == WaitObject0)
            {
                return new CloseWaitResult(CloseWaitKind.Exited, null);
            }

            if (status == WaitTimeout)
            {
                return new CloseWaitResult(CloseWaitKind.StillRunning, null);
            }

            int error = Marshal.GetLastWin32Error();
            string detail = error != 0
                ? error.ToString(CultureInfo.InvariantCulture)
                : status.ToString(CultureInfo.InvariantCulture);
            return new CloseWaitResult(CloseWaitKind.Unknown, detail);
        }

        if (identity.StartTime is { } expected)
        {
            return WaitByStartTime(identity.Pid, expected, remaining);
        }

        return WaitWithoutIdentity(identity);
    }

    private static ProcessIdentity CaptureStartTime(int pid)
    {
        HANDLE handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle.IsNull)
        {
            return new ProcessIdentity(pid, 0, null, Marshal.GetLastWin32Error());
        }

        using SafeFileHandle process = new(handle, ownsHandle: true);
        return ProcessStartTime.TryRead(process, out DateTimeOffset start, out int readError)
            ? new ProcessIdentity(pid, 0, start, 0)
            : new ProcessIdentity(pid, 0, null, readError);
    }

    private static CloseWaitResult WaitByStartTime(int pid, DateTimeOffset expected, TimeSpan remaining)
    {
        DateTime deadline = DateTime.UtcNow + (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        while (true)
        {
            HANDLE handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (handle.IsNull)
            {
                int error = Marshal.GetLastWin32Error();
                return error == ErrorInvalidParameter
                    ? new CloseWaitResult(CloseWaitKind.Exited, null)
                    : new CloseWaitResult(CloseWaitKind.Unknown, error.ToString(CultureInfo.InvariantCulture));
            }

            using SafeFileHandle process = new(handle, ownsHandle: true);
            if (!ProcessStartTime.TryRead(process, out DateTimeOffset start, out int readError))
            {
                return new CloseWaitResult(CloseWaitKind.Unknown, readError.ToString(CultureInfo.InvariantCulture));
            }

            if (start.UtcDateTime != expected.UtcDateTime)
            {
                return new CloseWaitResult(CloseWaitKind.Reused, null);
            }

            if (DateTime.UtcNow >= deadline)
            {
                return new CloseWaitResult(CloseWaitKind.StillRunning, null);
            }

            TimeSpan left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                return new CloseWaitResult(CloseWaitKind.StillRunning, null);
            }

            Thread.Sleep(left < TimeSpan.FromMilliseconds(StartTimePollMilliseconds) ? left : TimeSpan.FromMilliseconds(StartTimePollMilliseconds));
        }
    }

    private static CloseWaitResult WaitWithoutIdentity(ProcessIdentity identity)
    {
        HANDLE handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)identity.Pid);
        if (!handle.IsNull)
        {
            using SafeFileHandle process = new(handle, ownsHandle: true);
            string error = identity.CaptureError == 0
                ? "identity_unreadable"
                : identity.CaptureError.ToString(CultureInfo.InvariantCulture);
            return new CloseWaitResult(CloseWaitKind.Unknown, error);
        }

        int openError = Marshal.GetLastWin32Error();
        return openError == ErrorInvalidParameter
            ? new CloseWaitResult(CloseWaitKind.Exited, null)
            : new CloseWaitResult(CloseWaitKind.Unknown, openError.ToString(CultureInfo.InvariantCulture));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL EnumCallback(HWND window, LPARAM parameter)
    {
        try
        {
            ((EnumState)GCHandle.FromIntPtr((nint)parameter.Value).Target!).Visit(window);
        }
        catch (Exception)
        {
            // Never let an exception cross the native boundary.
        }

        return true;
    }

    private sealed class EnumState(List<VisibleWindow> windows)
    {
        public void Visit(HWND window)
        {
            if (!PInvoke.IsWindowVisible(window))
            {
                return;
            }

            uint pid;
            nint hwnd;
            unsafe
            {
                PInvoke.GetWindowThreadProcessId(window, &pid);
                hwnd = (nint)window.Value;
            }

            if (pid == 0)
            {
                return;
            }

            int processId = unchecked((int)pid);
            windows.Add(new VisibleWindow(hwnd, processId, WindowInspection.ProcessName(processId), WindowInspection.Title(window)));
        }
    }
}
