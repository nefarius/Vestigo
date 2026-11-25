using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

using Vestigo.Desktop;
using Vestigo.Input;
using Vestigo.Jobs;
using Vestigo.Processes;
using Vestigo.Vision;

namespace Vestigo.Server.Tests;

/// <summary>
///     OCR overlaps UI Automation only when both were requested. The merged elements, statuses, and observed-input
///     classification stay the same as the sequential path.
/// </summary>
public class InspectParallelRecognitionTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-parallel-inspect").FullName;
    private readonly string? _previousSettings = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");

    public InspectParallelRecognitionTests()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _previousSettings);
        try
        {
            Directory.Delete(_settings, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]
    [InlineData("0", false)]
    [InlineData(" 0 ", false)]
    [InlineData("00", true)]
    public void ShouldParallelizeRecognition_TreatsOnlyZeroAsRollback(string? value, bool parallel) =>
        Assert.Equal(parallel, SessionHelper.ShouldParallelizeRecognition(value));

    [Fact]
    public void BothBranches_RunOcrOnAWorkerWhileUiaStaysOnTheCaller()
    {
        int caller = Environment.CurrentManagedThreadId;
        using ManualResetEventSlim ocrEntered = new(false);
        using ManualResetEventSlim uiaEntered = new(false);
        int uiaThread = 0;
        int ocrThread = 0;

        OperationResult<InspectCaptureResult> result = Inspect(
            true,
            Scene.Ready,
            readUia: _ =>
            {
                uiaThread = Environment.CurrentManagedThreadId;
                uiaEntered.Set();
                Assert.True(ocrEntered.Wait(TimeSpan.FromSeconds(2)));
                return (Scene.Ready.Uia, "ok");
            },
            readOcr: (_, _) =>
            {
                ocrThread = Environment.CurrentManagedThreadId;
                ocrEntered.Set();
                Assert.True(uiaEntered.Wait(TimeSpan.FromSeconds(2)));
                return (Scene.Ready.Ocr, "ok");
            });

        Assert.True(result.Success, result.Error);
        Assert.Equal(caller, uiaThread);
        Assert.NotEqual(caller, ocrThread);
        AssertSame(Inspect(false, Scene.Ready).Value!, result.Value!);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void SingleFeature_StaysOnTheCaller(bool uia, bool ocr)
    {
        int caller = Environment.CurrentManagedThreadId;
        int uiaCalls = 0;
        int ocrCalls = 0;

        OperationResult<InspectCaptureResult> result = Inspect(
            true,
            Scene.Ready,
            uia,
            ocr,
            readUia: _ =>
            {
                uiaCalls++;
                Assert.Equal(caller, Environment.CurrentManagedThreadId);
                return (Scene.Ready.Uia, "ok");
            },
            readOcr: (_, _) =>
            {
                ocrCalls++;
                Assert.Equal(caller, Environment.CurrentManagedThreadId);
                return (Scene.Ready.Ocr, "ok");
            });

        Assert.True(result.Success, result.Error);
        Assert.Equal(uia ? 1 : 0, uiaCalls);
        Assert.Equal(ocr ? 1 : 0, ocrCalls);
        Assert.Equal(uia ? "ok" : "skipped", result.Value!.UiaStatus);
        Assert.Equal(ocr ? "ok" : "skipped", result.Value.OcrStatus);
    }

    [Fact]
    public void ParallelDisabled_RunsBothBranchesOnTheCaller()
    {
        int caller = Environment.CurrentManagedThreadId;
        OperationResult<InspectCaptureResult> result = Inspect(
            false,
            Scene.Ready,
            readUia: _ =>
            {
                Assert.Equal(caller, Environment.CurrentManagedThreadId);
                return (Scene.Ready.Uia, "partial");
            },
            readOcr: (_, _) =>
            {
                Assert.Equal(caller, Environment.CurrentManagedThreadId);
                return (Scene.Ready.Ocr, "truncated");
            });

        Assert.Equal("partial", result.Value!.UiaStatus);
        Assert.Equal("truncated", result.Value.OcrStatus);
        Assert.Equal(["Status ready", "Fixture control"], result.Value.Elements.Where(item => item.Source == ElementSource.Uia).Select(item => item.Name).ToArray());
        Assert.Equal(["Status ready"], result.Value.Elements.Where(item => item.Source == ElementSource.Ocr).Select(item => item.Text).ToArray());
    }

    [Fact]
    public void MissingImage_SkipsOcrWithoutAWorker()
    {
        int ocrCalls = 0;
        Scene scene = Scene.Ready with { Capture = Scene.Ready.Capture! with { FullPngBase64 = "" } };
        OperationResult<InspectCaptureResult> result = Inspect(
            true,
            scene,
            readOcr: (_, _) =>
            {
                ocrCalls++;
                return (Scene.Ready.Ocr, "ok");
            });

        Assert.Equal(0, ocrCalls);
        Assert.Equal("skipped", result.Value!.OcrStatus);
        Assert.Equal("ok", result.Value.UiaStatus);
        Assert.All(result.Value.Elements, item => Assert.Equal(ElementSource.Uia, item.Source));
    }

    [Fact]
    public void SlowOcr_IsJoinedAndKeepsItsLines()
    {
        using ManualResetEventSlim uiaDone = new(false);
        OperationResult<InspectCaptureResult> result = Inspect(
            true,
            Scene.Ready,
            readUia: _ =>
            {
                uiaDone.Set();
                return (Scene.Ready.Uia, "ok");
            },
            readOcr: (_, _) =>
            {
                Assert.True(uiaDone.Wait(TimeSpan.FromSeconds(2)));
                return (Scene.TextChanged.Ocr, "ok");
            });

        Assert.Equal(["Status changed"], result.Value!.Elements.Where(item => item.Source == ElementSource.Ocr).Select(item => item.Text).ToArray());
        Assert.Equal("ok", result.Value.UiaStatus);
    }

    [Fact]
    public void SlowUia_LetsOcrFinishAndStillListsUiaFirst()
    {
        using ManualResetEventSlim ocrDone = new(false);
        OperationResult<InspectCaptureResult> result = Inspect(
            true,
            Scene.Ready,
            readUia: _ =>
            {
                Assert.True(ocrDone.Wait(TimeSpan.FromSeconds(2)));
                return (Scene.ControlAdded.Uia, "partial");
            },
            readOcr: (_, _) =>
            {
                ocrDone.Set();
                return (Scene.Ready.Ocr, "truncated");
            });

        Assert.Equal(["Status ready", "Fixture control", "Extra control"], result.Value!.Elements.Take(3).Select(item => item.Name).ToArray());
        Assert.Equal(ElementSource.Ocr, result.Value.Elements[^1].Source);
        Assert.Equal("partial", result.Value.UiaStatus);
        Assert.Equal("truncated", result.Value.OcrStatus);
    }

    [Fact]
    public void ProviderFailures_KeepTheBranchStatus()
    {
        Scene unavailable = new(
            Scene.Ready.Capture,
            [],
            "uia_unavailable: walker failed",
            [],
            "ocr_unavailable: no OCR language pack is installed for the user's languages.");

        OperationResult<InspectCaptureResult> parallel = Inspect(true, unavailable);
        OperationResult<InspectCaptureResult> sequential = Inspect(false, unavailable);

        AssertSame(sequential.Value!, parallel.Value!);
        Assert.Empty(parallel.Value!.Elements);
    }

    [Fact]
    public void UiaException_JoinsTheWorkerBeforeTheNextSessionRequest()
    {
        int liveWorkers = 0;
        int index = 0;
        string transcript = Run(
            [Scene.Ready, Scene.Moved],
            true,
            (scene, request, uia, ocr, _) =>
            {
                int current = index++;
                return SessionHelper.ExecuteInspect(
                    request,
                    uia,
                    ocr,
                    true,
                    _ => OperationResult<ScreenCaptureResult>.Ok(scene.Capture!),
                    _ =>
                    {
                        if (current == 0)
                        {
                            throw new InvalidOperationException("window list unavailable");
                        }

                        Assert.Equal(0, Volatile.Read(ref liveWorkers));
                        return (scene.Uia, scene.UiaStatus);
                    },
                    (_, _) =>
                    {
                        bool tracked = current == 0;
                        if (tracked)
                        {
                            Interlocked.Increment(ref liveWorkers);
                        }

                        try
                        {
                            if (current == 0)
                            {
                                Thread.Sleep(200);
                            }

                            return (scene.Ocr, scene.OcrStatus);
                        }
                        finally
                        {
                            if (tracked)
                            {
                                Interlocked.Decrement(ref liveWorkers);
                            }
                        }
                    });
            });

        string[] lines = Lines(transcript);
        Assert.Equal(2, lines.Length);
        Assert.Equal("capture_failed", JsonDocument.Parse(lines[0]).RootElement.GetProperty("errorCode").GetString());
        Assert.True(JsonDocument.Parse(lines[1]).RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, Volatile.Read(ref liveWorkers));
    }

    [Fact]
    public void SequentialUiaException_DoesNotRunOcr()
    {
        int ocrCalls = 0;
        Assert.Throws<InvalidOperationException>(() => SessionHelper.ExecuteInspect(
            ScreenCaptureRequest.DesktopPreview,
            true,
            true,
            false,
            _ => OperationResult<ScreenCaptureResult>.Ok(Scene.Ready.Capture!),
            _ => throw new InvalidOperationException("window list unavailable"),
            (_, _) =>
            {
                ocrCalls++;
                return (Scene.Ready.Ocr, Scene.Ready.OcrStatus);
            }));
        Assert.Equal(0, ocrCalls);
    }

    [Fact]
    public void ChangingScenes_MatchAcrossMoveResizeCloseAndControls()
    {
        Scene[] scenes =
        [
            Scene.Ready,
            Scene.TextChanged,
            Scene.ControlRemoved,
            Scene.ControlAdded,
            Scene.Moved,
            Scene.Resized,
            Scene.Retitled,
            Scene.Closed
        ];

        foreach (Scene scene in scenes)
        {
            OperationResult<InspectCaptureResult> sequential = Inspect(false, scene);
            OperationResult<InspectCaptureResult> parallel = Inspect(true, scene);
            Assert.Equal(sequential.Success, parallel.Success);
            if (!sequential.Success)
            {
                Assert.Equal("window_not_found", parallel.ErrorCode);
                Assert.Equal(sequential.Error, parallel.Error);
                continue;
            }

            AssertSame(sequential.Value!, parallel.Value!);
            MergeResult sequentialMerge = ElementMerger.Merge(
                sequential.Value!.Elements.Where(item => item.Source == ElementSource.Uia),
                sequential.Value.Elements.Where(item => item.Source == ElementSource.Ocr),
                []);
            MergeResult parallelMerge = ElementMerger.Merge(
                parallel.Value!.Elements.Where(item => item.Source == ElementSource.Uia),
                parallel.Value.Elements.Where(item => item.Source == ElementSource.Ocr),
                []);
            Assert.Equal(Signature(sequentialMerge.Elements), Signature(parallelMerge.Elements));
            Assert.Equal(sequentialMerge.Elements.Select(item => item.Id).ToArray(), parallelMerge.Elements.Select(item => item.Id).ToArray());
        }
    }

    [Fact]
    public void SessionPairs_MatchAfterTheWindowChanges()
    {
        Scene[] scenes = [Scene.Ready, Scene.Moved, Scene.ControlRemoved, Scene.Resized];
        string sequential = Run(scenes, false, Recognize);
        string parallel = Run(scenes, true, Recognize);
        Assert.Equal(Body(sequential), Body(parallel));
    }

    [Theory]
    [InlineData("text", "observed")]
    [InlineData("removed", "observed")]
    [InlineData("added", "observed")]
    [InlineData("moved", "observed")]
    [InlineData("resized", "observed")]
    [InlineData("retitled", "observed")]
    [InlineData("unchanged", "no-response")]
    public async Task ObservedInput_ClassifiesSequentialAndParallelTheSameWay(string change, string expected)
    {
        Scene after = change switch
        {
            "text" => Scene.TextChanged,
            "removed" => Scene.ControlRemoved,
            "added" => Scene.ControlAdded,
            "moved" => Scene.Moved,
            "resized" => Scene.Resized,
            "retitled" => Scene.Retitled,
            _ => Scene.Ready
        };

        (string Sequential, int SequentialSends) sequential = await Observe(false, [Scene.Ready, after]);
        (string Parallel, int ParallelSends) parallel = await Observe(true, [Scene.Ready, after]);

        Assert.Equal(expected, sequential.Sequential);
        Assert.Equal(expected, parallel.Parallel);
        Assert.Equal(1, sequential.SequentialSends);
        Assert.Equal(1, parallel.ParallelSends);
    }

    [Fact]
    public async Task ObservedInput_ClosePreservesTheInjectionAndDoesNotReplay()
    {
        (string Status, int Sends, string? Error) sequential = await ObserveClose(false);
        (string Status, int Sends, string? Error) parallel = await ObserveClose(true);

        Assert.Equal("inconclusive", sequential.Status);
        Assert.Equal("inconclusive", parallel.Status);
        Assert.Equal("window_not_found", sequential.Error);
        Assert.Equal("window_not_found", parallel.Error);
        Assert.Equal(1, sequential.Sends);
        Assert.Equal(1, parallel.Sends);
    }

    [Theory]
    [InlineData("before", 0)]
    [InlineData("delay", 1)]
    [InlineData("after", 1)]
    public async Task FullObservation_CancellationDoesNotSubmitMoreInput(string phase, int sent)
    {
        ObserveHelperReuseTests.Rig rig = ObserveHelperReuseTests.Rig.Factory();
        rig.Frames.AddRange([ObserveFrame(1), ObserveFrame(2)]);
        if (phase == "before")
        {
            rig.OnInspect = (_, _) => rig.Caller.Cancel();
        }
        else if (phase == "delay")
        {
            rig.Clock.CancelOnDelay = true;
        }
        else
        {
            rig.OnInspect = (index, _) =>
            {
                if (index == 1)
                {
                    rig.Caller.Cancel();
                }
            };
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Send("full", delay: phase == "delay" ? 400 : 0, token: rig.Caller.Token));

        Assert.Equal(sent, rig.Launcher.SendCount);
        Assert.True(rig.Session!.Disposed);
        Assert.Equal(phase == "after" ? 2 : 1, rig.Session.Calls);
    }

    [Fact]
    public async Task HelperTimeout_CancelsTheProcessAndDoesNotWaitOutTheShutdownGrace()
    {
        WaitingLauncher launcher = new();
        OperationResult<IInspectCaptureSession> started = await InspectHelperSession.StartAsync(
            launcher,
            elevated: false,
            desktop: null,
            CancellationToken.None,
            shutdownGrace: TimeSpan.FromSeconds(5));
        Assert.True(started.Success, started.Error);

        OperationResult<InspectCaptureResult> timedOut = await started.Value!.InspectAsync(
            ScreenCaptureRequest.DesktopPreview,
            true,
            true,
            TimeSpan.FromMilliseconds(80),
            1024,
            CancellationToken.None);
        Assert.Equal("helper_timeout", timedOut.ErrorCode);
        Assert.True(SpinWait.SpinUntil(() => launcher.Cancelled, TimeSpan.FromSeconds(2)));

        Stopwatch dispose = Stopwatch.StartNew();
        await started.Value.DisposeAsync();
        Assert.True(dispose.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CallerCancellation_StopsTheHelperAndDoesNotStartAnotherCapture()
    {
        int calls = 0;
        using CancellationTokenSource process = new();
        using CancellationTokenSource caller = new();
        (InspectHelperChannel Channel, Task<int> Helper) pair = Connect(
            (_, _, _) =>
            {
                calls++;
                Assert.True(process.Token.WaitHandle.WaitOne(5000));
                process.Token.ThrowIfCancellationRequested();
                return OperationResult<InspectCaptureResult>.Ok(Sample(1));
            },
            () => process.Cancel());
        try
        {
            Task pending = pair.Channel.InspectAsync(ScreenCaptureRequest.DesktopPreview, true, true, TimeSpan.FromSeconds(5), 1024, caller.Token);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            OperationResult<InspectCaptureResult> second = await pair.Channel.InspectAsync(
                ScreenCaptureRequest.DesktopPreview,
                true,
                true,
                TimeSpan.FromSeconds(1),
                1024,
                CancellationToken.None);
            Assert.Equal("helper_failed", second.ErrorCode);
            Assert.InRange(calls, 0, 1);
            Assert.True(process.IsCancellationRequested);
        }
        finally
        {
            await pair.Channel.DisposeAsync();
        }

        Assert.Equal(0, await pair.Helper.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static async Task<(string Status, int Sends)> Observe(bool parallel, IReadOnlyList<Scene> scenes)
    {
        ObserveHelperReuseTests.Rig rig = Hooked(parallel, scenes);
        JsonElement result = await rig.Send("full", delay: 0);
        using JsonDocument document = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        return (document.RootElement.GetProperty("response").GetProperty("status").GetString()!, rig.Launcher.SendCount);
    }

    private static async Task<(string Status, int Sends, string? Error)> ObserveClose(bool parallel)
    {
        ObserveHelperReuseTests.Rig rig = Hooked(parallel, [Scene.Ready, Scene.Closed]);
        JsonElement result = await rig.Send("full", delay: 0);
        using JsonDocument document = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        return (
            document.RootElement.GetProperty("response").GetProperty("status").GetString()!,
            rig.Launcher.SendCount,
            document.RootElement.GetProperty("observeError").GetProperty("errorCode").GetString());
    }

    private static ObserveHelperReuseTests.Rig Hooked(bool parallel, IReadOnlyList<Scene> scenes)
    {
        int index = 0;
        ObserveHelperReuseTests.Rig rig = ObserveHelperReuseTests.Rig.Factory();
        rig.InspectHook = (capture, uia, ocr) =>
        {
            // A bounds change is a capture_mismatch, and send_input inspects that window once more
            // without diffAgainst. The window stays in its latest state for that retry.
            Scene scene = scenes[Math.Min(index, scenes.Count - 1)];
            index++;
            return SessionHelper.ExecuteInspect(
                capture,
                uia,
                ocr,
                parallel,
                _ => scene.Capture is null
                    ? OperationResult<ScreenCaptureResult>.Fail(scene.Error!, "The window no longer exists.")
                    : OperationResult<ScreenCaptureResult>.Ok(scene.Capture),
                _ => (scene.Uia, scene.UiaStatus),
                (_, bounds) =>
                {
                    Assert.Equal(scene.Capture!.Bounds, bounds);
                    return (scene.Ocr, scene.OcrStatus);
                });
        };
        return rig;
    }

    private static OperationResult<InspectCaptureResult> Inspect(
        bool parallel,
        Scene scene,
        Func<ScreenCaptureResult, (IReadOnlyList<ScreenElement> Elements, string Status)>? readUia = null,
        Func<byte[], DesktopRect, (IReadOnlyList<ScreenElement> Lines, string Status)>? readOcr = null) =>
        Inspect(parallel, scene, true, true, readUia, readOcr);

    private static OperationResult<InspectCaptureResult> Inspect(
        bool parallel,
        Scene scene,
        bool uia,
        bool ocr,
        Func<ScreenCaptureResult, (IReadOnlyList<ScreenElement> Elements, string Status)>? readUia = null,
        Func<byte[], DesktopRect, (IReadOnlyList<ScreenElement> Lines, string Status)>? readOcr = null) =>
        SessionHelper.ExecuteInspect(
            ScreenCaptureRequest.DesktopPreview,
            uia,
            ocr,
            parallel,
            _ => scene.Capture is null
                ? OperationResult<ScreenCaptureResult>.Fail(scene.Error!, "The window no longer exists.")
                : OperationResult<ScreenCaptureResult>.Ok(scene.Capture),
            readUia ?? (_ => (scene.Uia, scene.UiaStatus)),
            readOcr ?? ((_, _) => (scene.Ocr, scene.OcrStatus)));

    private static OperationResult<InspectCaptureResult> Recognize(
        Scene scene,
        ScreenCaptureRequest request,
        bool uia,
        bool ocr,
        bool parallel) =>
        SessionHelper.ExecuteInspect(
            request,
            uia,
            ocr,
            parallel,
            _ => OperationResult<ScreenCaptureResult>.Ok(scene.Capture!),
            _ => (scene.Uia, scene.UiaStatus),
            (_, _) => (scene.Ocr, scene.OcrStatus));

    private static string Run(
        IReadOnlyList<Scene> scenes,
        bool parallel,
        Func<Scene, ScreenCaptureRequest, bool, bool, bool, OperationResult<InspectCaptureResult>> recognize)
    {
        int index = 0;
        List<string> requests = [];
        for (int number = 0; number < scenes.Count; number++)
        {
            requests.Add(JsonSerializer.Serialize(
                new InspectSessionRequest(InspectSessionOp.Inspect, "s" + number, ScreenCaptureRequest.DesktopPreview, true, true),
                InspectHelperJsonContext.Default.InspectSessionRequest));
        }

        StringWriter output = new();
        int code = SessionHelper.RunInspectSession(
            new StringReader(string.Join('\n', requests) + "\n"),
            output,
            (request, uia, ocr) => recognize(scenes[index++], request, uia, ocr, parallel));
        Assert.Equal(0, code);
        return output.ToString();
    }

    private static string Body(string transcript)
    {
        List<string> bodies = [];
        foreach (string line in Lines(transcript))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            bodies.Add(document.RootElement.GetProperty("result").GetProperty("elements").GetRawText()
                + document.RootElement.GetProperty("result").GetProperty("ocrStatus").GetRawText()
                + document.RootElement.GetProperty("result").GetProperty("uiaStatus").GetRawText()
                + document.RootElement.GetProperty("result").GetProperty("capture").GetProperty("bounds").GetRawText()
                + document.RootElement.GetProperty("result").GetProperty("capture").GetProperty("windowTitle").GetRawText());
        }

        return string.Join('\n', bodies);
    }

    private static void AssertSame(InspectCaptureResult expected, InspectCaptureResult actual)
    {
        Assert.Equal(expected.OcrStatus, actual.OcrStatus);
        Assert.Equal(expected.UiaStatus, actual.UiaStatus);
        Assert.Equal(expected.Capture.Bounds, actual.Capture.Bounds);
        Assert.Equal(expected.Capture.WindowTitle, actual.Capture.WindowTitle);
        Assert.Equal(Signature(expected.Elements), Signature(actual.Elements));
    }

    private static string Signature(IReadOnlyList<ScreenElement> elements) =>
        string.Join('\n', elements.Select(element =>
            $"{element.Id}|{element.Source}|{element.Role}|{element.Name}|{element.Text}|{element.Box.X},{element.Box.Y},{element.Box.Width},{element.Box.Height}|{element.Enabled}|{element.Focused}"));

    private static string[] Lines(string transcript) =>
        transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static (InspectHelperChannel Channel, Task<int> Helper) Connect(
        Func<ScreenCaptureRequest, bool, bool, OperationResult<InspectCaptureResult>> capture,
        Action onBroken)
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
            catch (IOException)
            {
                // The caller closed the pipes while cancelling. The helper has stopped.
                return 0;
            }
            finally
            {
                try
                {
                    writer.Dispose();
                    reader.Dispose();
                }
                catch (IOException)
                {
                    // The caller closed the pipes while cancelling the helper.
                }
            }
        });
        return (new InspectHelperChannel(commands, results, onBroken), helper);
    }

    private static InspectCaptureResult Sample(byte marker) =>
        new(Frame(new DesktopRect(100, 100, 800, 600), marker, "Fixture"), [], "skipped", "skipped");

    private static ObserveHelperReuseTests.Scripted ObserveFrame(byte marker) =>
        new(marker, new DesktopRect(100, 100, 800, 600), null, null, "label", null, 7);

    private sealed record Scene(
        ScreenCaptureResult? Capture,
        IReadOnlyList<ScreenElement> Uia,
        string UiaStatus,
        IReadOnlyList<ScreenElement> Ocr,
        string OcrStatus,
        string? Error = null)
    {
        public static Scene Ready { get; } = Of("Status ready", "Fixture control", new DesktopRect(100, 100, 800, 600), "Fixture", 1, "Status ready");

        public static Scene TextChanged { get; } = Of("Status changed", "Fixture control", new DesktopRect(100, 100, 800, 600), "Fixture", 1, "Status changed");

        public static Scene ControlRemoved { get; } = Of(null, "Fixture control", new DesktopRect(100, 100, 800, 600), "Fixture", 1, "Fixture control");

        public static Scene ControlAdded { get; } = new(
            Frame(new DesktopRect(100, 100, 800, 600), 1, "Fixture"),
            [
                Control("Status ready", new DesktopRect(110, 120, 80, 16)),
                Control("Fixture control", new DesktopRect(110, 160, 120, 16)),
                Control("Extra control", new DesktopRect(110, 200, 90, 16))
            ],
            "ok",
            [Line("Status ready", new DesktopRect(110, 120, 80, 14))],
            "ok");

        public static Scene Moved { get; } = Of("Status ready", "Fixture control", new DesktopRect(400, 220, 800, 600), "Fixture", 1, "Status ready", 300, 120);

        public static Scene Resized { get; } = Of("Status ready", "Fixture control", new DesktopRect(100, 100, 640, 480), "Fixture", 1, "Status ready");

        public static Scene Retitled { get; } = Of("Status ready", "Fixture control", new DesktopRect(100, 100, 800, 600), "Fixture moved", 1, "Status ready");

        public static Scene Closed { get; } = new(null, [], "skipped", [], "skipped", "window_not_found");

        private static Scene Of(string? status, string control, DesktopRect bounds, string title, byte marker, string ocr, int shiftX = 0, int shiftY = 0)
        {
            List<ScreenElement> uia = [];
            if (status is not null)
            {
                uia.Add(Control(status, new DesktopRect(110 + shiftX, 120 + shiftY, 80, 16)));
            }

            uia.Add(Control(control, new DesktopRect(110 + shiftX, 160 + shiftY, 120, 16)));
            return new(Frame(bounds, marker, title), uia, "ok", [Line(ocr, new DesktopRect(110 + shiftX, 120 + shiftY, 80, 14))], "ok");
        }
    }

    private static ScreenElement Control(string name, DesktopRect box) =>
        new(0, ElementSource.Uia, "text", name, null, box, true, null);

    private static ScreenElement Line(string text, DesktopRect box) =>
        new(0, ElementSource.Ocr, "text", null, text, box);

    private static ScreenCaptureResult Frame(DesktopRect bounds, byte marker, string title) =>
        new(
            ScreenCaptureTarget.Window,
            bounds,
            new DesktopSize(bounds.Width, bounds.Height),
            null,
            96,
            ["\\\\.\\DISPLAY1"],
            "0x20",
            title,
            null,
            Convert.ToBase64String([marker]),
            7,
            WindowBounds: bounds);

    private sealed class WaitingLauncher : IProcessLauncher
    {
        public bool Cancelled { get; private set; }

        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) => OperationResult<bool>.Ok(true);

        public Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
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
        }
    }
}
