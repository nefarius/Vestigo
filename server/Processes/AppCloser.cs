using System.Text.RegularExpressions;

namespace Vestigo.Processes;

/// <summary>
///     Sends WM_CLOSE. Windows on the console user's desktop are only visible from inside that session, so the
///     interactive path starts <c>Vestigo.exe --session-helper close-window</c> there and parses its JSON output.
/// </summary>
public sealed class AppCloser(SessionHelperClient helper)
{
    public async Task<OperationResult<CloseReport>> CloseAsync(
        CloseSelector selector,
        int graceSeconds,
        LaunchContext context,
        bool elevated,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = CloseSelector.CompileTitle(selector.TitlePattern);
        }
        catch (RegexParseException ex)
        {
            return OperationResult<CloseReport>.Fail("invalid_request", "titleRegex is not a valid regular expression: " + ex.Message);
        }

        if (context == LaunchContext.Service)
        {
            return OperationResult<CloseReport>.Ok(
                await Task.Run(() => WindowCloser.Close(selector, TimeSpan.FromSeconds(graceSeconds), Environment.ProcessId), cancellationToken).ConfigureAwait(false));
        }

        return await helper.RunAsync<CloseReport>(
            SessionHelper.BuildCloseArguments(selector, graceSeconds, Environment.ProcessId),
            elevated,
            TimeSpan.FromSeconds(graceSeconds + 30),
            1024 * 1024,
            cancellationToken).ConfigureAwait(false);
    }

    internal static OperationResult<CloseReport> ParseHelperOutput(string text, int exitCode) =>
        SessionHelperClient.Parse<CloseReport>(text, exitCode);
}
