using System.Runtime.InteropServices;
using System.Text.Json;

using Vestigo.Configuration;
using Vestigo.Desktop;
using Vestigo.Jobs;
using Vestigo.Modules;
using Vestigo.Processes;

namespace Vestigo.Server.Tests;

public class DesktopTests
{
    [Fact]
    public void SessionHelper_RoundTripsListWindowsArguments()
    {
        WindowSelector selector = new([12, 34], ["notepad"], "Untitled.*", "Notepad", true, true, 25);

        string[] args = SessionHelper.BuildListWindowsArguments(selector).ToArray();

        Assert.True(SessionHelper.IsInvocation(args));
        Assert.True(SessionHelper.TryParseListWindows(args, out WindowSelector parsed, out string? error));
        Assert.Null(error);
        Assert.Equal([12, 34], parsed.Pids);
        Assert.Equal(["notepad"], parsed.ProcessNames);
        Assert.Equal("Untitled.*", parsed.TitlePattern);
        Assert.Equal("Notepad", parsed.ClassName);
        Assert.True(parsed.IncludeHidden);
        Assert.True(parsed.IncludeChildren);
        Assert.Equal(25, parsed.Limit);
    }

    [Fact]
    public void SessionHelper_RejectsBadListWindowsArguments()
    {
        Assert.False(SessionHelper.TryParseListWindows([SessionHelper.Flag, "other"], out _, out string? error));
        Assert.NotNull(error);
        Assert.False(SessionHelper.TryParseListWindows([SessionHelper.Flag, SessionHelper.ListWindowsCommand, "--pid", "x"], out _, out _));
        Assert.False(SessionHelper.TryParseListWindows([SessionHelper.Flag, SessionHelper.ListWindowsCommand, "--pid"], out _, out _));
        Assert.False(SessionHelper.TryParseListWindows([SessionHelper.Flag, SessionHelper.ListWindowsCommand, "--limit", "0"], out _, out _));
        Assert.False(SessionHelper.TryParseListWindows([SessionHelper.Flag, SessionHelper.ListWindowsCommand, "--include-hidden", "maybe"], out _, out _));
    }

    [Fact]
    public void SessionHelper_RoundTripsListDisplaysArguments()
    {
        string[] args = SessionHelper.BuildListDisplaysArguments().ToArray();

        Assert.True(SessionHelper.IsInvocation(args));
        Assert.True(SessionHelper.TryParseListDisplays(args, out string? error));
        Assert.Null(error);
        Assert.False(SessionHelper.TryParseListDisplays([SessionHelper.Flag, SessionHelper.ListDisplaysCommand, "--extra", "1"], out _));
        Assert.False(SessionHelper.TryParseListDisplays([SessionHelper.Flag, "other"], out _));
    }

    [Fact]
    public void SessionHelperClient_ParsesHelperOutput()
    {
        const string json = """{"windows":[],"displays":[],"truncated":false,"sessionId":1}""";

        OperationResult<WindowListResult> ok = SessionHelperClient.Parse<WindowListResult>("noise\n" + json + "\n", 0);
        Assert.True(ok.Success);
        Assert.Empty(ok.Value!.Windows);
        Assert.False(ok.Value.Truncated);
        Assert.Equal(1, ok.Value.SessionId);
        Assert.Equal("invalid_request", SessionHelperClient.Parse<WindowListResult>("""{"error":"bad"}""", 2).ErrorCode);
        Assert.Equal("helper_failed", SessionHelperClient.Parse<WindowListResult>("", 1).ErrorCode);
        Assert.Equal("helper_failed", SessionHelperClient.Parse<WindowListResult>("{oops", 0).ErrorCode);
    }

    [Fact]
    public void WindowSelector_CombinesProcessTitleAndClassFilters()
    {
        WindowSelector byName = new([], ["Notepad.exe"], "^Untitled", "Notepad", false, false, 10);
        System.Text.RegularExpressions.Regex regex = WindowSelector.CompileTitle(byName.TitlePattern)!;

        Assert.True(byName.Matches(1, "notepad", "Untitled - Notepad", "Notepad", regex));
        Assert.False(byName.Matches(1, "notepad", "Readme", "Notepad", regex));
        Assert.False(byName.Matches(1, "calc", "Untitled", "Notepad", regex));
        Assert.False(byName.Matches(1, "notepad", "Untitled - Notepad", "ConsoleWindowClass", regex));
        Assert.True(new WindowSelector([5], [], null, null, false, false, 10).Matches(5, "x", "", "", null));
        Assert.False(new WindowSelector([5], [], null, null, false, false, 10).Matches(6, "x", "", "", null));
        Assert.True(new WindowSelector([], [], "tit", null, false, false, 10).Matches(1, "any", "my title", "x", WindowSelector.CompileTitle("tit")));
    }

    [Fact]
    public void DesktopRect_DetectsIntersection()
    {
        DesktopRect left = new(0, 0, 100, 100);
        Assert.True(left.Intersects(new DesktopRect(50, 50, 100, 100)));
        Assert.False(left.Intersects(new DesktopRect(100, 0, 50, 50)));
        Assert.False(left.Intersects(new DesktopRect(0, 100, 50, 50)));
    }

    [Fact]
    public void Module_IsRegisteredDisabledByDefaultAndWarnsWithoutTokens()
    {
        VestigoModule module = Assert.Single(ModuleRegistry.All, candidate => candidate.Name == "Desktop");

        Assert.Contains(typeof(DesktopTools), module.ToolTypes);
        Assert.False(module.EnabledByDefault);
        Assert.Empty(module.Dependencies);
        Assert.DoesNotContain(ModuleRegistry.Enabled(new VestigoOptions()), item => item.Name == "Desktop");

        VestigoOptions open = new() { Modules = { ["Desktop"] = new ModuleSettings { Enabled = true } } };
        ModuleRegistry.ValidateConfiguration(open);
        string? warning = ModuleRegistry.GetPrivilegedModuleWarning(open);
        Assert.NotNull(warning);
        Assert.Contains("Desktop", warning);
        Assert.Contains("windows, titles, and displays", warning);
        Assert.DoesNotContain("administrator-equivalent", warning);

        VestigoOptions tokens = new() { ApiTokens = ["t"], Modules = { ["Desktop"] = new ModuleSettings { Enabled = true } } };
        Assert.Null(ModuleRegistry.GetPrivilegedModuleWarning(tokens));
    }

    [Fact]
    public async Task ListWindows_RejectsBadContextAndRegex()
    {
        DesktopTools tools = CreateTools(new UnavailableLauncher());

        Assert.Equal("invalid_request", (await tools.ListWindows(context: "desktop")).ErrorCode);
        Assert.Equal("invalid_request", (await tools.ListDisplays(context: "desktop")).ErrorCode);
        Assert.Equal("invalid_request", (await tools.ListWindows(titleRegex: "[")).ErrorCode);
        Assert.Equal("no_interactive_session", (await tools.ListWindows()).ErrorCode);
        Assert.Equal("no_interactive_session", (await tools.ListDisplays()).ErrorCode);
    }

    [Fact]
    public async Task ListWindows_ServiceContext_ClampsLimitAndFindsCreatedWindow()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            return;
        }

        string className = "VestigoDesktopTest_" + Guid.NewGuid().ToString("N");
        if (!TryCreateHiddenWindow(className, out nint hwnd))
        {
            return;
        }

        try
        {
            DesktopTools tools = CreateTools(new UnavailableLauncher());
            OperationResult<WindowListResult> listed = await tools.ListWindows(
                pid: Environment.ProcessId,
                className: className,
                includeHidden: true,
                limit: 0,
                context: "service");

            Assert.True(listed.Success, listed.Error);
            WindowInfo window = Assert.Single(listed.Value!.Windows);
            Assert.Equal(Environment.ProcessId, window.ProcessId);
            Assert.Equal(className, window.ClassName);
            Assert.False(window.Visible);
            Assert.StartsWith("0x", window.Handle, StringComparison.Ordinal);
            Assert.NotEmpty(listed.Value.Displays);
            Assert.Contains(listed.Value.Displays, display => display.GdiDeviceName.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase));
            Assert.False(listed.Value.Truncated);

            OperationResult<DisplayListResult> displays = await tools.ListDisplays(context: "service");
            Assert.True(displays.Success, displays.Error);
            Assert.NotEmpty(displays.Value!.Displays);
            Assert.Contains(displays.Value.Displays, display => display.GdiDeviceName.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DestroyWindow(hwnd);
            UnregisterClass(className, GetModuleHandle(null));
        }
    }

    [Fact]
    public void SessionHelper_SerializesWindowListResult()
    {
        WindowListResult result = new(
            [new WindowInfo(
                "0x1",
                "Title",
                "Class",
                5,
                6,
                "app",
                @"C:\app.exe",
                null,
                null,
                null,
                true,
                false,
                false,
                false,
                new DesktopRect(0, 0, 10, 10),
                new DesktopRect(0, 0, 10, 10),
                new DesktopSize(8, 8),
                96,
                @"\\.\DISPLAY1",
                [@"\\.\DISPLAY1"])],
            [new DisplayInfo(@"\\.\DISPLAY1", true, new DesktopRect(0, 0, 1920, 1080), new DesktopRect(0, 0, 1920, 1040), 96, null, [])],
            false,
            1);

        string json = JsonSerializer.Serialize(result, SessionHelper.Json);
        WindowListResult? parsed = JsonSerializer.Deserialize<WindowListResult>(json, SessionHelper.Json);
        Assert.NotNull(parsed);
        Assert.Equal("0x1", parsed.Windows.Single().Handle);
        Assert.Equal(@"\\.\DISPLAY1", parsed.Displays.Single().GdiDeviceName);
    }

    private static DesktopTools CreateTools(IProcessLauncher launcher) =>
        new(new DesktopInventory(launcher, new SessionHelperClient(launcher)));

    private sealed class UnavailableLauncher : IProcessLauncher
    {
        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) =>
            context == LaunchContext.Interactive
                ? OperationResult<bool>.Fail("no_interactive_session", "No user is logged on at the console.")
                : OperationResult<bool>.Ok(true);

        public Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken) =>
            throw new ProcessLaunchException("no_interactive_session", "No user is logged on at the console.");
    }

    private static readonly WndProc WindowProcedure = (window, message, wParam, lParam) =>
        DefWindowProc(window, message, wParam, lParam);

    private static bool TryCreateHiddenWindow(string className, out nint hwnd)
    {
        hwnd = 0;
        WndClass wc = new()
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
            hInstance = GetModuleHandle(null),
            lpszClassName = className
        };
        if (RegisterClass(ref wc) == 0)
        {
            return false;
        }

        hwnd = CreateWindowEx(0, className, "Vestigo Desktop Test", 0, 0, 0, 200, 100, 0, 0, wc.hInstance, 0);
        return hwnd != 0;
    }

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WndClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, nint instance);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? module);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
}
