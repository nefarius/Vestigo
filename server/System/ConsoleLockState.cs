namespace Vestigo.SystemInfo;

/// <summary>Console session lock observation used by interactive logon.</summary>
/// <param name="State"><c>locked</c>, <c>unlocked</c>, <c>logonScreen</c>, or <c>unknown</c>.</param>
/// <param name="SessionId">Active console session, or null when Windows reports none.</param>
/// <param name="HasUser">True when that console session has a user token.</param>
/// <param name="UserName">Domain and user when Windows reported one.</param>
public sealed record ConsoleLockRead(
    bool Success,
    string? ErrorCode,
    string? Error,
    string State,
    uint? SessionId,
    bool HasUser,
    string? UserName)
{
    public static ConsoleLockRead Ok(string state, uint? sessionId, bool hasUser, string? userName) =>
        new(true, null, null, state, sessionId, hasUser, userName);

    public static ConsoleLockRead Fail(string code, string error) =>
        new(false, code, error, "unknown", null, false, null);

    public bool CanInteractiveLogon => Success && (State is "locked" or "logonScreen");
}
