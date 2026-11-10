using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Protocol;

using Vestigo.Abstractions;
using Vestigo.Configuration;
using Vestigo.Desktop;
using Vestigo.Files;
using Vestigo.Jobs;
using Vestigo.Modules;
using Vestigo.Processes;
using Vestigo.Security;
using Vestigo.SystemInfo;

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
        Assert.True(SessionHelper.TryParseListDisplays(args, out bool includeModes, out string? error));
        Assert.False(includeModes);
        Assert.Null(error);
        string[] modes = SessionHelper.BuildListDisplaysArguments(includeModes: true).ToArray();
        Assert.True(SessionHelper.TryParseListDisplays(modes, out includeModes, out _));
        Assert.True(includeModes);
        Assert.False(SessionHelper.TryParseListDisplays([SessionHelper.Flag, SessionHelper.ListDisplaysCommand, "--extra", "1"], out _, out _));
        Assert.False(SessionHelper.TryParseListDisplays([SessionHelper.Flag, "other"], out _, out _));
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
        Assert.False(new WindowSelector([], [], "Button", null, false, true, 10).Matches("", "Button", WindowSelector.CompileTitle("Button")));
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
        Assert.Contains("list and inspect windows, capture window events, and list displays", warning);
        Assert.DoesNotContain("move windows by client rectangle", warning);
        Assert.DoesNotContain("change the active display layout", warning);
        Assert.DoesNotContain("administrator-equivalent", warning);

        VestigoOptions tokens = new() { ApiTokens = ["t"], Modules = { ["Desktop"] = new ModuleSettings { Enabled = true } } };
        Assert.Null(ModuleRegistry.GetPrivilegedModuleWarning(tokens));

        open.Desktop.AllowScreenshots = true;
        string? shots = ModuleRegistry.GetPrivilegedModuleWarning(open);
        Assert.NotNull(shots);
        Assert.Contains("take screenshots of that desktop", shots);

        open.Desktop.AllowWindowPlacement = true;
        string? placement = ModuleRegistry.GetPrivilegedModuleWarning(open);
        Assert.NotNull(placement);
        Assert.Contains("move windows by client rectangle", placement);

        open.Desktop.AllowDisplayLayoutChanges = true;
        string? layout = ModuleRegistry.GetPrivilegedModuleWarning(open);
        Assert.NotNull(layout);
        Assert.Contains("change the active display layout", layout);
    }

    [Fact]
    public void DesktopOptions_RejectsMaxScreenshotBytesOutOfRange()
    {
        new DesktopOptions().Validate();
        new DesktopOptions { MaxScreenshotBytes = DesktopOptions.MinMaxScreenshotBytes }.Validate();
        new DesktopOptions { MaxScreenshotBytes = DesktopOptions.MaxMaxScreenshotBytes }.Validate();
        Assert.Throws<InvalidOperationException>(() => new DesktopOptions { MaxScreenshotBytes = DesktopOptions.MinMaxScreenshotBytes - 1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new DesktopOptions { MaxScreenshotBytes = DesktopOptions.MaxMaxScreenshotBytes + 1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { Desktop = { MaxScreenshotBytes = 0 } }.Validate());
    }

    [Fact]
    public void SessionHelper_RoundTripsCaptureArguments()
    {
        ScreenCaptureRequest request = new(
            ScreenCaptureTarget.Window,
            @"\\.\DISPLAY2",
            "0xABC",
            [12],
            ["notepad"],
            "Untitled.*",
            new DesktopRect(-10, 20, 640, 480),
            true,
            800,
            true,
            4096);

        string[] args = SessionHelper.BuildCaptureArguments(request).ToArray();

        Assert.True(SessionHelper.IsInvocation(args));
        Assert.True(SessionHelper.TryParseCapture(args, out ScreenCaptureRequest parsed, out string? error));
        Assert.Null(error);
        Assert.Equal(ScreenCaptureTarget.Window, parsed.Target);
        Assert.Equal(@"\\.\DISPLAY2", parsed.Display);
        Assert.Equal("0xABC", parsed.WindowHandle);
        Assert.Equal([12], parsed.Pids);
        Assert.Equal(["notepad"], parsed.ProcessNames);
        Assert.Equal("Untitled.*", parsed.TitlePattern);
        Assert.Equal(new DesktopRect(-10, 20, 640, 480), parsed.Region);
        Assert.True(parsed.IncludeCursor);
        Assert.Equal(800, parsed.MaxEdge);
        Assert.True(parsed.IncludeFullImage);
        Assert.Equal(4096, parsed.MaxBytes);
    }

    [Theory]
    [InlineData(ScreenCaptureTarget.Desktop)]
    [InlineData(ScreenCaptureTarget.Display)]
    [InlineData(ScreenCaptureTarget.Window)]
    [InlineData(ScreenCaptureTarget.Region)]
    public void SessionHelper_RoundTripsEachCaptureTarget(string target)
    {
        ScreenCaptureRequest request = ScreenCaptureRequest.DesktopPreview with
        {
            Target = target,
            Display = target == ScreenCaptureTarget.Display ? "primary" : null,
            WindowHandle = target == ScreenCaptureTarget.Window ? "0x1" : null,
            Region = target == ScreenCaptureTarget.Region ? new DesktopRect(1, 2, 3, 4) : null
        };

        Assert.True(SessionHelper.TryParseCapture(SessionHelper.BuildCaptureArguments(request).ToArray(), out ScreenCaptureRequest parsed, out string? error));
        Assert.Null(error);
        Assert.Equal(target, parsed.Target);
    }

    [Fact]
    public void SessionHelper_RejectsBadCaptureArguments()
    {
        Assert.False(SessionHelper.TryParseCapture([SessionHelper.Flag, "other"], out _, out string? error));
        Assert.NotNull(error);
        Assert.False(SessionHelper.TryParseCapture([SessionHelper.Flag, SessionHelper.CaptureScreenCommand, "--target", "camera"], out _, out _));
        Assert.False(SessionHelper.TryParseCapture([SessionHelper.Flag, SessionHelper.CaptureScreenCommand, "--rect", "1,2"], out _, out _));
        Assert.False(SessionHelper.TryParseCapture([SessionHelper.Flag, SessionHelper.CaptureScreenCommand, "--max-edge", "-1"], out _, out _));
        Assert.False(SessionHelper.TryParseCapture([SessionHelper.Flag, SessionHelper.CaptureScreenCommand, "--cursor", "maybe"], out _, out _));
        Assert.False(SessionHelper.TryParseCapture([SessionHelper.Flag, SessionHelper.CaptureScreenCommand, "--pid"], out _, out _));
    }

    [Fact]
    public void SessionHelperClient_HonorsHelperErrorCode()
    {
        OperationResult<ScreenCaptureResult> named = SessionHelperClient.Parse<ScreenCaptureResult>("""{"error":"gone","errorCode":"window_not_found"}""", 2);
        Assert.Equal("window_not_found", named.ErrorCode);
        Assert.Equal("gone", named.Error);
        Assert.Equal("invalid_request", SessionHelperClient.Parse<ScreenCaptureResult>("""{"error":"bad"}""", 2).ErrorCode);
    }

    [Fact]
    public void DesktopRect_ClipsToIntersection()
    {
        DesktopRect left = new(0, 0, 100, 100);
        Assert.Equal(new DesktopRect(50, 50, 50, 50), left.Intersection(new DesktopRect(50, 50, 100, 100)));
        Assert.Null(left.Intersection(new DesktopRect(100, 0, 50, 50)));
        Assert.Null(new DesktopRect(0, 0, 10, 10).Intersection(new DesktopRect(20, 20, 5, 5)));
    }

    [Fact]
    public void ScreenCapture_EncodesPngAndDownscales()
    {
        byte[] bgra =
        [
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255
        ];

        byte[] png = ScreenCapture.EncodePng(bgra, 2, 2);
        Assert.True(png.Length >= 24);
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
        Assert.Equal((byte)'N', png[2]);
        Assert.Equal((byte)'G', png[3]);
        Assert.Equal(new DesktopSize(2, 2), ScreenCapture.ReadPngSize(png));

        byte[] wide = new byte[40 * 20 * 4];
        for (int i = 0; i < wide.Length; i += 4)
        {
            wide[i + 2] = 128;
            wide[i + 3] = 255;
        }

        byte[] preview = ScreenCapture.EncodePng(wide, 40, 20, maxEdge: 10);
        Assert.Equal(new DesktopSize(10, 5), ScreenCapture.ReadPngSize(preview));
    }

    [Fact]
    public void TakeScreenshot_BuildsCallToolResultFromCapture()
    {
        byte[] png = ScreenCapture.EncodePng([255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255], 2, 2);
        CapturedScreenshot capture = new(
            new ScreenCaptureInfo(
                ScreenCaptureTarget.Desktop,
                new DesktopRect(0, 0, 2, 2),
                new DesktopSize(2, 2),
                new DesktopSize(2, 2),
                96,
                [@"\\.\DISPLAY1"],
                null,
                null,
                @"C:\shots\desk.png",
                png.Length,
                "abc",
                false,
                1),
            png);

        CallToolResult result = DesktopTools.SuccessResult(capture);
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(2, result.Content.Count);
        ImageContentBlock image = Assert.IsType<ImageContentBlock>(result.Content[0]);
        Assert.Equal("image/png", image.MimeType);
        TextContentBlock text = Assert.IsType<TextContentBlock>(result.Content[1]);
        ScreenCaptureInfo? info = JsonSerializer.Deserialize<ScreenCaptureInfo>(text.Text, SessionHelper.Json);
        Assert.NotNull(info);
        Assert.Equal(@"C:\shots\desk.png", info.SavedPath);
        Assert.DoesNotContain("PreviewPngBase64", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TakeScreenshot_AddsDownloadLinkToMetadata()
    {
        byte[] png = ScreenCapture.EncodePng([255, 0, 0, 255], 1, 1);
        CapturedScreenshot capture = new(
            new ScreenCaptureInfo(
                ScreenCaptureTarget.Desktop,
                new DesktopRect(0, 0, 1, 1),
                new DesktopSize(1, 1),
                new DesktopSize(1, 1),
                96,
                [@"\\.\DISPLAY1"],
                null,
                null,
                @"C:\shots\desk.png",
                png.Length,
                "abc",
                false,
                1),
            png);
        Vestigo.Files.FileLink link = new("http://h/files/shots/desk.png?ticket=t", DateTimeOffset.UtcNow, true, "curl", "ps");

        CallToolResult result = DesktopTools.SuccessResult(capture, link);

        TextContentBlock text = Assert.IsType<TextContentBlock>(result.Content[1]);
        using JsonDocument document = JsonDocument.Parse(text.Text);
        JsonElement download = document.RootElement.GetProperty("download");
        Assert.Equal("http://h/files/shots/desk.png?ticket=t", download.GetProperty("url").GetString());
        Assert.True(download.GetProperty("singleUse").GetBoolean());
    }

    [Fact]
    public void TakeScreenshot_ShareAddsResourceLinkAndHandoffMetadata()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4];
        CapturedScreenshot capture = new(
            new ScreenCaptureInfo(
                ScreenCaptureTarget.Desktop,
                new DesktopRect(0, 0, 1, 1),
                new DesktopSize(1, 1),
                new DesktopSize(1, 1),
                96,
                [@"\\.\DISPLAY1"],
                null,
                null,
                null,
                null,
                null,
                null,
                1),
            png);
        ScreenshotShareLink share = new(
            "http://h/desktop/shares/shot-0123abcd?ticket=t",
            DateTimeOffset.UnixEpoch,
            true,
            ScreenshotShareLink.PresentToUser);

        CallToolResult result = DesktopTools.SuccessResult(capture, share: share);

        Assert.Equal(3, result.Content.Count);
        Assert.IsType<ImageContentBlock>(result.Content[0]);
        ResourceLinkBlock link = Assert.IsType<ResourceLinkBlock>(result.Content[1]);
        Assert.Equal(share.Url, link.Uri);
        Assert.Equal("image/png", link.MimeType);
        Assert.Equal("vestigo-screenshot.png", link.Name);
        Assert.NotNull(link.Annotations);
        Assert.Contains(Role.User, link.Annotations.Audience);
        TextContentBlock text = Assert.IsType<TextContentBlock>(result.Content[2]);
        using JsonDocument document = JsonDocument.Parse(text.Text);
        JsonElement shared = document.RootElement.GetProperty("share");
        Assert.Equal(share.Url, shared.GetProperty("url").GetString());
        Assert.True(shared.GetProperty("singleUse").GetBoolean());
        Assert.Equal(ScreenshotShareLink.PresentToUser, shared.GetProperty("instruction").GetString());
        Assert.Contains("may not appear in chat", text.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviewPngBase64", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TakeScreenshot_SavedCaptureReportsShareFailureWithoutLink()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4];
        CapturedScreenshot capture = new(
            new ScreenCaptureInfo(
                ScreenCaptureTarget.Desktop,
                new DesktopRect(0, 0, 1, 1),
                new DesktopSize(1, 1),
                new DesktopSize(1, 1),
                96,
                [@"\\.\DISPLAY1"],
                null,
                null,
                @"C:\shots\desk.png",
                png.Length,
                "abc",
                false,
                1),
            png);
        OperationResult<ScreenshotShareLink> failure = OperationResult<ScreenshotShareLink>.Fail(
            "share_unavailable",
            "Too many screenshot shares are in use. Try again shortly.");

        CallToolResult result = DesktopTools.SuccessResult(capture, shareFailure: failure);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(2, result.Content.Count);
        Assert.IsType<ImageContentBlock>(result.Content[0]);
        Assert.DoesNotContain(result.Content, block => block is ResourceLinkBlock);
        using JsonDocument document = JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[1]).Text);
        Assert.False(document.RootElement.TryGetProperty("share", out _));
        JsonElement shareError = document.RootElement.GetProperty("shareError");
        Assert.Equal("share_unavailable", shareError.GetProperty("errorCode").GetString());
        Assert.Contains("in use", shareError.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(@"C:\shots\desk.png", document.RootElement.GetProperty("savedPath").GetString());
    }

    [Fact]
    public async Task TakeScreenshot_RefusesShareWithoutPreviewOrStore()
    {
        DesktopTools disabled = CreateTools(new UnavailableLauncher());
        Assert.Equal("screenshots_disabled", ErrorCode(await disabled.TakeScreenshot(share: true)));

        VestigoOptions enabled = new() { Desktop = { AllowScreenshots = true } };
        DesktopTools tools = CreateTools(new UnavailableLauncher(), enabled);
        CallToolResult noPreview = await tools.TakeScreenshot(share: true, maxEdge: 0);
        CallToolResult noStore = await tools.TakeScreenshot(share: true);

        Assert.Equal("share_unavailable", ErrorCode(noPreview));
        Assert.Contains("maxEdge", ErrorText(noPreview), StringComparison.Ordinal);
        Assert.Equal("share_unavailable", ErrorCode(noStore));
        Assert.Contains("not available", ErrorText(noStore), StringComparison.Ordinal);
    }

    [Fact]
    public void ShareStore_BindsTokenAndRejectsUnknownAndMismatch()
    {
        ScreenshotShareStore store = new(new FakeTime());
        (string id, string ticket) = Issued(store, [0x89, 0x50, 0x4E, 0x47]);

        Assert.Equal(ShareFailure.Missing, Failure(store, id, " "));
        Assert.Equal(ShareFailure.Unknown, Failure(store, id, "not-a-ticket"));
        Assert.Equal(ShareFailure.Mismatched, Failure(store, "shot-deadbeef", ticket));
        ScreenshotShareStore.Reservation reservation = store.TryReserve(id, ticket, out ShareFailure reserved)!;
        Assert.Equal(ShareFailure.None, reserved);
        Assert.Equal(ShareFailure.Replayed, Failure(store, id, ticket));
        store.Release(reservation);
        Assert.NotNull(store.TryReserve(id, ticket, out ShareFailure again));
        Assert.Equal(ShareFailure.None, again);
    }

    [Fact]
    public void ShareStore_ReleasesThenConsumesAndExpires()
    {
        FakeTime time = new();
        ScreenshotShareStore store = new(time, lifetime: TimeSpan.FromMinutes(10));
        (string id, string ticket) = Issued(store, [1, 2, 3, 4]);

        ScreenshotShareStore.Reservation reservation = store.TryReserve(id, ticket, out ShareFailure failure)!;
        Assert.Equal(ShareFailure.None, failure);
        store.Release(reservation);
        reservation = store.TryReserve(id, ticket, out failure)!;
        Assert.Equal(ShareFailure.None, failure);
        store.Consume(reservation);
        Assert.Equal(ShareFailure.Replayed, Failure(store, id, ticket));

        (id, ticket) = Issued(store, [5, 6, 7, 8]);
        time.Now += TimeSpan.FromMinutes(11);
        Assert.Equal(ShareFailure.Expired, Failure(store, id, ticket));
        Assert.Equal(ShareFailure.Unknown, Failure(store, id, ticket));
    }

    [Fact]
    public void ShareStore_EvictsOldestIdleEntryForCountAndBytes()
    {
        FakeTime time = new();
        ScreenshotShareStore store = new(time, maxEntries: 2, maxTotalBytes: 10);
        (string firstId, string firstTicket) = Issued(store, [1, 2, 3]);
        Issued(store, [4, 5, 6]);
        (string thirdId, string thirdTicket) = Issued(store, [7, 8, 9]);

        Assert.Equal(ShareFailure.Unknown, Failure(store, firstId, firstTicket));
        ScreenshotShareStore.Reservation third = store.TryReserve(thirdId, thirdTicket, out ShareFailure thirdFailure)!;
        Assert.Equal(ShareFailure.None, thirdFailure);
        store.Release(third);

        ScreenshotShareStore bytes = new(time, maxEntries: 8, maxTotalBytes: 6);
        (string largeId, string largeTicket) = Issued(bytes, [1, 2, 3, 4]);
        (string keptId, string keptTicket) = Issued(bytes, [5, 6, 7]);
        Assert.Equal(ShareFailure.Unknown, Failure(bytes, largeId, largeTicket));
        Assert.NotNull(bytes.TryReserve(keptId, keptTicket, out ShareFailure keptFailure));
        Assert.Equal(ShareFailure.None, keptFailure);

        ScreenshotShareStore busy = new(time, maxEntries: 1, maxTotalBytes: 8);
        (string onlyId, string onlyTicket) = Issued(busy, [1, 2]);
        Assert.NotNull(busy.TryReserve(onlyId, onlyTicket, out _));
        OperationResult<ScreenshotShareLink> refused = busy.Issue([3, 4], "http://lab");
        Assert.False(refused.Success);
        Assert.Equal("share_unavailable", refused.ErrorCode);
        Assert.Contains("in use", refused.Error, StringComparison.Ordinal);
        OperationResult<ScreenshotShareLink> tooLarge = busy.Issue(new byte[9], "http://lab");
        Assert.Equal("share_unavailable", tooLarge.ErrorCode);
        Assert.Contains("too large", tooLarge.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorResult_OmitsANullReasonAndKeepsAPresentOne()
    {
        CallToolResult absent = DesktopTools.ErrorResult("screenshots_disabled", "Screenshots are disabled.");
        using (JsonDocument missing = JsonDocument.Parse(ErrorText(absent)))
        {
            Assert.Equal("screenshots_disabled", missing.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal("Screenshots are disabled.", missing.RootElement.GetProperty("error").GetString());
            Assert.False(missing.RootElement.TryGetProperty("reason", out _));
        }

        CallToolResult present = DesktopTools.ErrorResult(
            "no_interactive_session",
            "No user is logged on at the console.",
            ConsoleSessionReasons.NobodyLoggedOn);
        using JsonDocument withReason = JsonDocument.Parse(ErrorText(present));
        Assert.Equal("no_interactive_session", withReason.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("No user is logged on at the console.", withReason.RootElement.GetProperty("error").GetString());
        Assert.Equal(ConsoleSessionReasons.NobodyLoggedOn, withReason.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ListWindows_RejectsBadContextAndRegex()
    {
        DesktopTools tools = CreateTools(new UnavailableLauncher());

        Assert.Equal("invalid_request", (await tools.ListWindows(context: "desktop")).ErrorCode);
        Assert.Equal("invalid_request", (await tools.ListDisplays(context: "desktop")).ErrorCode);
        Assert.Equal("invalid_request", (await tools.ListWindows(titleRegex: "[")).ErrorCode);
        OperationResult<WindowListResult> windows = await tools.ListWindows();
        Assert.Equal("no_interactive_session", windows.ErrorCode);
        Assert.Equal(ConsoleSessionReasons.NobodyLoggedOn, windows.Reason);
        Assert.Equal("no_interactive_session", (await tools.ListDisplays()).ErrorCode);
    }

    [Fact]
    public async Task TakeScreenshot_RefusesWhenDisabledOrInvalid()
    {
        DesktopTools tools = CreateTools(new UnavailableLauncher());

        Assert.Equal("screenshots_disabled", ErrorCode(await tools.TakeScreenshot()));
        Assert.Equal("screenshots_disabled", ErrorCode(await tools.TakeScreenshot(target: "camera")));

        VestigoOptions enabled = new() { Desktop = { AllowScreenshots = true } };
        DesktopTools allowed = CreateTools(new UnavailableLauncher(), enabled);
        Assert.Equal("invalid_request", ErrorCode(await allowed.TakeScreenshot(target: "camera")));
        Assert.Equal("invalid_request", ErrorCode(await allowed.TakeScreenshot(maxEdge: -1)));
        Assert.Equal("invalid_request", ErrorCode(await allowed.TakeScreenshot(target: "window", windowHandle: "0x1", pid: 1)));
        Assert.Equal("invalid_request", ErrorCode(await allowed.TakeScreenshot(target: "region", x: 0, y: 0)));
        CallToolResult missing = await allowed.TakeScreenshot();
        Assert.Equal("no_interactive_session", ErrorCode(missing));
        Assert.Equal(ConsoleSessionReasons.NobodyLoggedOn, ErrorReason(missing));
    }

    [Fact]
    public async Task TakeScreenshot_RefusesSaveToWithoutWriteRoot()
    {
        string root = Directory.CreateTempSubdirectory("vestigo-shot-").FullName;
        try
        {
            VestigoOptions options = new()
            {
                Desktop = { AllowScreenshots = true },
                Roots =
                {
                    new RootDefinition { Name = "shots", Path = root, Access = RootAccess.Read }
                }
            };

            DesktopTools tools = CreateTools(new UnavailableLauncher(), options);
            Assert.Equal("access_denied", ErrorCode(await tools.TakeScreenshot(saveTo: "shots:desk.png")));
            Assert.Equal("unknown_root", ErrorCode(await tools.TakeScreenshot(saveTo: "other:desk.png")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

            OperationResult<ScreenCaptureResult> shot = ScreenCapture.Capture(new ScreenCaptureRequest(
                ScreenCaptureTarget.Window,
                null,
                "0x" + hwnd.ToString("X"),
                [],
                [],
                null,
                null,
                false,
                64,
                false,
                DesktopOptions.DefaultMaxScreenshotBytes));
            if (shot.ErrorCode == "desktop_unavailable")
            {
                return;
            }

            Assert.True(shot.Success, shot.Error);
            Assert.Equal(ScreenCaptureTarget.Window, shot.Value!.Target);
            Assert.NotNull(shot.Value.PreviewPngBase64);
            Assert.Null(shot.Value.FullPngBase64);
            byte[] preview = Convert.FromBase64String(shot.Value.PreviewPngBase64);
            Assert.Equal(0x89, preview[0]);
            DesktopSize size = ScreenCapture.ReadPngSize(preview);
            Assert.True(size.Width > 0 && size.Height > 0);
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

    private static (string Id, string Ticket) Issued(ScreenshotShareStore store, byte[] png)
    {
        ScreenshotShareLink link = store.Issue(png, "http://lab").Value!;
        Uri uri = new(link.Url);
        string query = uri.Query.TrimStart('?');
        Assert.StartsWith("ticket=", query, StringComparison.Ordinal);
        string ticket = Uri.UnescapeDataString(query["ticket=".Length..]);
        return (uri.AbsolutePath.Split('/')[^1], ticket);
    }

    private static ShareFailure Failure(ScreenshotShareStore store, string id, string? ticket)
    {
        store.TryReserve(id, ticket, out ShareFailure failure);
        return failure;
    }

    private static string? ErrorCode(CallToolResult result)
    {
        Assert.True(result.IsError);
        using JsonDocument document = JsonDocument.Parse(ErrorText(result));
        return document.RootElement.GetProperty("errorCode").GetString();
    }

    private static string ErrorText(CallToolResult result)
    {
        TextContentBlock text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        return text.Text;
    }

    private static string? ErrorReason(CallToolResult result)
    {
        TextContentBlock text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using JsonDocument document = JsonDocument.Parse(text.Text);
        return document.RootElement.TryGetProperty("reason", out JsonElement reason) ? reason.GetString() : null;
    }

    private static DesktopTools CreateTools(IProcessLauncher launcher, VestigoOptions? options = null)
    {
        options ??= new VestigoOptions();
        FileDeployer deployer = new(new FakeRebootScheduler());
        return new DesktopTools(
            new DesktopInventory(launcher, new SessionHelperClient(launcher), deployer, options),
            options,
            new RootPolicy(options));
    }

    private sealed class UnavailableLauncher : IProcessLauncher
    {
        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) =>
            context == LaunchContext.Interactive
                ? OperationResult<bool>.Fail(
                    "no_interactive_session",
                    "No user is logged on at the console.",
                    ConsoleSessionReasons.NobodyLoggedOn)
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

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

[Collection("VestigoHost")]
public class ScreenshotShareEndpointTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-share-host").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");

    public ScreenshotShareEndpointTests()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _previous);
        try
        {
            Directory.Delete(_settings, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of temp data.
        }
    }

    [Fact]
    public async Task Get_ServesInlinePreviewOnceAndRejectsReplay()
    {
        await using WebApplicationFactory<Program> factory = Create(["secret"]);
        using HttpClient client = factory.CreateClient();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 9, 8, 7];
        ScreenshotShareLink link = factory.Services.GetRequiredService<ScreenshotShareStore>().Issue(png, "http://lab").Value!;
        string path = new Uri(link.Url).PathAndQuery;

        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret");
        HttpResponseMessage bearerOnly = await client.GetAsync(path.Split('?')[0]);
        client.DefaultRequestHeaders.Authorization = null;
        HttpResponseMessage first = await client.GetAsync(path);
        HttpResponseMessage second = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, bearerOnly.StatusCode);
        Assert.Contains("invalid_ticket", await bearerOnly.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(png, await first.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", first.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", first.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("vestigo-screenshot.png", first.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Contains("no-store", first.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Contains("already used", await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_RejectsUnknownMismatchAndKeepsTheMatchingTicket()
    {
        await using WebApplicationFactory<Program> factory = Create([]);
        using HttpClient client = factory.CreateClient();
        ScreenshotShareLink link = factory.Services.GetRequiredService<ScreenshotShareStore>().Issue([1, 2, 3, 4], "http://lab").Value!;
        Uri uri = new(link.Url);
        string ticket = Uri.UnescapeDataString(uri.Query.TrimStart('?')["ticket=".Length..]);

        HttpResponseMessage unknown = await client.GetAsync("/desktop/shares/shot-0123abcd?ticket=nope");
        HttpResponseMessage mismatched = await client.GetAsync($"/desktop/shares/shot-deadbeef?ticket={Uri.EscapeDataString(ticket)}");
        HttpResponseMessage matching = await client.GetAsync(uri.PathAndQuery);

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Contains("unknown", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, mismatched.StatusCode);
        Assert.Contains("does not match", await mismatched.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, matching.StatusCode);
        Assert.Equal("nosniff", matching.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", matching.Headers.GetValues("Referrer-Policy").Single());
    }

    private WebApplicationFactory<Program> Create(string[] tokens)
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        string json = JsonSerializer.Serialize(new
        {
            Vestigo = new
            {
                ListenAddress = "127.0.0.1",
                ListenPort = port,
                ApiTokens = tokens,
                Modules = new { Desktop = new { Enabled = true } },
                Desktop = new { AllowScreenshots = true }
            }
        });
        File.WriteAllText(Path.Combine(_settings, "appsettings.json"), json);
        return new WebApplicationFactory<Program>();
    }
}
