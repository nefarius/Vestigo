using System.ComponentModel;

using ModelContextProtocol.Server;

namespace Vestigo.SystemInfo;

/// <summary>MCP tools for locking, unlocking, and signing in the console through the Vestigo Credential Provider.</summary>
[McpServerToolType]
public sealed class InteractiveLogonTools(InteractiveLogonService service)
{
    [McpServerTool(Name = "get_interactive_logon_status", ReadOnly = true)]
    [Description("Returns whether interactive logon is allowed, whether the Credential Provider DLL is installed and registered, whether LogonUI is connected, and the console lock state (locked, unlocked, logonScreen, or unknown). Passwords are never returned.")]
    public OperationResult<InteractiveLogonStatus> GetInteractiveLogonStatus() => service.GetStatus();

    [McpServerTool(Name = "request_interactive_logon", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Starts a one-time consent request to sign in or unlock the console as userName from the Windows logon or lock screen. Returns a URL. Display that full URL as a clickable link so a human can open it in their visible desktop browser, or launch that OS browser; do not fetch or open it in a headless, embedded, IDE, or automated browser, and do not ask for the password in chat. After they submit, call get_logon_request. Requires Vestigo:System:AllowInteractiveLogon, the MSI Interactive logon feature, and a locked or logon-screen console. The password is sent only to LogonUI over a SYSTEM-only named pipe.")]
    public OperationResult<LogonConsentLink> RequestInteractiveLogon(
        [Description("Account name, or DOMAIN\\user.")] string userName,
        [Description("Domain or computer name. Omit for a local account.")] string? domain = null) =>
        service.RequestLogon(userName, domain);

    [McpServerTool(Name = "lock_workstation", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Locks the active console session through LockWorkStation in that session. Requires Vestigo:System:AllowInteractiveLogon. waitSeconds 0 returns after the lock call; 1 through 15 waits for the console to report locked and, when the Credential Provider is registered, for LogonUI to connect. Idempotent when already locked. Does not lock the logon screen.")]
    public Task<OperationResult<WorkstationLockResult>> LockWorkstation(
        [Description("Seconds to wait for the lock screen and LogonUI, 0 to 15.")] int waitSeconds = 5,
        CancellationToken cancellationToken = default) =>
        service.LockWorkstationAsync(waitSeconds, cancellationToken);

    [McpServerTool(Name = "request_unlock_workstation", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Starts a one-time consent request to unlock the locked console. Omitting userName uses the locked console user. Returns a URL. Display that full URL as a clickable link so a human can open it in their visible desktop browser, or launch that OS browser; do not fetch or open it in a headless, embedded, IDE, or automated browser, and do not ask for the password in chat. After they submit, call get_logon_request. Requires Vestigo:System:AllowInteractiveLogon, the MSI Interactive logon feature, a locked console, and a connected LogonUI. The password is sent only to LogonUI over a SYSTEM-only named pipe.")]
    public OperationResult<LogonConsentLink> RequestUnlockWorkstation(
        [Description("Account name, or DOMAIN\\user. Omit to use the locked console user.")] string? userName = null,
        [Description("Domain or computer name. Omit for a local account or when userName is omitted.")] string? domain = null) =>
        service.RequestUnlock(userName, domain);
}
