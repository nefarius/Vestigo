using System.Text;
using System.Text.Json;

using Vestigo.Desktop;
using Vestigo.Vision;

namespace Vestigo.Processes;

public static partial class SessionHelper
{
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

        OperationResult<InspectCaptureResult> result = ExecuteInspect(request, uia, ocr);
        if (!result.Success)
        {
            return RejectInspect(result.Error!, result.ErrorCode!, result.Reason);
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(result.Value, InspectHelperJsonContext.Default.InspectCaptureResult));
        return 0;
    }

    /// <summary>
    ///     Captures one frame. Desktop relation, window identity, and the window crop are resolved again on every call.
    /// </summary>
    internal static OperationResult<InspectCaptureResult> ExecuteInspect(ScreenCaptureRequest request, bool uia, bool ocr)
    {
        try
        {
            OperationResult<ScreenCaptureResult> captured = ScreenCapture.Capture(request with { IncludeFullImage = true });
            if (!captured.Success)
            {
                return captured.FailAs<InspectCaptureResult>();
            }

            ScreenCaptureResult capture = captured.Value!;
            nint previousDpi = WindowEnumerator.SetDpiAware();
            try
            {
                IReadOnlyList<ScreenElement> elements = [];
                string uiaStatus = "skipped";
                if (uia)
                {
                    IReadOnlyList<WindowInfo> windows = WindowEnumerator.List(WindowSelector.All with { Limit = 400 }).Windows;
                    if (capture.WindowHandle is { Length: > 0 } handle)
                    {
                        windows = windows.Where(window => string.Equals(window.Handle, handle, StringComparison.OrdinalIgnoreCase)).ToList();
                    }

                    (elements, uiaStatus) = UiaWalker.Collect(windows, capture.Bounds);
                }

                List<ScreenElement> all = [.. elements];
                string ocrStatus = "skipped";
                if (ocr && capture.FullPngBase64 is { Length: > 0 } fullPng)
                {
                    (IReadOnlyList<ScreenElement> lines, ocrStatus) = OcrReader.Read(Convert.FromBase64String(fullPng), capture.Bounds);
                    all.AddRange(lines);
                }

                return OperationResult<InspectCaptureResult>.Ok(new InspectCaptureResult(capture, all, ocrStatus, uiaStatus));
            }
            finally
            {
                WindowEnumerator.RestoreDpi(previousDpi);
            }
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

    private static int RunInspectSession(string[] args)
    {
        if (args.Length != 2 || args[0] != Flag || args[1] != InspectSessionCommand)
        {
            return RejectInspect($"Usage: {Flag} {InspectSessionCommand}");
        }

        using StreamWriter results = new(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
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

            OperationResult<InspectCaptureResult> captured = capture(request.Capture, request.Uia, request.Ocr);
            WriteSession(results, captured.Success
                ? new InspectSessionResponse(request.RequestId, true, captured.Value)
                : new InspectSessionResponse(request.RequestId, false, Error: captured.Error, ErrorCode: captured.ErrorCode, Reason: captured.Reason));
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

            if (next == '\n')
            {
                return tooLong ? "" : builder.ToString().TrimEnd('\r');
            }

            if (tooLong)
            {
                continue;
            }

            char ch = (char)next;
            int charBytes = ch <= 0x7F ? 1 : Encoding.UTF8.GetByteCount([ch]);
            if (bytes + charBytes > MaxInspectSessionRequestBytes)
            {
                tooLong = true;
                builder.Clear();
                continue;
            }

            bytes += charBytes;
            builder.Append(ch);
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
