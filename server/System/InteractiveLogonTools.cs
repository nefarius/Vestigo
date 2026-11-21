using System.ComponentModel;

using ModelContextProtocol.Server;

namespace Vestigo.SystemInfo;

/// <summary>MCP tools for signing in or unlocking the console through the Vestigo Credential Provider.</summary>
[McpServerToolType]
public sealed class InteractiveLogonTools(InteractiveLogonService service)
{
    [McpServerTool(Name = "get_interactive_logon_status", ReadOnly = true)]
    [Description("Returns whether interactive logon is allowed, whether the Credential Provider DLL is installed and registered, whether LogonUI is connected, and the console lock state (locked, unlocked, logonScreen, or unknown). Passwords are never returned.")]
    public OperationResult<InteractiveLogonStatus> GetInteractiveLogonStatus() => service.GetStatus();

    [McpServerTool(Name = "request_interactive_logon", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Starts a one-time consent request to sign in or unlock the console as userName from the Windows logon or lock screen. Returns a URL the user must open to enter the Windows password. Present that URL; do not fetch it and do not ask for the password in chat. Requires Vestigo:System:AllowInteractiveLogon, the MSI Interactive logon feature, and a locked or logon-screen console. The password is sent only to LogonUI over a SYSTEM-only named pipe.")]
    public OperationResult<LogonConsentLink> RequestInteractiveLogon(
        [Description("Account name, or DOMAIN\\user.")] string userName,
        [Description("Domain or computer name. Omit for a local account.")] string? domain = null) =>
        service.RequestLogon(userName, domain);
}
