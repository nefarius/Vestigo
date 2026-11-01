namespace Vestigo.Desktop;

/// <summary>Settings for the Desktop module, bound from <c>Vestigo:Desktop</c>.</summary>
public sealed class DesktopOptions
{
    public const string SectionName = "Desktop";

    public const long DefaultMaxScreenshotBytes = 128L * 1024 * 1024;

    public const long MinMaxScreenshotBytes = 1024 * 1024;

    public const long MaxMaxScreenshotBytes = 512L * 1024 * 1024;

    /// <summary>Enables <c>take_screenshot</c>. Window and display listing works regardless.</summary>
    public bool AllowScreenshots { get; set; }

    /// <summary>
    ///     Upper bound for the session-helper JSON document that carries the PNG (including base64). The helper
    ///     refuses a capture that would exceed this size.
    /// </summary>
    public long MaxScreenshotBytes { get; set; } = DefaultMaxScreenshotBytes;

    public void Validate()
    {
        if (MaxScreenshotBytes is < MinMaxScreenshotBytes or > MaxMaxScreenshotBytes)
        {
            throw new InvalidOperationException(
                $"Vestigo:Desktop:MaxScreenshotBytes must be between {MinMaxScreenshotBytes} and {MaxMaxScreenshotBytes}.");
        }
    }
}
