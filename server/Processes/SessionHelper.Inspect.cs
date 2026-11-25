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

        try
        {
            OperationResult<ScreenCaptureResult> captured = ScreenCapture.Capture(request with { IncludeFullImage = true });
            if (!captured.Success)
            {
                return RejectInspect(captured.Error!, captured.ErrorCode!, captured.Reason);
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

                Console.Out.WriteLine(JsonSerializer.Serialize(
                    new InspectCaptureResult(capture, all, ocrStatus, uiaStatus),
                    InspectHelperJsonContext.Default.InspectCaptureResult));
                return 0;
            }
            finally
            {
                WindowEnumerator.RestoreDpi(previousDpi);
            }
        }
        catch (System.Text.RegularExpressions.RegexParseException ex)
        {
            return RejectInspect("Invalid title pattern: " + ex.Message);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException ex)
        {
            return RejectInspect("Title pattern timed out: " + ex.Message);
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
