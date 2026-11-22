using System.ComponentModel;

using ModelContextProtocol.Server;

namespace Vestigo.SystemInfo;

/// <summary>MCP tools for Windows AutoLogon. Passwords are collected through a one-time consent URL, never as tool arguments.</summary>
[McpServerToolType]
public sealed class AutoLogonTools(AutoLogonService service, LogonRequestService requests)
{
    [McpServerTool(Name = "get_autologon_state", ReadOnly = true)]
    [Description("Returns whether Windows AutoLogon is enabled, the default user and domain, AutoLogonCount, ForceAutoLogon, whether an LSA DefaultPassword secret is present, and whether a legacy plaintext Winlogon DefaultPassword value exists. The password is never returned.")]
    public OperationResult<AutoLogonState> GetAutologonState() => service.GetState();

    [McpServerTool(Name = "request_autologon_enable", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Starts a one-time consent request to enable Windows AutoLogon for userName (optional domain, optional logonCount, optional forceAutoLogon). Returns a URL. Display that full URL as a clickable link so a human can open it in their visible desktop browser, or launch that OS browser; do not fetch or open it in a headless, embedded, IDE, or automated browser, and do not ask for the password in chat. After they submit, call get_logon_request. Requires Vestigo:System:AllowAutoLogonChanges. The password is stored as an LSA private secret, never in appsettings.json, MCP results, or logs.")]
    public OperationResult<LogonConsentLink> RequestAutologonEnable(
        [Description("Account name, or DOMAIN\\user.")] string userName,
        [Description("Domain or computer name. Omit for a local account.")] string? domain = null,
        [Description("Optional remaining automatic logons. Omit for persistent AutoLogon.")] int? logonCount = null,
        [Description("Ignore the Shift key that would skip AutoLogon.")] bool forceAutoLogon = false) =>
        service.RequestEnable(userName, domain, logonCount, forceAutoLogon);

    [McpServerTool(Name = "disable_autologon", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Turns AutoAdminLogon off, deletes the LSA DefaultPassword secret, and removes any legacy plaintext Winlogon DefaultPassword value. Requires Vestigo:System:AllowAutoLogonChanges. clearDefaultUser also removes DefaultUserName and DefaultDomainName.")]
    public OperationResult<AutoLogonDisableResult> DisableAutologon(
        [Description("Also clear DefaultUserName and DefaultDomainName.")] bool clearDefaultUser = false) =>
        service.Disable(clearDefaultUser);

    [McpServerTool(Name = "get_logon_request", ReadOnly = true)]
    [Description("Returns the status of a request_autologon_enable, request_interactive_logon, or request_unlock_workstation consent request. waitSeconds 0 returns immediately; 1 through 30 waits for the human to finish the consent page. The password is never returned.")]
    public Task<OperationResult<LogonRequestView>> GetLogonRequest(
        [Description("Request id from request_autologon_enable, request_interactive_logon, or request_unlock_workstation.")] string requestId,
        [Description("Seconds to wait for completion, 0 to 30.")] int waitSeconds = 0,
        CancellationToken cancellationToken = default) =>
        requests.GetAsync(requestId, waitSeconds, cancellationToken);

    [McpServerTool(Name = "cancel_logon_request", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Cancels a pending AutoLogon, interactive-logon, or unlock consent request so the URL stops working.")]
    public OperationResult<LogonRequestView> CancelLogonRequest(
        [Description("Request id from request_autologon_enable, request_interactive_logon, or request_unlock_workstation.")] string requestId) =>
        requests.Cancel(requestId);
}
