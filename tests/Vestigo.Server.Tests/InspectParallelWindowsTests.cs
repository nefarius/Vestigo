using System.Diagnostics;
using System.Runtime.InteropServices;

using Vestigo.Desktop;
using Vestigo.Processes;
using Vestigo.Vision;

namespace Vestigo.Server.Tests;

/// <summary>Real window, capture, and UI Automation comparisons. Skipped without an interactive Windows desktop.</summary>
public class InspectParallelWindowsTests
{
    [Fact]
    public void ChangingWindow_SequentialAndParallelInspectionsMatch()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            Assert.Skip("Requires an interactive Windows desktop. This run does not have one.");
        }

        using Fixture window = Fixture.Show("Vestigo Parallel A");
        Inspection firstSequential = Inspection.Take(window.Handle, false);
        Inspection firstParallel = Inspection.Take(window.Handle, true);
        firstParallel.AssertMatches(firstSequential);
        Assert.Contains("Status ready", firstSequential.Elements, StringComparison.Ordinal);
        Assert.Contains("Fixture control", firstSequential.Elements, StringComparison.Ordinal);

        window.SetStatus("Status changed");
        Inspection textSequential = Inspection.Take(window.Handle, false);
        Inspection textParallel = Inspection.Take(window.Handle, true);
        textParallel.AssertMatches(textSequential);
        Assert.Contains("Status changed", textSequential.Elements, StringComparison.Ordinal);
        Assert.DoesNotContain("Status ready", textSequential.Elements, StringComparison.Ordinal);

        window.RemoveControl();
        Inspection removedSequential = Inspection.Take(window.Handle, false);
        Inspection removedParallel = Inspection.Take(window.Handle, true);
        removedParallel.AssertMatches(removedSequential);
        Assert.DoesNotContain("Fixture control", removedSequential.Elements, StringComparison.Ordinal);

        window.AddExtra();
        Inspection addedSequential = Inspection.Take(window.Handle, false);
        Inspection addedParallel = Inspection.Take(window.Handle, true);
        addedParallel.AssertMatches(addedSequential);
        Assert.Contains("Extra control", addedSequential.Elements, StringComparison.Ordinal);

        window.MoveAndRename("Vestigo Parallel B", 240, 180, 420, 260);
        Inspection secondSequential = Inspection.Take(window.Handle, false);
        Inspection secondParallel = Inspection.Take(window.Handle, true);
        secondParallel.AssertMatches(secondSequential);
        Assert.NotEqual(firstSequential.Title, secondSequential.Title);
        Assert.NotEqual(firstSequential.Bounds, secondSequential.Bounds);
        Assert.Contains("Vestigo Parallel B", secondSequential.Title, StringComparison.Ordinal);

        window.Close();
        OperationResult<InspectCaptureResult> closedSequential = window.Inspect(false);
        OperationResult<InspectCaptureResult> closedParallel = window.Inspect(true);
        Assert.Equal("window_not_found", closedSequential.ErrorCode);
        Assert.Equal(closedSequential.ErrorCode, closedParallel.ErrorCode);
    }

    [Fact]
    public void SlowUia_StuckHelperProcessIsStopped()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            Assert.Skip("Requires an interactive Windows desktop. This run does not have one.");
        }

        string dll = Path.Combine(AppContext.BaseDirectory, "Vestigo.dll");
        if (!File.Exists(dll))
        {
            Assert.Skip("Vestigo.dll is not beside the test host.");
        }

        using Fixture window = Fixture.Show("Vestigo Slow UIA");
        window.FreezeMessagePump();
        ProcessStartInfo start = new()
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add("--session-helper");
        start.ArgumentList.Add("inspect-screen");
        start.ArgumentList.Add("--target");
        start.ArgumentList.Add("window");
        start.ArgumentList.Add("--hwnd");
        start.ArgumentList.Add(window.Hex);
        start.ArgumentList.Add("--cursor");
        start.ArgumentList.Add("false");
        start.ArgumentList.Add("--max-edge");
        start.ArgumentList.Add("0");
        start.ArgumentList.Add("--full");
        start.ArgumentList.Add("true");
        start.ArgumentList.Add("--max-bytes");
        start.ArgumentList.Add("2000000");
        start.ArgumentList.Add("--uia");
        start.ArgumentList.Add("true");
        start.ArgumentList.Add("--ocr");
        start.ArgumentList.Add("false");

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("The inspection helper did not start.");
        if (process.WaitForExit(3000))
        {
            string output = process.StandardOutput.ReadToEnd();
            Assert.Skip("UI Automation returned while the window thread was frozen, so process cleanup was not required. " + output);
        }

        process.Kill(entireProcessTree: true);
        Assert.True(process.WaitForExit(3000));
    }

    private sealed class Inspection
    {
        private Inspection(string title, DesktopRect bounds, string uiaStatus, string ocrStatus, string elements)
        {
            Title = title;
            Bounds = bounds;
            UiaStatus = uiaStatus;
            OcrStatus = ocrStatus;
            Elements = elements;
        }

        public string Title { get; }

        public DesktopRect Bounds { get; }

        public string UiaStatus { get; }

        public string OcrStatus { get; }

        public string Elements { get; }

        public static Inspection Take(nint handle, bool parallel)
        {
            OperationResult<InspectCaptureResult> result = Fixture.Inspect(handle, parallel);
            Assert.True(result.Success, result.Error);
            InspectCaptureResult value = result.Value!;
            string elements = string.Join(
                '\n',
                value.Elements
                    .Where(item => item.Source == "uia")
                    .Select(item => $"{item.Role}|{item.Name}|{item.Text}|{item.Box.X},{item.Box.Y},{item.Box.Width},{item.Box.Height}"));
            return new Inspection(value.Capture.WindowTitle ?? "", value.Capture.Bounds, value.UiaStatus, value.OcrStatus, elements);
        }

        public void AssertMatches(Inspection other)
        {
            Assert.Equal(other.Title, Title);
            Assert.Equal(other.Bounds, Bounds);
            Assert.Equal(other.UiaStatus, UiaStatus);
            Assert.Equal(other.OcrStatus, OcrStatus);
            Assert.Equal(other.Elements, Elements);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private const uint TopLevelStyle = 0x10CF0000;
        private const uint ChildStyle = 0x50000000;

        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly ManualResetEventSlim _frozen = new(false);
        private readonly ManualResetEventSlim _pumpFrozen = new(false);
        private readonly Queue<Action> _commands = new();
        private nint _window;
        private nint _status;
        private nint _control;
        private nint _extra;
        private int _createError;

        private Fixture(Thread thread)
        {
            _thread = thread;
        }

        public nint Handle => _window;

        public string Hex => "0x" + _window.ToString("X");

        public static Fixture Show(string title)
        {
            Fixture fixture = null!;
            Thread thread = new(() => fixture.Run(title)) { IsBackground = true };
            fixture = new Fixture(thread);
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(fixture._ready.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(fixture._window != 0, $"CreateWindowEx failed with Win32 error {fixture._createError}.");
            return fixture;
        }

        public void SetStatus(string text) => OnWindow(() => Assert.True(SetWindowText(_status, text)));

        public void RemoveControl() => OnWindow(() =>
        {
            Assert.NotEqual(0, _control);
            Assert.True(DestroyWindow(_control), $"DestroyWindow failed with Win32 error {Marshal.GetLastWin32Error()}.");
            _control = 0;
        });

        public void AddExtra() => OnWindow(() =>
        {
            _extra = CreateWindowEx(0, "Static", "Extra control", ChildStyle, 12, 84, 180, 24, _window, 0, GetModuleHandle(null), 0);
            Assert.True(_extra != 0, $"CreateWindowEx failed with Win32 error {Marshal.GetLastWin32Error()}.");
        });

        public void MoveAndRename(string title, int x, int y, int width, int height) => OnWindow(() =>
        {
            Assert.True(SetWindowText(_window, title));
            Assert.True(SetWindowPos(_window, 0, x, y, width, height, 0x0014));
        });

        private void OnWindow(Action action)
        {
            Exception? error = null;
            using ManualResetEventSlim done = new(false);
            lock (_commands)
            {
                _commands.Enqueue(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    finally
                    {
                        done.Set();
                    }
                });
            }

            Assert.True(done.Wait(TimeSpan.FromSeconds(2)), "The window thread did not run the command.");
            if (error is not null)
            {
                throw error;
            }

            Thread.Sleep(150);
        }

        public void FreezeMessagePump()
        {
            _frozen.Set();
            Assert.True(_pumpFrozen.Wait(TimeSpan.FromSeconds(2)));
        }

        public void Close()
        {
            _stop.Set();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(3)));
        }

        public OperationResult<InspectCaptureResult> Inspect(bool parallel) => Inspect(_window, parallel);

        public static OperationResult<InspectCaptureResult> Inspect(nint handle, bool parallel)
        {
            ScreenCaptureRequest request = ScreenCaptureRequest.DesktopPreview with
            {
                Target = ScreenCaptureTarget.Window,
                WindowHandle = "0x" + handle.ToString("X"),
                MaxEdge = 0,
                IncludeFullImage = true,
                MaxBytes = 8L * 1024 * 1024
            };
            return SessionHelper.ExecuteInspect(request, true, true, parallel);
        }

        public void Dispose()
        {
            _stop.Set();
            _frozen.Set();
            if (!_thread.Join(TimeSpan.FromSeconds(5)))
            {
                _thread.Interrupt();
            }

            _ready.Dispose();
            _stop.Dispose();
            _frozen.Dispose();
            _pumpFrozen.Dispose();
        }

        private void Run(string title)
        {
            _window = CreateWindowEx(0, "Static", title, TopLevelStyle, 80, 80, 420, 260, 0, 0, GetModuleHandle(null), 0);
            if (_window == 0)
            {
                _createError = Marshal.GetLastWin32Error();
                _ready.Set();
                return;
            }

            _status = CreateWindowEx(0, "Static", "Status ready", ChildStyle, 12, 12, 180, 24, _window, 0, GetModuleHandle(null), 0);
            _control = CreateWindowEx(0, "Static", "Fixture control", ChildStyle, 12, 48, 180, 24, _window, 0, GetModuleHandle(null), 0);
            ShowWindow(_window, 5);
            _ready.Set();
            while (!_stop.IsSet)
            {
                Drain();
                if (_frozen.IsSet)
                {
                    _pumpFrozen.Set();
                    _stop.Wait(200);
                    continue;
                }

                while (PeekMessage(out Msg message, 0, 0, 0, 1))
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }

                if (!_stop.Wait(15))
                {
                    continue;
                }
            }

            if (_window != 0)
            {
                DestroyWindow(_window);
                _window = 0;
            }
        }

        private void Drain()
        {
            while (true)
            {
                Action? command;
                lock (_commands)
                {
                    if (_commands.Count == 0)
                    {
                        return;
                    }

                    command = _commands.Dequeue();
                }

                command();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetWindowText(nint window, string text);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg message, nint window, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Msg message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? module);
}
