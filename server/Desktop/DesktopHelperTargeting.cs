namespace Vestigo.Desktop;

/// <summary>
///     Resolves a managed session id to a desktop path. An omitted id keeps <c>winsta0\default</c>.
///     This never calls <c>SwitchDesktop</c>.
/// </summary>
public static class DesktopHelperTargeting
{
    public const string InactiveReason = "desktop_inactive";

    public const string Unsupported = "unsupported_on_inactive_desktop";

    public static async Task<OperationResult<DesktopHelperTarget?>> ResolveAsync(
        IManagedDesktopDirectory? desktops,
        string? sessionId,
        bool requireInput,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return OperationResult<DesktopHelperTarget?>.Ok(null);
        }

        if (desktops is null)
        {
            return OperationResult<DesktopHelperTarget?>.Fail(
                "desktop_sessions_disabled",
                "Managed desktop sessions require the Desktop module and Vestigo:Desktop:AllowDesktopSessions.");
        }

        OperationResult<DesktopHelperTarget> described = await desktops.DescribeAsync(sessionId.Trim(), cancellationToken).ConfigureAwait(false);
        if (!described.Success)
        {
            return described.FailAs<DesktopHelperTarget?>();
        }

        if (requireInput && !described.Value!.IsInputDesktop)
        {
            return OperationResult<DesktopHelperTarget?>.Fail(
                Unsupported,
                "This operation needs the test desktop to be the input desktop. Call activate_desktop_session. Nothing was switched.",
                InactiveReason);
        }

        return OperationResult<DesktopHelperTarget?>.Ok(described.Value);
    }
}
