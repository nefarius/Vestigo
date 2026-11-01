namespace Vestigo.DebugOutput;

/// <summary>One parsed OutputDebugString line from DebugViewConsole tab-separated stdout.</summary>
public sealed record DebugViewMessage(string Time, int Pid, string ProcessName, string Message);

/// <summary>
///     Classifies DebugViewConsole stdout and stderr. With <c>-s -p -n -q</c> each message line is
///     <c>time\tpid\tprocessName\t\tmessage</c>; the extra empty field is the second tab before the message.
/// </summary>
public static class DebugViewLineParser
{
    public static bool IsBanner(string line) =>
        line.StartsWith("DebugViewConsole ", StringComparison.Ordinal) ||
        line.Equals("Listening for OutputDebugString messages...", StringComparison.Ordinal) ||
        line.Equals("Process ended normally.", StringComparison.Ordinal) ||
        line.Equals("Log file closed.", StringComparison.Ordinal) ||
        line.StartsWith("Neither output to logfile", StringComparison.Ordinal) ||
        line.StartsWith("The debug privilege is not available", StringComparison.Ordinal) ||
        line.StartsWith("Unable to enable the debug privilege", StringComparison.Ordinal);

    public static bool IsDbWinInUse(string text) =>
        text.Contains("CreateDBWinBufferMapping", StringComparison.Ordinal) ||
        text.Contains("Another DebugView++", StringComparison.Ordinal);

    public static bool IsGlobalCaptureWarning(string text) =>
        text.Contains("Unable to capture Global Win32 messages", StringComparison.Ordinal);

    /// <summary>
    ///     Splits a tab-separated message into at most five fields so tabs inside the message survive.
    /// </summary>
    public static bool TryParse(string line, out DebugViewMessage? message)
    {
        message = null;
        if (string.IsNullOrEmpty(line) || IsBanner(line))
        {
            return false;
        }

        string[] parts = line.Split('\t', 5);
        if (parts.Length < 5 || !int.TryParse(parts[1], out int pid))
        {
            return false;
        }

        message = new DebugViewMessage(parts[0], pid, parts[2], parts[4]);
        return true;
    }
}
