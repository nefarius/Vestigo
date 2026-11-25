using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

using Vestigo.Desktop;
using Vestigo.Vision;

namespace Vestigo.Processes;

public static partial class SessionHelper
{
    /// <summary>
    ///     Process switch for inspection helpers. Unset, or any value other than <c>0</c>, overlaps OCR with UI Automation.
    ///     <c>0</c> runs them one after the other. This is not a <c>VestigoOptions</c> setting. The service reads it from its
    ///     own process and forwards the resolved value, because an interactive helper does not inherit the service environment.
    /// </summary>
    internal const string ParallelRecognitionVariable = "VESTIGO_INSPECT_PARALLEL_RECOGNITION";

    /// <summary>Unset and every value except <c>0</c> overlap OCR and UI Automation. <c>0</c> is the sequential rollback.</summary>
    internal static bool ShouldParallelizeRecognition(string? value) =>
        !string.Equals(value?.Trim(), "0", StringComparison.Ordinal);

    /// <summary>The helper environment entry for one launch. The value is always <c>1</c> or <c>0</c>.</summary>
    internal static Dictionary<string, string> ParallelRecognitionEnvironment(string? value) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [ParallelRecognitionVariable] = ShouldParallelizeRecognition(value) ? "1" : "0"
        };

    /// <summary>Reads <see cref="ParallelRecognitionVariable"/> from this process.</summary>
    internal static bool ParallelRecognitionEnabled() =>
        ShouldParallelizeRecognition(Environment.GetEnvironmentVariable(ParallelRecognitionVariable));
    /// <summary>
    ///     Builds helper arguments for <c>inspect-screen</c>: the capture arguments with the full image requested,
    ///     plus switches for the UI Automation walk and OCR.
    /// </summary>
    public static List<string> BuildInspectArguments(ScreenCaptureRequest request, bool uia, bool ocr)
    {
        List<string> arguments = BuildCaptureArguments(request with { MaxEdge = 0, IncludeFullImage = true });
        arguments[1] = InspectScreenCommand;
        arguments.Add("--uia");
        arguments.Add(uia ? "true" : "false");
        arguments.Add("--ocr");
        arguments.Add(ocr ? "true" : "false");
        return arguments;
    }

    /// <summary>Parses <c>inspect-screen</c> helper arguments into a capture request and the UIA/OCR switches.</summary>
    public static bool TryParseInspect(string[] args, out ScreenCaptureRequest request, out bool uia, out bool ocr, out string? error)
    {
        uia = true;
        ocr = true;
        List<string> capture = [];
        capture.AddRange(args.Take(2));
        for (int index = 2; index < args.Length; index += 2)
        {
            // Options come in name/value pairs; a value such as "--ocr" must never be read as a switch.
            if (index + 1 < args.Length && args[index] is "--uia" or "--ocr")
            {
                if (!TryParseFlag(args[index + 1], out bool flag))
                {
                    request = ScreenCaptureRequest.DesktopPreview;
                    error = $"Option '{args[index]}' needs true or false.";
                    return false;
                }

                if (args[index] == "--uia")
                {
                    uia = flag;
                }
                else
                {
                    ocr = flag;
                }

                continue;
            }

            capture.Add(args[index]);
            if (index + 1 < args.Length)
            {
                capture.Add(args[index + 1]);
            }
        }

        if (capture.Count >= 2 && capture[1] == InspectScreenCommand)
        {
            capture[1] = CaptureScreenCommand;
        }

        return TryParseCapture([.. capture], out request, out error);
    }

    private static int RunInspectScreen(string[] args)
    {
        if (!TryParseInspect(args, out ScreenCaptureRequest request, out bool uia, out bool ocr, out string? error))
        {
            return RejectInspect(error!);
        }

        OperationResult<InspectCaptureResult> result = ExecuteInspect(request, uia, ocr, ParallelRecognitionEnabled());
        if (!result.Success)
        {
            return RejectInspect(result.Error!, result.ErrorCode!, result.Reason);
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(result.Value, InspectHelperJsonContext.Default.InspectCaptureResult));
        return 0;
    }

    /// <summary>
    ///     Captures one frame. Desktop relation, window identity, and the window crop are resolved again on every call.
    ///     Recognition follows <see cref="ParallelRecognitionEnabled"/>.
    /// </summary>
    internal static OperationResult<InspectCaptureResult> ExecuteInspect(ScreenCaptureRequest request, bool uia, bool ocr) =>
        ExecuteInspect(request, uia, ocr, ParallelRecognitionEnabled());

    /// <summary>
    ///     Captures one frame, then UI Automation and OCR. When <paramref name="parallel"/> is set and both are requested,
    ///     OCR runs on a worker while UI Automation stays on this thread. The worker is joined before this method returns.
    /// </summary>
    internal static OperationResult<InspectCaptureResult> ExecuteInspect(
        ScreenCaptureRequest request,
        bool uia,
        bool ocr,
        bool parallel) =>
        ExecuteInspect(request, uia, ocr, parallel, ScreenCapture.Capture, CollectUia, OcrReader.Read);

    /// <summary>
    ///     Test seam for <see cref="ExecuteInspect(ScreenCaptureRequest, bool, bool, bool)"/>. UI Automation runs on the
    ///     calling thread. OCR runs on a worker only when both branches are requested and <paramref name="parallel"/> is set.
    /// </summary>
    internal static OperationResult<InspectCaptureResult> ExecuteInspect(
        ScreenCaptureRequest request,
        bool uia,
        bool ocr,
        bool parallel,
        Func<ScreenCaptureRequest, OperationResult<ScreenCaptureResult>> captureFrame,
        Func<ScreenCaptureResult, (IReadOnlyList<ScreenElement> Elements, string Status)> readUia,
        Func<byte[], DesktopRect, (IReadOnlyList<ScreenElement> Lines, string Status)> readOcr)
    {
        try
        {
            OperationResult<ScreenCaptureResult> captured = captureFrame(request with { IncludeFullImage = true });
            if (!captured.Success)
            {
                return captured.FailAs<InspectCaptureResult>();
            }

            return Recognize(captured.Value!, uia, ocr, parallel, readUia, readOcr);
        }
        catch (System.Text.RegularExpressions.RegexParseException ex)
        {
            return OperationResult<InspectCaptureResult>.Fail("invalid_request", "Invalid title pattern: " + ex.Message);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException ex)
        {
            return OperationResult<InspectCaptureResult>.Fail("invalid_request", "Title pattern timed out: " + ex.Message);
        }
    }

    /// <summary>UI Automation for one capture: the window inventory, then the elements that overlap the frame.</summary>
    private static (IReadOnlyList<ScreenElement> Elements, string Status) CollectUia(ScreenCaptureResult capture)
    {
        IReadOnlyList<WindowInfo> windows = WindowEnumerator.List(WindowSelector.All with { Limit = 400 }).Windows;
        if (capture.WindowHandle is { Length: > 0 } handle)
        {
            windows = windows.Where(window => string.Equals(window.Handle, handle, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return UiaWalker.Collect(windows, capture.Bounds);
    }

    /// <summary>
    ///     Runs UI Automation on this thread and, when both sources are requested, OCR on a worker. UI Automation elements
    ///     are listed before OCR elements. A worker started for this capture is finished before the method returns.
    /// </summary>
    private static OperationResult<InspectCaptureResult> Recognize(
        ScreenCaptureResult capture,
        bool uia,
        bool ocr,
        bool parallel,
        Func<ScreenCaptureResult, (IReadOnlyList<ScreenElement> Elements, string Status)> readUia,
        Func<byte[], DesktopRect, (IReadOnlyList<ScreenElement> Lines, string Status)> readOcr)
    {
        string? fullPng = capture.FullPngBase64;
        bool runOcr = ocr && fullPng is { Length: > 0 };
        Task<(IReadOnlyList<ScreenElement> Lines, string Status)>? ocrTask = null;
        if (parallel && uia && runOcr)
        {
            string png = fullPng!;
            DesktopRect bounds = capture.Bounds;
            ocrTask = Task.Run(() => readOcr(Convert.FromBase64String(png), bounds));
        }

        IReadOnlyList<ScreenElement> elements = [];
        string uiaStatus = "skipped";
        Exception? uiaFailure = null;
        nint previousDpi = 0;
        try
        {
            previousDpi = WindowEnumerator.SetDpiAware();
            if (uia)
            {
                try
                {
                    (elements, uiaStatus) = readUia(capture);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    uiaFailure = ex;
                }
            }

            (IReadOnlyList<ScreenElement> lines, string ocrStatus) = FinishOcr(ocrTask, runOcr, fullPng, capture.Bounds, readOcr, uiaFailure);
            if (uiaFailure is not null)
            {
                ExceptionDispatchInfo.Capture(uiaFailure).Throw();
            }

            return OperationResult<InspectCaptureResult>.Ok(new InspectCaptureResult(capture, [.. elements, .. lines], ocrStatus, uiaStatus));
        }
        finally
        {
            WindowEnumerator.RestoreDpi(previousDpi);
            ObserveOcr(ocrTask);
        }
    }

    private static (IReadOnlyList<ScreenElement> Lines, string Status) FinishOcr(
        Task<(IReadOnlyList<ScreenElement> Lines, string Status)>? ocrTask,
        bool runOcr,
        string? fullPng,
        DesktopRect bounds,
        Func<byte[], DesktopRect, (IReadOnlyList<ScreenElement> Lines, string Status)> readOcr,
        Exception? uiaFailure)
    {
        if (ocrTask is not null)
        {
            try
            {
                return ocrTask.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (uiaFailure is not null && ex is not OutOfMemoryException)
            {
                return ([], "skipped");
            }
        }

        if (!runOcr)
        {
            return ([], "skipped");
        }

        return readOcr(Convert.FromBase64String(fullPng!), bounds);
    }

    /// <summary>Waits out an OCR worker that <see cref="FinishOcr"/> did not already observe, so it cannot outlive the capture.</summary>
    private static void ObserveOcr(Task? ocrTask)
    {
        if (ocrTask is null || ocrTask.IsCompleted)
        {
            return;
        }

        try
        {
            ocrTask.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    private static int RunInspectSession(string[] args)
    {
        if (args.Length != 2 || args[0] != Flag || args[1] != InspectSessionCommand)
        {
            return RejectInspect($"Usage: {Flag} {InspectSessionCommand}");
        }

        using StreamWriter results = new(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        using StreamReader commands = new(Console.OpenStandardInput(), Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        return RunInspectSession(commands, results, ExecuteInspect);
    }

    /// <summary>
    ///     Serves inspect requests from <paramref name="commands" /> until <c>close</c>, end of input, or the request cap.
    ///     A failed capture becomes a response line and leaves the helper waiting for the next request.
    /// </summary>
    internal static int RunInspectSession(
        TextReader commands,
        TextWriter results,
        Func<ScreenCaptureRequest, bool, bool, OperationResult<InspectCaptureResult>> capture)
    {
        int served = 0;
        while (true)
        {
            string? line = ReadRequestLine(commands, out bool tooLong);
            if (line is null)
            {
                return 0;
            }

            if (tooLong)
            {
                WriteSession(results, SessionFailure("", "invalid_request", "The inspect request is too large."));
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            InspectSessionRequest? request;
            try
            {
                request = JsonSerializer.Deserialize(line, InspectHelperJsonContext.Default.InspectSessionRequest);
            }
            catch (JsonException)
            {
                request = null;
            }

            if (request is null || string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.Op))
            {
                WriteSession(results, SessionFailure(request?.RequestId ?? "", "invalid_request", "The inspect request could not be read."));
                continue;
            }

            if (request.Op == InspectSessionOp.Close)
            {
                return 0;
            }

            if (request.Op != InspectSessionOp.Inspect)
            {
                WriteSession(results, SessionFailure(request.RequestId, "invalid_request", "Unknown inspect-session operation."));
                continue;
            }

            if (request.Capture is null)
            {
                WriteSession(results, SessionFailure(request.RequestId, "invalid_request", "The inspect request has no capture."));
                continue;
            }

            if (served >= MaxInspectSessionRequests)
            {
                WriteSession(results, SessionFailure(request.RequestId, "invalid_request", "The inspection helper has served its request limit."));
                return 0;
            }

            served++;
            OperationResult<InspectCaptureResult> captured;
            try
            {
                captured = capture(request.Capture, request.Uia, request.Ocr);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                captured = OperationResult<InspectCaptureResult>.Fail("capture_failed", ex.Message);
            }

            WriteSession(results, captured.Success
                ? new InspectSessionResponse(request.RequestId, true, captured.Value)
                : new InspectSessionResponse(request.RequestId, false, Error: captured.Error, ErrorCode: captured.ErrorCode, Reason: captured.Reason));
            if (served >= MaxInspectSessionRequests)
            {
                return 0;
            }
        }
    }

    private static InspectSessionResponse SessionFailure(string requestId, string code, string error) =>
        new(requestId, false, Error: error, ErrorCode: code);

    private static void WriteSession(TextWriter results, InspectSessionResponse response) =>
        results.WriteLine(JsonSerializer.Serialize(response, InspectHelperJsonContext.Default.InspectSessionResponse));

    /// <summary>Reads one request line, or reports that it exceeded <see cref="MaxInspectSessionRequestBytes"/>.</summary>
    private static string? ReadRequestLine(TextReader commands, out bool tooLong)
    {
        tooLong = false;
        StringBuilder builder = new();
        int bytes = 0;
        while (true)
        {
            int next = commands.Read();
            if (next < 0)
            {
                return tooLong || builder.Length == 0 ? null : builder.ToString();
            }

            if (next is '\n')
            {
                return tooLong ? "" : builder.ToString();
            }

            if (next == '\r' && commands.Peek() == '\n')
            {
                commands.Read();
                return tooLong ? "" : builder.ToString();
            }

            if (tooLong)
            {
                continue;
            }

            string text = char.ToString((char)next);
            if (char.IsHighSurrogate((char)next))
            {
                int low = commands.Peek();
                if (low >= 0 && char.IsLowSurrogate((char)low))
                {
                    commands.Read();
                    text += (char)low;
                }
            }

            int charBytes = Encoding.UTF8.GetByteCount(text);
            if (bytes + charBytes > MaxInspectSessionRequestBytes)
            {
                tooLong = true;
                builder.Clear();
                continue;
            }

            bytes += charBytes;
            builder.Append(text);
        }
    }

    /// <summary>
    ///     Writes the <c>inspect-screen</c> error envelope with the source-generated contract and returns exit code 2.
    ///     The JSON shape matches <see cref="Reject" />: <c>error</c>, <c>errorCode</c>, and <c>reason</c> only when set.
    /// </summary>
    private static int RejectInspect(string error, string code = "invalid_request", string? reason = null)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(
            new InspectHelperError(error, code, reason),
            InspectHelperJsonContext.Default.InspectHelperError));
        return 2;
    }
}
