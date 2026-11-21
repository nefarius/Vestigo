namespace Vestigo.SystemInfo;

/// <summary>Values rendered by the out-of-band MudBlazor SSR consent page. Passwords are never included.</summary>
public sealed class LogonConsentPageModel
{
    public string Title { get; init; } = "Vestigo logon consent";

    public string Machine { get; init; } = Environment.MachineName;

    public string? Operation { get; init; }

    public string? Account { get; init; }

    public string? Details { get; init; }

    public string? Ticket { get; init; }

    public string? AntiforgeryField { get; init; }

    public string? AntiforgeryValue { get; init; }

    public string? Message { get; init; }

    public string? Error { get; init; }

    public bool ShowForm { get; init; }

    public bool HttpWarning { get; init; }

    public int RemainingAttempts { get; init; }
}
