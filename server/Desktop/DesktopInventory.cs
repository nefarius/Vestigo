using System.Text.RegularExpressions;

using Vestigo.Processes;

namespace Vestigo.Desktop;

/// <summary>
///     Lists windows and displays on the service desktop or, through the session helper, on the console user's
///     desktop.
/// </summary>
public sealed class DesktopInventory(IProcessLauncher launcher, SessionHelperClient helper)
{
    internal const long HelperMaxOutputBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(30);

    public async Task<OperationResult<WindowListResult>> ListWindowsAsync(
        WindowSelector selector,
        LaunchContext context,
        bool elevated,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = WindowSelector.CompileTitle(selector.TitlePattern);
        }
        catch (RegexParseException ex)
        {
            return OperationResult<WindowListResult>.Fail("invalid_request", "titleRegex is not a valid regular expression: " + ex.Message);
        }

        if (context == LaunchContext.Service)
        {
            if (!OperatingSystem.IsWindows())
            {
                return OperationResult<WindowListResult>.Fail("windows_required", "Window enumeration requires Windows.");
            }

            return OperationResult<WindowListResult>.Ok(
                await Task.Run(() => WindowEnumerator.List(selector), cancellationToken).ConfigureAwait(false));
        }

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated);
        if (!available.Success)
        {
            return OperationResult<WindowListResult>.Fail(available.ErrorCode!, available.Error!);
        }

        return await helper.RunAsync<WindowListResult>(
            SessionHelper.BuildListWindowsArguments(selector),
            elevated,
            HelperTimeout,
            HelperMaxOutputBytes,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<DisplayListResult>> ListDisplaysAsync(
        LaunchContext context,
        bool elevated,
        CancellationToken cancellationToken)
    {
        if (context == LaunchContext.Service)
        {
            if (!OperatingSystem.IsWindows())
            {
                return OperationResult<DisplayListResult>.Fail("windows_required", "Display enumeration requires Windows.");
            }

            return OperationResult<DisplayListResult>.Ok(
                await Task.Run(DisplayTopology.List, cancellationToken).ConfigureAwait(false));
        }

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated);
        if (!available.Success)
        {
            return OperationResult<DisplayListResult>.Fail(available.ErrorCode!, available.Error!);
        }

        return await helper.RunAsync<DisplayListResult>(
            SessionHelper.BuildListDisplaysArguments(),
            elevated,
            HelperTimeout,
            HelperMaxOutputBytes,
            cancellationToken).ConfigureAwait(false);
    }
}
