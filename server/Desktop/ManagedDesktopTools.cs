using System.ComponentModel;

using ModelContextProtocol.Server;

namespace Vestigo.Desktop;

/// <summary>Creates, inspects, activates, and closes named test desktops in the console user's window station.</summary>
[McpServerToolType]
public sealed class ManagedDesktopTools(ManagedDesktopSessionManager sessions)
{
    [McpServerTool(Name = "create_desktop_session", Destructive = true)]
    [Description(
        "Creates a named test desktop on the console user's window station and returns its session id. "
        + "The desktop name is assigned by Vestigo and always starts with Vestigo-. The call records the current input desktop and does not switch to the new one. "
        + "Secure desktops, including the lock screen and UAC, are refused. "
        + "cleanupPolicy terminate-owned (default) is used for lease expiry, cancellation, service stop, and startup recovery. "
        + "fail-if-running makes an explicit close leave the session open while a process it launched is still running. "
        + "Requires Vestigo:Desktop:AllowDesktopSessions.")]
    public Task<OperationResult<ManagedDesktopSession>> CreateDesktopSession(
        [Description("terminate-owned (default) or fail-if-running. Automatic cleanup always uses terminate-owned.")] string cleanupPolicy = "terminate-owned",
        CancellationToken cancellationToken = default) =>
        sessions.CreateAsync(cleanupPolicy, cancellationToken);

    [McpServerTool(Name = "list_desktop_sessions", ReadOnly = true)]
    [Description("Lists managed desktop sessions, including closed sessions still remembered by this process. This does not switch desktops.")]
    public OperationResult<IReadOnlyList<ManagedDesktopSession>> ListDesktopSessions() => sessions.List();

    [McpServerTool(Name = "get_desktop_session", ReadOnly = true)]
    [Description("Returns one managed desktop session by the id from create_desktop_session.")]
    public OperationResult<ManagedDesktopSession> GetDesktopSession(
        [Description("Managed desktop session id.")] string id) =>
        sessions.Get(id);

    [McpServerTool(Name = "list_window_station_desktops", ReadOnly = true)]
    [Description(
        "Lists desktops on the console user's window station and classifies each as owned, user, or secure. "
        + "Secure desktops are not opened. This does not create, activate, or close a desktop. Interactive session only.")]
    public Task<OperationResult<WindowStationDesktopList>> ListWindowStationDesktops(CancellationToken cancellationToken = default) =>
        sessions.EnumerateAsync(cancellationToken);

    [McpServerTool(Name = "activate_desktop_session", Destructive = true)]
    [Description(
        "Makes a managed test desktop the input desktop. This is explicit: no other tool switches desktops. "
        + "The previous input desktop was recorded at creation and can be restored. "
        + "Refuses while the lock screen or UAC secure desktop is active. Requires Vestigo:Desktop:AllowDesktopSessions.")]
    public Task<OperationResult<ManagedDesktopSession>> ActivateDesktopSession(
        [Description("Managed desktop session id.")] string id,
        CancellationToken cancellationToken = default) =>
        sessions.ActivateAsync(id, cancellationToken);

    [McpServerTool(Name = "restore_desktop_session", Destructive = true)]
    [Description(
        "Switches the input desktop back to the one recorded before this session was activated, when this session is still the input desktop. "
        + "If the user or another session has since changed the input desktop, the call returns input_desktop_changed and does not switch. "
        + "Refuses while a secure desktop is active. Requires Vestigo:Desktop:AllowDesktopSessions.")]
    public Task<OperationResult<ManagedDesktopSession>> RestoreDesktopSession(
        [Description("Managed desktop session id.")] string id,
        CancellationToken cancellationToken = default) =>
        sessions.RestoreAsync(id, cancellationToken);

    [McpServerTool(Name = "close_desktop_session", Destructive = true)]
    [Description(
        "Restores the recorded input desktop when this session switched it, applies the cleanup policy, and releases the desktop handle this service holds. "
        + "terminate-owned cancels owned jobs and then kills any of those processes still running. fail-if-running returns processes_still_running and changes nothing while one is alive. "
        + "Closing the handle does not stop an unowned process and does not destroy a desktop Windows still considers in use; desktopRetained reports that. "
        + "Requires Vestigo:Desktop:AllowDesktopSessions.")]
    public Task<OperationResult<ManagedDesktopSession>> CloseDesktopSession(
        [Description("Managed desktop session id.")] string id,
        [Description("Omit to use the policy from create_desktop_session. terminate-owned or fail-if-running.")] string? cleanupPolicy = null,
        CancellationToken cancellationToken = default) =>
        sessions.CloseAsync(id, cleanupPolicy, cancellationToken);
}
