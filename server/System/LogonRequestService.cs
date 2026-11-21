namespace Vestigo.SystemInfo;

/// <summary>Shared poll/cancel for AutoLogon and interactive-logon consent requests.</summary>
public sealed class LogonRequestService(LogonConsentStore store)
{
    public async Task<OperationResult<LogonRequestView>> GetAsync(
        string? requestId,
        int waitSeconds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return OperationResult<LogonRequestView>.Fail("invalid_request", "requestId is required.");
        }

        if (waitSeconds is < 0 or > 30)
        {
            return OperationResult<LogonRequestView>.Fail("invalid_request", "waitSeconds must be between 0 and 30.");
        }

        LogonRequestView? view = waitSeconds == 0
            ? store.Peek(requestId)
            : await store.WaitAsync(requestId, TimeSpan.FromSeconds(waitSeconds), cancellationToken).ConfigureAwait(false);
        return view is null
            ? OperationResult<LogonRequestView>.Fail("unknown_request", "The consent request is unknown or has expired.")
            : OperationResult<LogonRequestView>.Ok(view);
    }

    public OperationResult<LogonRequestView> Cancel(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return OperationResult<LogonRequestView>.Fail("invalid_request", "requestId is required.");
        }

        if (!store.Cancel(requestId))
        {
            LogonRequestView? existing = store.Peek(requestId);
            return existing is null
                ? OperationResult<LogonRequestView>.Fail("unknown_request", "The consent request is unknown or has expired.")
                : OperationResult<LogonRequestView>.Fail("already_completed", "This consent request was already completed.");
        }

        return OperationResult<LogonRequestView>.Ok(store.Peek(requestId)!);
    }
}
