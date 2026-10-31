using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using Windows.Win32;
using Windows.Win32.Foundation;

namespace Vestigo.Processes;

/// <summary>A process that matched a close request.</summary>
public sealed record MatchedProcess(int Pid, string Name, int WindowsPosted);

/// <summary>A process that was still running after the grace period. Vestigo never kills it.</summary>
public sealed record SurvivingProcess(int Pid, string Name);

/// <summary>Result of a graceful close attempt.</summary>
public sealed record CloseReport(
    IReadOnlyList<MatchedProcess> Matched,
    IReadOnlyList<SurvivingProcess> StillRunning,
    int WindowsPosted);

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

/// <summary>
///     Posts WM_CLOSE to top-level windows on the calling thread's desktop and waits for the owners to exit.
///     Window enumeration only sees the caller's desktop, which is why interactive closes run through the
///     session helper (<c>Vestigo.exe --session-helper close-window</c>) inside the console user's session.
/// </summary>
public static class WindowCloser
{
    public static CloseReport Close(CloseSelector selector, TimeSpan grace, int excludePid)
    {
        Regex? titleRegex = CloseSelector.CompileTitle(selector.TitlePattern);

        EnumState state = new(selector, titleRegex, excludePid);
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

        Dictionary<int, int> posted = state.Posted;
        Dictionary<int, string> names = state.Names;
        List<MatchedProcess> matched = posted
            .Select(pair => new MatchedProcess(pair.Key, names[pair.Key], pair.Value))
            .OrderBy(item => item.Pid)
            .ToList();

        DateTime deadline = DateTime.UtcNow + grace;
        List<SurvivingProcess> surviving = [];
        foreach (MatchedProcess item in matched)
        {
            try
            {
                using Process process = Process.GetProcessById(item.Pid);
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (!process.WaitForExit(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero))
                {
                    surviving.Add(new SurvivingProcess(item.Pid, item.Name));
                }
            }
            catch (ArgumentException)
            {
                // Exited already.
            }
        }

        return new CloseReport(matched, surviving, matched.Sum(item => item.WindowsPosted));
    }

    private sealed class EnumState(CloseSelector selector, Regex? titleRegex, int excludePid)
    {
        public Dictionary<int, int> Posted { get; } = [];

        public Dictionary<int, string> Names { get; } = [];

        public void Visit(HWND window)
        {
            if (!PInvoke.IsWindowVisible(window))
            {
                return;
            }

            uint pid;
            unsafe
            {
                PInvoke.GetWindowThreadProcessId(window, &pid);
            }

            if (pid == 0 || (int)pid == excludePid)
            {
                return;
            }

            if (!Names.TryGetValue((int)pid, out string? name))
            {
                name = ProcessName((int)pid);
                Names[(int)pid] = name;
            }

            if (selector.Matches((int)pid, name, WindowTitle(window), titleRegex)
                && PInvoke.PostMessage(window, PInvoke.WM_CLOSE, default, default))
            {
                Posted[(int)pid] = Posted.GetValueOrDefault((int)pid) + 1;
            }
        }
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
    private static string ProcessName(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static unsafe string WindowTitle(HWND window)
    {
        int length = PInvoke.GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        char[] buffer = new char[length + 1];
        fixed (char* pointer = buffer)
        {
            int copied = PInvoke.GetWindowText(window, new PWSTR(pointer), buffer.Length);
            return new string(buffer, 0, Math.Max(copied, 0));
        }
    }
}
