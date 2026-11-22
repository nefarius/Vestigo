using Vestigo.Processes;

namespace Vestigo.SystemInfo;

/// <summary>
///     Runs <c>Vestigo.exe --session-helper lock-workstation</c> as the console user so
///     <c>LockWorkStation</c> targets that window station, not Session 0.
/// </summary>
public sealed class SessionHelperWorkstationLockPlatform(SessionHelperClient helper) : IWorkstationLockPlatform
{
    public async Task<OperationResult<bool>> LockAsync(CancellationToken cancellationToken)
    {
        OperationResult<WorkstationLockHelperResult> result = await helper
            .RunAsync<WorkstationLockHelperResult>(
                SessionHelper.BuildLockWorkStationArguments(),
                elevated: false,
                TimeSpan.FromSeconds(10),
                maxOutputBytes: 4096,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            return result.FailAs<bool>();
        }

        return result.Value is { Locked: true }
            ? OperationResult<bool>.Ok(true)
            : OperationResult<bool>.Fail("lock_failed", "Windows did not lock the workstation.");
    }
}
