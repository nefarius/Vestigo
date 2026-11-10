using System.Runtime.InteropServices;

using Vestigo.Desktop;
using Vestigo.Processes;

namespace Vestigo.Server.Tests;

public class WindowInvestigationWindowsTests
{
    [Fact]
    public void InspectAndPlace_UsesControlledWindowsOnly()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            Assert.Skip("Requires an interactive Windows desktop. This run does not have one.");
        }

        string decoratedClass = "VestigoInspectDecorated_" + Guid.NewGuid().ToString("N");
        string borderlessClass = "VestigoInspectBorderless_" + Guid.NewGuid().ToString("N");
        string rejectClass = "VestigoInspectReject_" + Guid.NewGuid().ToString("N");
        nint decorated = Create(decoratedClass, 0x00CF0000, DefaultProcedure);
        nint borderless = Create(borderlessClass, 0x80000000, DefaultProcedure);
        nint rejecting = Create(rejectClass, 0x00CF0000, RejectMove);
        try
        {
            Assert.NotEqual(0, decorated);
            Assert.NotEqual(0, borderless);
            Assert.NotEqual(0, rejecting);
            WindowsWindowInspectionSource source = new();
            WindowInspector inspector = new(source);
            OperationResult<WindowInspectionReport> decoratedReport = inspector.Inspect(Hex(decorated), Environment.ProcessId, null);
            OperationResult<WindowInspectionReport> borderlessReport = inspector.Inspect(Hex(borderless), Environment.ProcessId, null);
            Assert.True(decoratedReport.Success, decoratedReport.Error);
            Assert.True(borderlessReport.Success, borderlessReport.Error);
            Assert.Equal(DesktopCoordinates.PhysicalScreenPixels, decoratedReport.Value!.Geometry!.CoordinateSpace);
            Assert.True(decoratedReport.Value.DpiContext.Value!.Established);
            Assert.Equal(DesktopCoordinates.PerMonitorV2, decoratedReport.Value.DpiContext.Value.Actual);
            WindowInsets decoratedInsets = decoratedReport.Value.Geometry.Insets.Value!;
            WindowInsets borderlessInsets = borderlessReport.Value!.Geometry!.Insets.Value!;
            Assert.True(decoratedInsets.Top > borderlessInsets.Top);
            Assert.True(decoratedReport.Value.Geometry.ClientBounds.Value!.Width < decoratedReport.Value.Geometry.RawBounds.Value!.Width
                || decoratedReport.Value.Geometry.ClientBounds.Value.Height < decoratedReport.Value.Geometry.RawBounds.Value.Height);

            WindowPlacer placer = new(new WindowsWindowPlacementSource(), new WindowPlacementStore(TimeProvider.System));
            OperationResult<WindowPlacementResult> placed = placer.Place(new WindowPlacementRequest(
                Hex(decorated), Environment.ProcessId, null, -40, 40, 160, 90, DesktopCoordinates.PhysicalScreenPixels, true));
            Assert.True(placed.Success, placed.Error);
            OperationResult<WindowInspectionReport> after = inspector.Inspect(Hex(decorated), Environment.ProcessId, null);
            Assert.True(after.Success, after.Error);
            Assert.Equal(placed.Value!.MeasuredClient, after.Value!.Geometry!.ClientBounds.Value);
            if (after.Value.Geometry.RawBounds.Value!.X < 0)
            {
                Assert.True(placed.Value.MeasuredRaw!.X < 0);
            }

            if (placed.Value.Achieved)
            {
                Assert.Equal(-40, after.Value.Geometry.ClientBounds.Value.X);
            }
            else
            {
                Assert.Equal("placement_not_achieved", placed.Value.StopCode);
            }

            OperationResult<WindowPlacementResult> rejected = placer.Place(new WindowPlacementRequest(
                Hex(rejecting), Environment.ProcessId, null, 300, 300, 180, 120, DesktopCoordinates.PhysicalScreenPixels, false));
            Assert.True(rejected.Success, rejected.Error);
            Assert.False(rejected.Value!.Achieved);
            Assert.Equal("placement_not_achieved", rejected.Value.StopCode);

            string capture = Path.Combine(Path.GetTempPath(), "vestigo-window-events-" + Guid.NewGuid().ToString("N") + ".log");
            using StreamWriter writer = new(capture) { AutoFlush = true };
            using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(8));
            Task<int> loop = Task.Run(() => WindowEventCaptureLoop.Run(
                new WindowEventStart(Hex(decorated), Environment.ProcessId, null, TimeSpan.FromSeconds(6), 100),
                writer,
                cancel.Token));
            Assert.True(WaitFor(capture, "\"event\":\"location-changed\"", () =>
            {
                placer.Place(new WindowPlacementRequest(
                    Hex(decorated), Environment.ProcessId, null, 80, 80, 150, 100, DesktopCoordinates.PhysicalScreenPixels, false));
            }));
            cancel.Cancel();
            Assert.True(loop.Wait(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            Destroy(decorated, decoratedClass);
            Destroy(borderless, borderlessClass);
            Destroy(rejecting, rejectClass);
        }
    }

    [Fact]
    public async Task DisplayTransaction_RoundTripsWhenOptedIn()
    {
        if (!OperatingSystem.IsWindows() || !string.Equals(Environment.GetEnvironmentVariable("VESTIGO_DISPLAY_LAYOUT_TESTS"), "1", StringComparison.Ordinal))
        {
            Assert.Skip("Requires Windows and VESTIGO_DISPLAY_LAYOUT_TESTS=1 on a dedicated machine. This run did not opt in.");
        }

        string settings = Directory.CreateTempSubdirectory("vestigo-display-optin").FullName;
        string? previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", settings);
        try
        {
            ProcessLauncher launcher = new();
            HelperDisplayLayoutClient client = new(new SessionHelperClient(launcher), launcher);
            DisplayLayoutTransactionManager manager = new(client, new Vestigo.Configuration.VestigoOptions { Desktop = { AllowDisplayLayoutChanges = true } }, TimeProvider.System);
            OperationResult<DisplayLayoutSnapshot> snapshot = await manager.SnapshotAsync(false, CancellationToken.None);
            Assert.True(snapshot.Success, snapshot.Error);
            DisplayPathLayout path = snapshot.Value!.Paths[0];
            DisplayTargetChange same = new(
                path.Identity.MonitorDevicePath,
                path.Identity.ConnectorInstance,
                path.Identity.AdapterLuid,
                path.Identity.TargetId,
                path.X,
                path.Y,
                path.Width,
                path.Height,
                path.RefreshNumerator,
                path.RefreshDenominator,
                path.Rotation);
            OperationResult<DisplayLayoutTransaction> started = await manager.StartAsync([same], false, false, CancellationToken.None);
            try
            {
                Assert.True(started.Success, started.Error);
                OperationResult<DisplayLayoutObservation> observed = await manager.ObserveAsync(
                    started.Value!.Id,
                    new DisplayWait(TimeSpan.FromSeconds(10), 2, same),
                    CancellationToken.None);
                Assert.True(observed.Success, observed.Error);
                Assert.True(observed.Value!.Stable);
            }
            finally
            {
                if (started.Success)
                {
                    OperationResult<DisplayLayoutTransaction> restored = await manager.RestoreAsync(started.Value!.Id, CancellationToken.None);
                    Assert.True(restored.Value!.RestorationVerified, string.Join(' ', restored.Value.Differences));
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", previous);
            try
            {
                Directory.Delete(settings, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool WaitFor(string path, string text, Action nudge)
    {
        DateTimeOffset readyDeadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < readyDeadline && !File.ReadAllText(path).Contains("\"kind\":\"ready\"", StringComparison.Ordinal))
        {
            Thread.Sleep(20);
        }

        nudge();
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.ReadAllText(path).Contains(text, StringComparison.Ordinal))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    private static nint Create(string className, uint style, WndProc procedure)
    {
        WndClass windowClass = new()
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procedure),
            hInstance = GetModuleHandle(null),
            lpszClassName = className
        };
        if (RegisterClass(ref windowClass) == 0)
        {
            return 0;
        }

        return CreateWindowEx(0, className, "Vestigo Desktop Test", style, 120, 120, 240, 180, 0, 0, windowClass.hInstance, 0);
    }

    private static void Destroy(nint window, string className)
    {
        if (window != 0)
        {
            DestroyWindow(window);
        }

        UnregisterClass(className, GetModuleHandle(null));
    }

    private static string Hex(nint window) => "0x" + window.ToString("X");

    private static readonly WndProc DefaultProcedure = DefWindowProc;

    private static readonly WndProc RejectMove = (window, message, wParam, lParam) =>
    {
        if (message == 0x0046 && lParam != 0)
        {
            WindowPos position = Marshal.PtrToStructure<WindowPos>(lParam);
            position.flags |= 0x0001 | 0x0002;
            Marshal.StructureToPtr(position, lParam, false);
        }

        return DefWindowProc(window, message, wParam, lParam);
    };

    private delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public nint hwnd;
        public nint hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WndClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, nint instance);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? module);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
}
