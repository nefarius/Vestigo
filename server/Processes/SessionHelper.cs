using System.Text.Json;

namespace Vestigo.Processes;

/// <summary>
///     Handles <c>Vestigo.exe --session-helper ...</c>. Program.cs calls this before the web host starts. The helper
///     runs inside the console user's session so it can see that desktop's windows, prints one JSON document to
///     stdout, and exits.
/// </summary>
public static partial class SessionHelper
{
    public const string Flag = "--session-helper";

    public const string CloseWindowCommand = "close-window";

    public const string ListWindowsCommand = "list-windows";

    public const string ListDisplaysCommand = "list-displays";

    public const string ListAudioCommand = "list-audio";

    public const string SetDefaultAudioCommand = "set-default-audio";

    public const string CaptureScreenCommand = "capture-screen";

    public const string InspectScreenCommand = "inspect-screen";

    public const string SendInputCommand = "send-input";

    public const string InspectInputStateCommand = "inspect-input-state";

    public const string ProbeCaptureCommand = "probe-capture";

    public const string ProbeAudioFormatCommand = "probe-audio-format";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool IsInvocation(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Flag, StringComparison.Ordinal);

    /// <summary>Runs the helper command and returns the process exit code.</summary>
    public static int Run(string[] args)
    {
        if (args.Length < 2 || args[0] != Flag)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { error = $"Usage: {Flag} <{CloseWindowCommand}|{ListWindowsCommand}|{ListDisplaysCommand}|{ListAudioCommand}|{SetDefaultAudioCommand}|{CaptureScreenCommand}|{InspectScreenCommand}|{SendInputCommand}|{ProbeCaptureCommand}|{ProbeAudioFormatCommand}|{InspectWindowCommand}|{WindowEventsCommand}|{SnapshotDisplayCommand}|{ApplyDisplayCommand}|{PlaceWindowCommand}|{RestoreWindowCommand}|{DesktopHostCommand}|{DesktopRecoverCommand}|{DesktopEnumerateCommand}|{InspectInputStateCommand}> ..." }, Json));
            return 2;
        }

        return args[1] switch
        {
            CloseWindowCommand => RunClose(args),
            ListWindowsCommand => RunListWindows(args),
            ListDisplaysCommand => RunListDisplays(args),
            ListAudioCommand => RunListAudio(args),
            SetDefaultAudioCommand => RunSetDefaultAudio(args),
            CaptureScreenCommand => RunCaptureScreen(args),
            InspectScreenCommand => RunInspectScreen(args),
            SendInputCommand => RunSendInput(args),
            ProbeCaptureCommand => RunProbeCapture(args),
            ProbeAudioFormatCommand => RunProbeAudioFormat(args),
            InspectWindowCommand => RunInspectWindow(args),
            WindowEventsCommand => RunWindowEvents(args),
            SnapshotDisplayCommand => RunSnapshotDisplay(args),
            ApplyDisplayCommand => RunApplyDisplay(args),
            PlaceWindowCommand => RunPlaceWindow(args),
            RestoreWindowCommand => RunRestoreWindow(args),
            DesktopHostCommand => RunDesktopHost(args),
            DesktopRecoverCommand => RunDesktopRecover(args),
            DesktopEnumerateCommand => RunDesktopEnumerate(args),
            InspectInputStateCommand => RunInspectInputState(args),
            _ => Reject($"Unknown session-helper command '{args[1]}'.")
        };
    }

    private static int Reject(string error, string code = "invalid_request", string? reason = null)
    {
        object payload = reason is null ? new { error, errorCode = code } : new { error, errorCode = code, reason };
        Console.Out.WriteLine(JsonSerializer.Serialize(payload, Json));
        return 2;
    }

    private static bool TryParseFlag(string value, out bool flag)
    {
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1")
        {
            flag = true;
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || value == "0")
        {
            flag = false;
            return true;
        }

        flag = false;
        return false;
    }
}
