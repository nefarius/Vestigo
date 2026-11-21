namespace Vestigo.SystemInfo;

public enum LogonConsentKind
{
    AutoLogonEnable,
    InteractiveLogon
}

public enum LogonRequestStatus
{
    Pending,
    Approved,
    Rejected,
    Expired,
    Failed,
    Cancelled,
    Submitted
}

/// <summary>One-time URL the MCP client must present to the user. The ticket is the only credential.</summary>
public sealed record LogonConsentLink(
    string RequestId,
    string Url,
    DateTimeOffset ExpiresUtc,
    string Kind,
    string UserName,
    string? Domain,
    string Instruction)
{
    public const string PresentToUser =
        "Present this URL to the user so they can approve the request and enter the Windows password in their browser. Do not fetch the URL yourself. Do not ask the user to paste the password into chat. The password never appears in MCP results or logs.";
}

/// <summary>Status of a consent request. Credentials are never included.</summary>
public sealed record LogonRequestView(
    string RequestId,
    string Kind,
    string Status,
    string UserName,
    string? Domain,
    int? LogonCount,
    bool ForceAutoLogon,
    DateTimeOffset ExpiresUtc,
    string? ErrorCode,
    string? Error,
    IReadOnlyList<string> Notes);

public sealed record AutoLogonDisableResult(
    bool Enabled,
    string? UserName,
    string? Domain,
    IReadOnlyList<string> Notes);

public sealed record InteractiveLogonStatus(
    bool AllowInteractiveLogon,
    bool ProviderInstalled,
    bool ProviderRegistered,
    bool LogonUiConnected,
    string ConsoleLockState,
    uint? ConsoleSessionId,
    string? ConsoleUserName,
    IReadOnlyList<string> Notes);
