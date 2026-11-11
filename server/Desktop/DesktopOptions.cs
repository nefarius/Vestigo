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

    /// <summary>Enables <c>set_window_client_rect</c> and <c>restore_window_placement</c>.</summary>
    public bool AllowWindowPlacement { get; set; }

    /// <summary>Enables temporary or persistent display-layout transactions.</summary>
    public bool AllowDisplayLayoutChanges { get; set; }

    /// <summary>Enables creating, activating, and closing named test desktops in the console user's window station.</summary>
    public bool AllowDesktopSessions { get; set; }

    /// <summary>Most managed test desktops that may be open at once.</summary>
    public int MaxDesktopSessions { get; set; } = 4;

    /// <summary>How long an unclosed managed desktop may keep the input desktop or its handles.</summary>
    public TimeSpan DesktopSessionLease { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Most window-event captures that may run at once.</summary>
    public int MaxWindowEventCaptures { get; set; } = 2;

    /// <summary>Most events stored for one window-event capture.</summary>
    public int MaxWindowEvents { get; set; } = 20_000;

    /// <summary>Largest spool for one window-event capture.</summary>
    public long MaxWindowEventBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>Longest window-event capture, even when a caller asks for more.</summary>
    public TimeSpan MaxWindowEventDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a finished window-event capture and its spool are kept.</summary>
    public TimeSpan WindowEventRetention { get; set; } = TimeSpan.FromHours(6);

    /// <summary>How long an unreleased display-layout transaction may stay applied.</summary>
    public TimeSpan DisplayTransactionLease { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Delay between display-topology samples while waiting for a target.</summary>
    public int DisplayObservationIntervalMilliseconds { get; set; } = 200;

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

        if (MaxWindowEventCaptures is < 1 or > 8)
        {
            throw new InvalidOperationException("Vestigo:Desktop:MaxWindowEventCaptures must be between 1 and 8.");
        }

        if (MaxWindowEvents is < 1 or > 100_000)
        {
            throw new InvalidOperationException("Vestigo:Desktop:MaxWindowEvents must be between 1 and 100000.");
        }

        if (MaxWindowEventBytes is < 64 * 1024 or > 128L * 1024 * 1024)
        {
            throw new InvalidOperationException("Vestigo:Desktop:MaxWindowEventBytes must be between 65536 and 134217728.");
        }

        if (MaxWindowEventDuration <= TimeSpan.Zero || MaxWindowEventDuration > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException("Vestigo:Desktop:MaxWindowEventDuration must be positive and at most one hour.");
        }

        if (WindowEventRetention <= TimeSpan.Zero || WindowEventRetention > TimeSpan.FromDays(7))
        {
            throw new InvalidOperationException("Vestigo:Desktop:WindowEventRetention must be positive and at most 7 days.");
        }

        if (DisplayTransactionLease < TimeSpan.FromSeconds(15) || DisplayTransactionLease > TimeSpan.FromHours(2))
        {
            throw new InvalidOperationException("Vestigo:Desktop:DisplayTransactionLease must be between 15 seconds and 2 hours.");
        }

        if (DisplayObservationIntervalMilliseconds is < 50 or > 2000)
        {
            throw new InvalidOperationException("Vestigo:Desktop:DisplayObservationIntervalMilliseconds must be between 50 and 2000.");
        }

        if (MaxDesktopSessions is < 1 or > 8)
        {
            throw new InvalidOperationException("Vestigo:Desktop:MaxDesktopSessions must be between 1 and 8.");
        }

        if (DesktopSessionLease < TimeSpan.FromSeconds(15) || DesktopSessionLease > TimeSpan.FromHours(2))
        {
            throw new InvalidOperationException("Vestigo:Desktop:DesktopSessionLease must be between 15 seconds and 2 hours.");
        }
    }
}
