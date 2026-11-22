namespace Vestigo.SystemInfo;

public enum LogonConsentKind
{
    AutoLogonEnable,
    InteractiveLogon,
    UnlockWorkstation
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
        "Display this full URL as a clickable link in your reply so a human can open it in their own visible desktop browser, or launch that OS browser yourself. Do not fetch, preview, or open the URL in any headless, embedded, IDE, or automated browser (including curl, WebFetch, Playwright, and Cursor or VS Code Simple Browser). Do not ask the user to paste the Windows password into chat. After they submit the page, call get_logon_request with this requestId (waitSeconds 0 through 30). The password never appears in MCP results or logs.";
}

/// <summary>Result of locking the active console session.</summary>
public sealed record WorkstationLockResult(
    bool Locked,
    bool AlreadyLocked,
    string ConsoleLockState,
    bool LogonUiConnected,
    uint? ConsoleSessionId,
    string? ConsoleUserName,
    IReadOnlyList<string> Notes);

/// <summary>JSON payload from <c>--session-helper lock-workstation</c>.</summary>
public sealed record WorkstationLockHelperResult(bool Locked);

/// <summary>Locks the active console session. Tests supply a fake.</summary>
public interface IWorkstationLockPlatform
{
    Task<OperationResult<bool>> LockAsync(CancellationToken cancellationToken);
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
