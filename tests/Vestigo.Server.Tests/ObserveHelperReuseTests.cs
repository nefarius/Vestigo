using System.IO.Pipes;
using System.Text;
using System.Text.Json;

using Microsoft.Win32.SafeHandles;

using ModelContextProtocol.Protocol;

using Vestigo.Configuration;
using Vestigo.Desktop;
using Vestigo.Input;
using Vestigo.Jobs;
using Vestigo.Processes;
using Vestigo.Vision;

namespace Vestigo.Server.Tests;

/// <summary>The <c>inspect-session</c> helper answers capture requests until close, EOF, or its request cap.</summary>
public class InspectSessionProtocolTests
{
    [Fact]
    public void Loop_ReturnsCaptureFailuresAndKeepsServing()
    {
        int calls = 0;
        string transcript = Run(
            [
                Request("inspect", "one", crop: new DesktopRect(20, 16, 835, 450), uia: false, ocr: false),
                Request("inspect", "two", crop: new DesktopRect(20, 16, 100, 50), uia: true, ocr: true),
                Request(InspectSessionOp.Close, "stop")
            ],
            (capture, uia, ocr) =>
            {
                int index = calls++;
                if (index == 0)
                {
                    Assert.Equal(new DesktopRect(20, 16, 835, 450), capture.WindowCrop);
                    Assert.Equal("0x20", capture.WindowHandle);
                    Assert.False(uia || ocr);
                    return OperationResult<InspectCaptureResult>.Fail("window_not_found", "The window no longer exists.");
                }

                Assert.Equal(new DesktopRect(20, 16, 100, 50), capture.WindowCrop);
                Assert.True(uia && ocr);
                return OperationResult<InspectCaptureResult>.Ok(Sample(2));
            });

        string[] lines = Lines(transcript);
        Assert.Equal(2, calls);
        Assert.Equal("window_not_found", ErrorCode(lines[0]));
        Assert.Equal("two", JsonDocument.Parse(lines[1]).RootElement.GetProperty("requestId").GetString());
        Assert.True(JsonDocument.Parse(lines[1]).RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void Loop_RejectsALongLineInvalidJsonAndStopsAtTheRequestCap()
    {
        List<string> input =
        [
            new string('a', SessionHelper.MaxInspectSessionRequestBytes + 8),
            "{",
            Request("noop", "odd"),
            Request("inspect", "missing")
        ];
        for (int index = 0; index < SessionHelper.MaxInspectSessionRequests + 1; index++)
        {
            input.Add(Request("inspect", "n" + index, crop: new DesktopRect(1, 2, 3, 4)));
        }

        int calls = 0;
        string transcript = Run(input, (_, _, _) =>
        {
            calls++;
            return OperationResult<InspectCaptureResult>.Ok(Sample((byte)calls));
        });

        string[] lines = Lines(transcript);
        Assert.Equal(SessionHelper.MaxInspectSessionRequests, calls);
        Assert.Equal("The inspect request is too large.", Error(lines[0]));
        Assert.Equal("The inspect request could not be read.", Error(lines[1]));
        Assert.Equal("Unknown inspect-session operation.", Error(lines[2]));
        Assert.Equal("The inspect request has no capture.", Error(lines[3]));
        Assert.Equal("n" + (SessionHelper.MaxInspectSessionRequests - 1), JsonDocument.Parse(lines[^1]).RootElement.GetProperty("requestId").GetString());
        Assert.True(JsonDocument.Parse(lines[^1]).RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void Loop_AcceptsAMaxSizedLineEndedByCarriageReturn()
    {
        string body = new string('x', SessionHelper.MaxInspectSessionRequestBytes);
        StringWriter output = new();
        int code = SessionHelper.RunInspectSession(new StringReader(body + "\r\n"), output, (_, _, _) =>
            OperationResult<InspectCaptureResult>.Ok(Sample(1)));

        Assert.Equal(0, code);
        Assert.Equal("The inspect request could not be read.", Error(Lines(output.ToString())[0]));
    }

    [Fact]
    public void Loop_CaptureFailureStaysOnTheHelper()
    {
        int calls = 0;
        string transcript = Run(
            [Request("inspect", "bad", crop: new DesktopRect(1, 2, 3, 4)), Request("inspect", "next", crop: new DesktopRect(1, 2, 3, 4))],
            (_, _, _) =>
            {
                if (calls++ == 0)
                {
                    throw new InvalidOperationException("window list unavailable");
                }

                return OperationResult<InspectCaptureResult>.Ok(Sample(1));
            });

        string[] lines = Lines(transcript);
        Assert.Equal("capture_failed", ErrorCode(lines[0]));
        Assert.Equal("next", JsonDocument.Parse(lines[1]).RootElement.GetProperty("requestId").GetString());
    }

    [Fact]
    public void Loop_ExitsOnEndOfInput()
    {
        int calls = 0;
        string transcript = Run([Request("inspect", "only", crop: new DesktopRect(0, 0, 2, 2))], (_, _, _) =>
        {
            calls++;
            return OperationResult<InspectCaptureResult>.Ok(Sample(1));
        });

        Assert.Equal(1, calls);
        Assert.Single(Lines(transcript));
    }

    [Fact]
    public void ExecuteInspect_ReportsWindowsRequiredOffWindows()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "On Windows this calls the real capture path.");
        OperationResult<InspectCaptureResult> result = SessionHelper.ExecuteInspect(ScreenCaptureRequest.DesktopPreview, false, false);
        Assert.Equal("windows_required", result.ErrorCode);
    }

    [Fact]
    public async Task Channel_RoundTripsACroppedPairAndThenClosesTheHelper()
    {
        int calls = 0;
        (InspectHelperChannel Channel, Task<int> Helper) pair = Connect((capture, _, _) =>
        {
            calls++;
            Assert.Equal(new DesktopRect(20, 16, 835, 450), capture.WindowCrop);
            return OperationResult<InspectCaptureResult>.Ok(Sample((byte)calls));
        });
        try
        {
            OperationResult<InspectCaptureResult> first = await pair.Channel.InspectAsync(CroppedRequest(), false, false, TimeSpan.FromSeconds(2), 1024 * 1024, CancellationToken.None);
            OperationResult<InspectCaptureResult> second = await pair.Channel.InspectAsync(CroppedRequest(), false, false, TimeSpan.FromSeconds(2), 1024 * 1024, CancellationToken.None);
            Assert.True(first.Success, first.Error);
            Assert.True(second.Success, second.Error);
            Assert.Equal("Ag==", second.Value!.Capture.FullPngBase64);
            Assert.Equal(2, calls);
        }
        finally
        {
            await pair.Channel.DisposeAsync();
        }

        Assert.Equal(0, await pair.Helper.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Channel_TimeoutMalformedJsonAndOversizeOutputFailTheRead()
    {
        await using AnonymousPipeServerStream commands = new(PipeDirection.Out);
        await using AnonymousPipeServerStream results = new(PipeDirection.In);
        InspectHelperChannel idle = new(commands, results);
        OperationResult<InspectCaptureResult> timedOut = await idle.InspectAsync(
            CroppedRequest(),
            false,
            false,
            TimeSpan.FromMilliseconds(50),
            1024,
            CancellationToken.None);
        Assert.Equal("helper_timeout", timedOut.ErrorCode);
        OperationResult<InspectCaptureResult> broken = await idle.InspectAsync(CroppedRequest(), false, false, TimeSpan.FromSeconds(1), 1024, CancellationToken.None);
        Assert.Equal("helper_failed", broken.ErrorCode);

        await using AnonymousPipeServerStream rawIn = new(PipeDirection.Out);
        await using AnonymousPipeClientStream rawInClient = new(PipeDirection.In, rawIn.GetClientHandleAsString());
        await using AnonymousPipeServerStream rawOut = new(PipeDirection.In);
        await using AnonymousPipeClientStream rawOutClient = new(PipeDirection.Out, rawOut.GetClientHandleAsString());
        InspectHelperChannel reader = new(rawIn, rawOut);
        await using StreamWriter writer = new(rawOutClient, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync("not-json");
        OperationResult<InspectCaptureResult> invalid = await reader.InspectAsync(CroppedRequest(), false, false, TimeSpan.FromSeconds(2), 1024, CancellationToken.None);
        Assert.Equal("helper_failed", invalid.ErrorCode);
        Assert.Contains("invalid JSON", invalid.Error, StringComparison.Ordinal);

        await writer.WriteLineAsync(new string('x', 64));
        OperationResult<InspectCaptureResult> oversized = await reader.InspectAsync(CroppedRequest(), false, false, TimeSpan.FromSeconds(2), 16, CancellationToken.None);
        Assert.Equal("too_large", oversized.ErrorCode);
        _ = rawInClient;
    }

    [Fact]
    public async Task Channel_CallerCancellationIsNotAHelperTimeout()
    {
        await using AnonymousPipeServerStream commands = new(PipeDirection.Out);
        await using AnonymousPipeServerStream results = new(PipeDirection.In);
        using CancellationTokenSource cancellation = new();
        InspectHelperChannel channel = new(commands, results);
        Task<OperationResult<InspectCaptureResult>> pending = channel.InspectAsync(
            CroppedRequest(),
            false,
            false,
            TimeSpan.FromSeconds(5),
            1024,
            cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task ServiceRunner_RejectsPipedStandardOutput()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, []);
        try
        {
            await using AnonymousPipeServerStream pipe = new(PipeDirection.In);
            JobContext output = new("helper", path, 1024);
            LaunchRequest request = new(
                "dotnet",
                [],
                null,
                new Dictionary<string, string>(),
                LaunchContext.Service,
                false,
                StandardOutput: pipe.ClientSafePipeHandle);
            ProcessLaunchException ex = await Assert.ThrowsAsync<ProcessLaunchException>(() => ServiceProcessRunner.RunAsync(request, output, CancellationToken.None));
            Assert.Equal("invalid_request", ex.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static (InspectHelperChannel Channel, Task<int> Helper) Connect(
        Func<ScreenCaptureRequest, bool, bool, OperationResult<InspectCaptureResult>> capture)
    {
        AnonymousPipeServerStream commands = new(PipeDirection.Out);
        AnonymousPipeClientStream commandClient = new(PipeDirection.In, commands.GetClientHandleAsString());
        AnonymousPipeServerStream results = new(PipeDirection.In);
        AnonymousPipeClientStream resultClient = new(PipeDirection.Out, results.GetClientHandleAsString());
        StreamReader reader = new(commandClient, Encoding.UTF8);
        StreamWriter writer = new(resultClient, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        Task<int> helper = Task.Run(() =>
        {
            try
            {
                return SessionHelper.RunInspectSession(reader, writer, capture);
            }
            finally
            {
                writer.Dispose();
                reader.Dispose();
            }
        });
        return (new InspectHelperChannel(commands, results), helper);
    }

    private static string Run(
        IReadOnlyList<string> requests,
        Func<ScreenCaptureRequest, bool, bool, OperationResult<InspectCaptureResult>> capture)
    {
        StringWriter output = new();
        int code = SessionHelper.RunInspectSession(new StringReader(string.Join('\n', requests) + "\n"), output, capture);
        Assert.Equal(0, code);
        return output.ToString();
    }

    private static string Request(string op, string id, DesktopRect? crop = null, bool uia = false, bool ocr = false)
    {
        ScreenCaptureRequest? capture = op == InspectSessionOp.Inspect && crop is not null ? CroppedRequest(crop) : null;
        if (op == InspectSessionOp.Inspect && crop is null)
        {
            capture = null;
        }

        return JsonSerializer.Serialize(new InspectSessionRequest(op, id, capture, uia, ocr), InspectHelperJsonContext.Default.InspectSessionRequest);
    }

    private static ScreenCaptureRequest CroppedRequest(DesktopRect? crop = null) =>
        ScreenCaptureRequest.DesktopPreview with
        {
            Target = ScreenCaptureTarget.Window,
            WindowHandle = "0x20",
            MaxEdge = 0,
            IncludeFullImage = true,
            MaxBytes = 128L * 1024 * 1024,
            WindowCrop = crop ?? new DesktopRect(20, 16, 835, 450)
        };

    private static InspectCaptureResult Sample(byte marker) =>
        new(
            new ScreenCaptureResult(
                ScreenCaptureTarget.Window,
                new DesktopRect(120, 96, 835, 450),
                new DesktopSize(835, 450),
                null,
                96,
                ["a"],
                "0x20",
                "Fixture",
                null,
                Convert.ToBase64String([marker]),
                1,
                WindowBounds: new DesktopRect(100, 80, 1920, 1160),
                EffectiveCrop: new DesktopRect(20, 16, 835, 450)),
            [],
            "skipped",
            "skipped");

    private static string[] Lines(string transcript) =>
        transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Error(string line) => JsonDocument.Parse(line).RootElement.GetProperty("error").GetString()!;

    private static string ErrorCode(string line) => JsonDocument.Parse(line).RootElement.GetProperty("errorCode").GetString()!;
}

/// <summary>
///     <c>send_input</c> observation keeps one inspection helper for the call when the test switch is on.
///     Image analysis is scripted. The helper process is either scripted or an in-memory <c>inspect-session</c> loop.
/// </summary>
public class ObserveHelperReuseTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-observe-reuse").FullName;
    private readonly string? _previousSettings = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");
    private readonly string? _previousReuse = Environment.GetEnvironmentVariable(InputTools.ReuseHelperVariable);

    public ObserveHelperReuseTests()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);
        Environment.SetEnvironmentVariable(InputTools.ReuseHelperVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _previousSettings);
        Environment.SetEnvironmentVariable(InputTools.ReuseHelperVariable, _previousReuse);
        try
        {
            Directory.Delete(_settings, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Pixels_UsesOneHelperForBothCapturesAndClosesIt()
    {
        Rig rig = Rig.Factory();
        rig.Frames.AddRange([Frame(1), Frame(2)]);

        JsonElement result = await rig.Send("pixels", delay: 0, crop: (20, 16, 835, 450), imageMode: "annotated");

        Assert.Equal("observed", Response(result).GetProperty("status").GetString());
        Assert.Equal("window-crop-pixels", Response(result).GetProperty("compared").GetString());
        Assert.Equal(2, rig.Session!.Calls);
        Assert.True(rig.Session.Disposed);
        Assert.Equal([false, true], rig.Annotated);
        Assert.All(rig.Session.Requests, request =>
        {
            Assert.Equal(new DesktopRect(20, 16, 835, 450), request.Capture.WindowCrop);
            Assert.False(request.Uia || request.Ocr);
        });
        Assert.Equal(1, rig.Launcher.SendCount);
        Assert.Equal(0, rig.Launcher.InspectScreenCount);
        Assert.Equal(0, rig.Launcher.InspectSessionCount);
    }

    [Fact]
    public async Task Full_ReusesTheHelperAndStillSeesAnElementChange()
    {
        Rig rig = Rig.Factory();
        rig.Frames.AddRange([Frame(1, element: "ready"), Frame(1, element: "changed")]);

        JsonElement result = await rig.Send("full", delay: 0);

        Assert.Equal("observed", Response(result).GetProperty("status").GetString());
        Assert.Equal("window", Response(result).GetProperty("compared").GetString());
        Assert.Equal(2, rig.Session!.Calls);
        Assert.All(rig.Session.Requests, request => Assert.True(request.Uia && request.Ocr));
        Assert.True(rig.Session.Disposed);
        Assert.Equal(1, rig.Launcher.SendCount);
    }

    [Fact]
    public async Task RejectedInput_SkipsTheAfterCaptureAndClosesTheHelper()
    {
        Rig rig = Rig.Factory();
        rig.SendResult = Rejected();
        rig.Frames.Add(Frame(1));

        JsonElement result = await rig.Send("pixels", delay: 2000, crop: (20, 16, 100, 50));

        Assert.Equal("skipped", Response(result).GetProperty("status").GetString());
        Assert.Equal("input_rejected", Response(result).GetProperty("reason").GetString());
        Assert.Equal(1, rig.Session!.Calls);
        Assert.True(rig.Session.Disposed);
        Assert.Empty(rig.Clock.Delays);
        Assert.Equal(1, rig.Launcher.SendCount);
    }

    [Fact]
    public async Task CancellationDuringBefore_ClosesTheHelperBeforeInput()
    {
        Rig rig = Rig.Factory();
        rig.Frames.Add(Frame(1));
        rig.OnInspect = (_, _) => rig.Caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Send("pixels", delay: 0, token: rig.Caller.Token));

        Assert.Equal(1, rig.Session.Calls);
        Assert.True(rig.Session.Disposed);
        Assert.Equal(0, rig.Launcher.SendCount);
    }

    [Fact]
    public async Task CancellationDuringInput_ClosesTheHelper()
    {
        Rig rig = Rig.Factory();
        rig.Frames.AddRange([Frame(1), Frame(2)]);
        rig.Launcher.CancelOnSend = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Send("pixels", delay: 0, token: rig.Caller.Token));

        Assert.Equal(1, rig.Session!.Calls);
        Assert.True(rig.Session.Disposed);
        Assert.Equal(1, rig.Launcher.SendCount);
    }

    [Fact]
    public async Task CancellationDuringDelay_ClosesTheHelper()
    {
        Rig rig = Rig.Factory();
        rig.Frames.AddRange([Frame(1), Frame(2)]);
        rig.Clock.CancelOnDelay = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Send("pixels", delay: 400, token: rig.Caller.Token));

        Assert.Equal(1, rig.Session!.Calls);
        Assert.True(rig.Session.Disposed);
        Assert.Equal(1, rig.Launcher.SendCount);
    }

    [Fact]
    public async Task CancellationDuringAfter_ClosesTheHelper()
    {
        Rig rig = Rig.Factory();
        rig.Frames.AddRange([Frame(1), Frame(2)]);
        rig.OnInspect = (index, _) =>
        {
            if (index == 1)
            {
                rig.Caller.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Send("pixels", delay: 0, token: rig.Caller.Token));

        Assert.Equal(2, rig.Session.Calls);
        Assert.True(rig.Session.Disposed);
        Assert.Equal(1, rig.Launcher.SendCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Timeout_ClosesTheHelperAndKeepsAcceptedInput(int failingCapture)
    {
        Rig rig = Rig.Factory();
        rig.Frames.AddRange([
            Frame(1, error: failingCapture == 0 ? "helper_timeout" : null),
            Frame(2, error: failingCapture == 1 ? "helper_timeout" : null)]);

        JsonElement result = await rig.Send("pixels", delay: 0, crop: (20, 16, 100, 50));

        Assert.Equal("inconclusive", Response(result).GetProperty("status").GetString());
        if (failingCapture == 1)
        {
            Assert.Equal("helper_timeout", Root(result).GetProperty("observeError").GetProperty("errorCode").GetString());
        }

        Assert.Equal(InputInjectionStatus.Submitted, Injection(result).GetProperty("status").GetString());
        Assert.Equal(2, Injection(result).GetProperty("submittedEvents").GetInt32());
        Assert.True(rig.Session!.Disposed);
        Assert.Equal(1, rig.Launcher.SendCount);
    }

    [Fact]
    public async Task SessionChange_DoesNotReportNoResponse()
    {
        Rig rig = Rig.Factory();
        rig.Frames.AddRange([Frame(1), Frame(2, session: 9)]);

        JsonElement result = await rig.Send("pixels", delay: 0, crop: (20, 16, 100, 50));

        Assert.Equal("inconclusive", Response(result).GetProperty("status").GetString());
        Assert.Equal("session_changed", Root(result).GetProperty("observeError").GetProperty("errorCode").GetString());
        Assert.Equal(InputInjectionStatus.Submitted, Injection(result).GetProperty("status").GetString());
        Assert.Equal(1, rig.Launcher.SendCount);
        Assert.True(rig.Session!.Disposed);
    }

    [Fact]
    public async Task WindowMove_RevalidatesTheSameCropAndReportsNoResponse()
    {
        DesktopRect crop = new(20, 16, 100, 50);
        Rig rig = Rig.Factory();
        rig.Frames.AddRange([
            Frame(1, bounds: new DesktopRect(120, 116, 100, 50), crop: crop, window: new DesktopRect(100, 100, 800, 600)),
            Frame(1, bounds: new DesktopRect(320, 216, 100, 50), crop: crop, window: new DesktopRect(300, 200, 800, 600))]);

        JsonElement result = await rig.Send("pixels", delay: 0, crop: (20, 16, 100, 50));

        Assert.Equal("no-response", Response(result).GetProperty("status").GetString());
        Assert.Equal(2, rig.Session!.Calls);
        Assert.All(rig.Session.Requests, request => Assert.Equal(crop, request.Capture.WindowCrop));
        Assert.True(rig.Session.Disposed);
    }

    [Fact]
    public async Task WindowCloseAndHelperFailure_PreserveTheInjectionAndDoNotReplay()
    {
        Rig closed = Rig.Factory();
        closed.Frames.AddRange([Frame(1), Frame(2, error: "window_not_found")]);
        JsonElement moved = await closed.Send("pixels", delay: 0, crop: (20, 16, 100, 50));
        Assert.Equal("inconclusive", Response(moved).GetProperty("status").GetString());
        Assert.Equal("window_not_found", Root(moved).GetProperty("observeError").GetProperty("errorCode").GetString());
        Assert.Equal(InputInjectionStatus.Submitted, Injection(moved).GetProperty("status").GetString());
        Assert.True(closed.Session!.Disposed);
        Assert.Equal(1, closed.Launcher.SendCount);

        Rig failed = Rig.Factory();
        failed.Frames.AddRange([Frame(1), Frame(2, error: "helper_failed")]);
        JsonElement broken = await failed.Send("full", delay: 0);
        Assert.Equal("inconclusive", Response(broken).GetProperty("status").GetString());
        Assert.Equal(InputInjectionStatus.Submitted, Injection(broken).GetProperty("status").GetString());
        Assert.Equal(2, Injection(broken).GetProperty("submittedEvents").GetInt32());
        Assert.True(Injection(broken).TryGetProperty("timing", out _));
        Assert.Equal(1, broken.GetProperty("content").GetArrayLength());
        Assert.True(failed.Session!.Disposed);
        Assert.Equal(1, failed.Launcher.SendCount);
        Assert.Equal(2, failed.Session.Calls);
    }

    [Fact]
    public async Task StartFailure_StillSubmitsAndDoesNotLaunchAOneShotInspection()
    {
        Rig rig = Rig.Factory();
        rig.SessionStart = OperationResult<IInspectCaptureSession>.Fail("start_failed", "The inspection helper did not start.");
        rig.Frames.Add(Frame(1));

        JsonElement result = await rig.Send("pixels", delay: 0);

        Assert.Equal("inconclusive", Response(result).GetProperty("status").GetString());
        Assert.Equal("start_failed", Root(result).GetProperty("observeError").GetProperty("errorCode").GetString());
        Assert.Equal(InputInjectionStatus.Submitted, Injection(result).GetProperty("status").GetString());
        Assert.Equal(1, rig.Launcher.SendCount);
        Assert.Equal(0, rig.Launcher.InspectScreenCount);
        Assert.Null(rig.Session);
    }

    [Fact]
    public async Task WithoutTheSwitch_EachCaptureStillLaunchesItsOwnHelper()
    {
        Rig rig = Rig.OneShot();
        rig.Frames.AddRange([Frame(1), Frame(2)]);

        JsonElement result = await rig.Send("pixels", delay: 0, crop: (20, 16, 100, 50));

        Assert.Equal("observed", Response(result).GetProperty("status").GetString());
        Assert.Equal(2, rig.Launcher.InspectScreenCount);
        Assert.Equal(1, rig.Launcher.SendCount);
        Assert.Equal(0, rig.Launcher.InspectSessionCount);
    }

    [Fact]
    public async Task EnvironmentSwitch_LaunchesOneInspectionProcessAndOneInputProcess()
    {
        Environment.SetEnvironmentVariable(InputTools.ReuseHelperVariable, "1");
        Rig rig = Rig.Environment();
        DesktopRect crop = new(20, 16, 835, 450);
        DesktopRect bounds = new(120, 96, 835, 450);
        DesktopRect window = new(100, 80, 1920, 1160);
        rig.Frames.AddRange([
            Frame(1, bounds, crop, window),
            Frame(2, bounds, crop, window)]);

        JsonElement result = await rig.Send("pixels", delay: 0, crop: (20, 16, 835, 450));

        Assert.Equal("observed", Response(result).GetProperty("status").GetString());
        Assert.Equal(1, rig.Launcher.InspectSessionCount);
        Assert.Equal(1, rig.Launcher.SendCount);
        Assert.Equal(0, rig.Launcher.InspectScreenCount);
        Assert.Equal(2, rig.Launcher.BridgedCaptures);
        Assert.All(rig.Launcher.BridgedCrops, crop => Assert.Equal(new DesktopRect(20, 16, 835, 450), crop));
        LaunchRequest session = rig.Launcher.SessionLaunch!;
        Assert.NotNull(session.StandardInput);
        Assert.NotNull(session.StandardOutput);
        Assert.False(session.Elevated);
    }

    [Fact]
    public async Task StartAsync_CancelsTheHelperWhenItDoesNotExit()
    {
        WaitingLauncher launcher = new();
        OperationResult<IInspectCaptureSession> started = await InspectHelperSession.StartAsync(
            launcher,
            elevated: false,
            desktop: @"winsta0\default",
            CancellationToken.None,
            shutdownGrace: TimeSpan.FromMilliseconds(50));

        Assert.True(started.Success, started.Error);
        Assert.Contains(SessionHelper.InspectSessionCommand, launcher.Command, StringComparison.Ordinal);
        Assert.Equal(@"winsta0\default", launcher.Request!.Desktop);
        await started.Value!.DisposeAsync();
        Assert.True(launcher.Cancelled);
        Assert.True(launcher.Completed.IsCompleted);
    }

    [Fact]
    public async Task StartAsync_ReturnsFailureWhenTheHelperFaultsBeforeRunning()
    {
        string spool = Path.Combine(VestigoPaths.SpoolDirectory, "helper");
        int before = Directory.Exists(spool) ? Directory.GetFiles(spool).Length : 0;

        OperationResult<IInspectCaptureSession> started = await InspectHelperSession.StartAsync(
            new FaultedLauncher(),
            elevated: false,
            desktop: null,
            CancellationToken.None);

        Assert.False(started.Success);
        Assert.Equal("start_failed", started.ErrorCode);
        int after = Directory.Exists(spool) ? Directory.GetFiles(spool).Length : 0;
        Assert.Equal(before, after);
    }

    private static JsonElement Root(JsonElement result)
    {
        using JsonDocument document = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        return document.RootElement.Clone();
    }

    private static JsonElement Response(JsonElement result) => Root(result).GetProperty("response");

    private static JsonElement Injection(JsonElement result) => Root(result).GetProperty("injection");

    private static Scripted Frame(
        byte marker,
        DesktopRect? bounds = null,
        DesktopRect? crop = null,
        DesktopRect? window = null,
        string element = "label",
        string? error = null,
        int session = 7) =>
        new(marker, bounds ?? new DesktopRect(100, 100, 800, 600), crop, crop is null ? null : window ?? new DesktopRect(100, 100, 800, 600), element, error, session);

    private static InputResult Rejected() => new(
        0,
        7,
        null,
        0,
        "0x20",
        "0xB2",
        false,
        Status: InputInjectionStatus.Rejected,
        StopCode: InputStopCodes.FocusNotGranted,
        StopReason: "The requested window was not the foreground window immediately before input submission.",
        StoppedAtActionIndex: 0,
        StoppedAtActionKind: InputKinds.Key);

    private static InputResult Submitted() => new(
        1,
        7,
        null,
        2,
        null,
        "0x20",
        true,
        3,
        4,
        Timing: new InputTiming(
            InputTiming.QueryPerformanceCounter,
            "10000000",
            false,
            new InputTimestamp(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), "9007199254740997"),
            new InputTimestamp(new DateTimeOffset(2026, 3, 1, 0, 0, 1, TimeSpan.Zero), "9007199254740999")));

    private sealed record Scripted(byte Marker, DesktopRect Bounds, DesktopRect? Crop, DesktopRect? Window, string Element, string? Error, int Session = 7);

    private sealed class Rig
    {
        private Rig(bool environment)
        {
            VestigoOptions options = new() { Input = { AllowInput = true }, Desktop = { AllowScreenshots = true } };
            Launcher = new ScriptedLauncher(this, environment);
            SessionHelperClient helper = new(Launcher);
            CaptureCache cache = new();
            PerceptionService perception = new(Launcher, helper, cache, options)
            {
                Analyzer = (png, bounds, uia, ocr, _, _, annotate, _) =>
                {
                    Annotated.Add(annotate);
                    return new AnalysisResult(
                        [.. uia.Select((item, index) => item with { Id = index + 1 }), .. ocr],
                        new DesktopSize(bounds.Width, bounds.Height),
                        png,
                        annotate ? [9] : null,
                        new SourceCounts(uia.Count, ocr.Count, 0),
                        new SourceCounts(0, 0, 0),
                        0);
                },
                Differ = (before, after) => before.SequenceEqual(after)
                    ? new DiffResult([], 0)
                    : new DiffResult([new ChangedRegion(new DesktopRect(0, 0, 10, 10))], 4.5)
            };
            Tools = new InputTools(helper, Launcher, perception, options, clock: Clock);
            Clock.Caller = Caller;
        }

        public List<bool> Annotated { get; } = [];

        public List<Scripted> Frames { get; } = [];

        public RecordingClock Clock { get; } = new();

        public ScriptedLauncher Launcher { get; }

        public InputTools Tools { get; }

        public ScriptedInspection? Session { get; private set; }

        public OperationResult<IInspectCaptureSession>? SessionStart { get; set; }

        public InputResult SendResult { get; set; } = Submitted();

        public CancellationTokenSource Caller { get; } = new();

        public Action<int, CancellationToken>? OnInspect { get; set; }

        public int Served { get; set; }

        public static Rig Factory()
        {
            Rig rig = new(environment: false);
            rig.Tools.InspectSessionFactory = _ => Task.FromResult(rig.OpenSession());
            return rig;
        }

        public static Rig OneShot() => new(environment: false);

        public static Rig Environment() => new(environment: true);

        public async Task<JsonElement> Send(
            string mode,
            int delay = 300,
            string imageMode = "none",
            (int X, int Y, int Width, int Height)? crop = null,
            CancellationToken? token = null)
        {
            CallToolResult result = await Tools.SendInput(
                [new InputActionSpec { Type = "key", Keys = "f8" }],
                focusWindow: "0x20",
                requireFocus: true,
                observe: true,
                observeDelayMs: delay,
                observeWindowHandle: "0x20",
                imageMode: imageMode,
                observeMode: mode,
                observeCropX: crop?.X,
                observeCropY: crop?.Y,
                observeCropWidth: crop?.Width,
                observeCropHeight: crop?.Height,
                cancellationToken: token ?? TestContext.Current.CancellationToken);
            Assert.True(result.IsError is not true, JsonSerializer.Serialize(result.Content.Select(block => (block as TextContentBlock)?.Text)));
            return JsonSerializer.SerializeToElement(new
            {
                content = result.Content.Select(block => new { type = block is TextContentBlock ? "text" : "image", text = (block as TextContentBlock)?.Text })
            });
        }

        public OperationResult<IInspectCaptureSession> OpenSession()
        {
            if (SessionStart is not null)
            {
                return SessionStart;
            }

            Session = new ScriptedInspection(this);
            return OperationResult<IInspectCaptureSession>.Ok(Session);
        }

        public InspectCaptureResult ResultFor(int index, bool uia)
        {
            Scripted frame = Frames[index];
            return new InspectCaptureResult(
                new ScreenCaptureResult(
                    ScreenCaptureTarget.Window,
                    frame.Bounds,
                    new DesktopSize(frame.Bounds.Width, frame.Bounds.Height),
                    null,
                    96,
                    ["a"],
                    "0x20",
                    "Fixture",
                    null,
                    Convert.ToBase64String([frame.Marker]),
                    frame.Session,
                    WindowBounds: frame.Window,
                    EffectiveCrop: frame.Crop),
                uia ? [new ScreenElement(0, ElementSource.Uia, "text", frame.Element, null, new DesktopRect(frame.Bounds.X, frame.Bounds.Y, 10, 10))] : [],
                uia ? "ok" : "skipped",
                uia ? "ok" : "skipped");
        }
    }

    private sealed class ScriptedInspection(Rig rig) : IInspectCaptureSession
    {
        public int Calls { get; private set; }

        public bool Disposed { get; private set; }

        public List<(ScreenCaptureRequest Capture, bool Uia, bool Ocr)> Requests { get; } = [];

        public Task<OperationResult<InspectCaptureResult>> InspectAsync(
            ScreenCaptureRequest capture,
            bool uia,
            bool ocr,
            TimeSpan timeout,
            long maxOutputBytes,
            CancellationToken cancellationToken)
        {
            int index = Calls++;
            Requests.Add((capture, uia, ocr));
            rig.OnInspect?.Invoke(index, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Scripted frame = rig.Frames[index];
            return Task.FromResult(frame.Error is null
                ? OperationResult<InspectCaptureResult>.Ok(rig.ResultFor(index, uia))
                : OperationResult<InspectCaptureResult>.Fail(frame.Error, "scripted"));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedLauncher(Rig rig, bool bridge) : IProcessLauncher
    {
        public List<string> Commands { get; } = [];

        public List<DesktopRect?> BridgedCrops { get; } = [];

        public LaunchRequest? SessionLaunch { get; private set; }

        public bool CancelOnSend { get; set; }

        public int SendCount => Commands.Count(command => command.Contains("send-input", StringComparison.Ordinal));

        public int InspectScreenCount => Commands.Count(command => command.Contains("inspect-screen", StringComparison.Ordinal));

        public int InspectSessionCount => Commands.Count(command => command.Contains("inspect-session", StringComparison.Ordinal));

        public int BridgedCaptures => BridgedCrops.Count;

        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) => OperationResult<bool>.Ok(true);

        public Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken)
        {
            string command = string.Join(' ', request.Arguments);
            Commands.Add(command);
            if (command.Contains("inspect-session", StringComparison.Ordinal))
            {
                SessionLaunch = request;
                return bridge
                    ? Bridge(request)
                    : Task.FromResult(0);
            }

            if (command.Contains("send-input", StringComparison.Ordinal))
            {
                if (CancelOnSend)
                {
                    rig.Caller.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                File.WriteAllText(output.OutputPath, JsonSerializer.Serialize(rig.SendResult, SessionHelper.Json));
                return Task.FromResult(0);
            }

            int index = rig.Served++;
            Scripted frame = rig.Frames[index];
            if (frame.Error is not null)
            {
                File.WriteAllText(output.OutputPath, JsonSerializer.Serialize(new { error = "scripted", errorCode = frame.Error }));
                return Task.FromResult(2);
            }

            bool uia = command.Contains("--uia true", StringComparison.Ordinal);
            File.WriteAllText(output.OutputPath, JsonSerializer.Serialize(rig.ResultFor(index, uia), SessionHelper.Json));
            return Task.FromResult(0);
        }

        private Task<int> Bridge(LaunchRequest request)
        {
            FileStream stdin = new(new SafeFileHandle(request.StandardInput!.DangerousGetHandle(), ownsHandle: false), FileAccess.Read, 4096, isAsync: false);
            FileStream stdout = new(new SafeFileHandle(request.StandardOutput!.DangerousGetHandle(), ownsHandle: false), FileAccess.Write, 4096, isAsync: false);
            return Task.Run(() =>
            {
                using StreamReader reader = new(stdin, Encoding.UTF8);
                using StreamWriter writer = new(stdout, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
                try
                {
                    return SessionHelper.RunInspectSession(reader, writer, (capture, uia, ocr) =>
                    {
                        BridgedCrops.Add(capture.WindowCrop);
                        int index = BridgedCrops.Count - 1;
                        Scripted frame = rig.Frames[index];
                        return frame.Error is null
                            ? OperationResult<InspectCaptureResult>.Ok(rig.ResultFor(index, uia))
                            : OperationResult<InspectCaptureResult>.Fail(frame.Error, "scripted");
                    });
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    return 0;
                }
            });
        }
    }

    private sealed class FaultedLauncher : IProcessLauncher
    {
        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) => OperationResult<bool>.Ok(true);

        public Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken) =>
            Task.FromException<int>(new InvalidOperationException("The inspection helper did not start."));
    }

    private sealed class WaitingLauncher : IProcessLauncher
    {
        public string Command { get; private set; } = "";

        public LaunchRequest? Request { get; private set; }

        public bool Cancelled { get; private set; }

        public Task<int> Completed { get; private set; } = Task.FromResult(0);

        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) => OperationResult<bool>.Ok(true);

        public Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken)
        {
            Request = request;
            Command = string.Join(' ', request.Arguments);
            Completed = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    return 0;
                }
                catch (OperationCanceledException)
                {
                    Cancelled = true;
                    return -1;
                }
            }, CancellationToken.None);
            return Completed;
        }
    }

    private sealed class RecordingClock : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];

        public bool CancelOnDelay { get; set; }

        public CancellationTokenSource? Caller { get; set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (CancelOnDelay)
            {
                Caller!.Cancel();
                return new NoopTimer();
            }

            Delays.Add(dueTime);
            callback(state);
            return new NoopTimer();
        }

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
