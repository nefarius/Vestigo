using System.Text;
using System.Text.Json;

using Vestigo.Processes;

namespace Vestigo.Desktop;

/// <summary>
///     Runs display snapshot and apply in the console session. The service keeps the transaction.
///     <c>SetDisplayConfig</c> with <c>SDC_ALLOW_CHANGES</c> is refused (access denied) unless the calling thread's desktop is the
///     input desktop, so a managed desktop that is active must be the helper's desktop (<paramref name="desktopPath" />).
/// </summary>
public sealed class HelperDisplayLayoutClient(SessionHelperClient helper, IProcessLauncher launcher) : IDisplayLayoutClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public Task<OperationResult<DisplayLayoutSnapshot>> SnapshotAsync(bool elevated, CancellationToken cancellationToken, string? desktopPath = null)
    {
        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated);
        return !available.Success
            ? Task.FromResult(available.FailAs<DisplayLayoutSnapshot>())
            : helper.RunAsync<DisplayLayoutSnapshot>(
                SessionHelper.BuildSnapshotDisplayArguments(),
                elevated,
                Timeout,
                1024 * 1024,
                cancellationToken,
                desktopPath);
    }

    public Task<OperationResult<DisplayLayoutApplyResult>> ApplyAsync(
        DisplayLayoutRequest request,
        bool elevated,
        CancellationToken cancellationToken,
        string? desktopPath = null)
    {
        List<string> arguments = SessionHelper.BuildApplyDisplayArguments(request);
        if (arguments[^1].Length > SessionHelper.MaxBatchArgumentLength)
        {
            return Task.FromResult(OperationResult<DisplayLayoutApplyResult>.Fail(
                "invalid_request",
                "The display layout request is too large for the session helper."));
        }

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated);
        return !available.Success
            ? Task.FromResult(available.FailAs<DisplayLayoutApplyResult>())
            : helper.RunAsync<DisplayLayoutApplyResult>(arguments, elevated, Timeout, 1024 * 1024, cancellationToken, desktopPath);
    }
}
