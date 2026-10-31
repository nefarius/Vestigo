using System.Text.Json;
using System.Text.RegularExpressions;

using Vestigo.Configuration;
using Vestigo.Jobs;

namespace Vestigo.Processes;

/// <summary>
///     Sends WM_CLOSE. Windows on the console user's desktop are only visible from inside that session, so the
///     interactive path starts <c>Vestigo.exe --session-helper close-window</c> there and parses its JSON output.
/// </summary>
public sealed class AppCloser(IProcessLauncher launcher)
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

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated);
        if (!available.Success)
        {
            return OperationResult<CloseReport>.Fail(available.ErrorCode!, available.Error!);
        }

        string directory = Path.Combine(VestigoPaths.SpoolDirectory, "helper");
        Directory.CreateDirectory(directory);
        string outputPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(outputPath, []);
        try
        {
            (string executable, List<string> prefix) = HelperHost();
            List<string> arguments = [.. prefix, .. SessionHelper.BuildCloseArguments(selector, graceSeconds, Environment.ProcessId)];
            LaunchRequest request = new(
                executable,
                arguments,
                Path.GetDirectoryName(executable),
                new Dictionary<string, string>(),
                LaunchContext.Interactive,
                elevated);

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(graceSeconds + 30));
            JobContext output = new("helper", outputPath, 1024 * 1024);
            int exitCode = await launcher.RunAsync(request, output, timeout.Token).ConfigureAwait(false);
            return ParseHelperOutput(await File.ReadAllTextAsync(outputPath, cancellationToken).ConfigureAwait(false), exitCode);
        }
        catch (ProcessLaunchException ex)
        {
            return OperationResult<CloseReport>.Fail(ex.Code, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OperationResult<CloseReport>.Fail("helper_timeout", "The session helper did not finish in time.");
        }
        finally
        {
            try
            {
                File.Delete(outputPath);
            }
            catch (IOException)
            {
                // Swept with the spool later.
            }
        }
    }

    internal static OperationResult<CloseReport> ParseHelperOutput(string text, int exitCode)
    {
        string? line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(candidate => candidate.StartsWith('{'));
        if (line is null)
        {
            return OperationResult<CloseReport>.Fail("helper_failed", $"The session helper exited with code {exitCode} and printed no result.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                return OperationResult<CloseReport>.Fail("invalid_request", error.GetString() ?? "The session helper rejected the request.");
            }

            CloseReport? report = document.RootElement.Deserialize<CloseReport>(SessionHelper.Json);
            return report is null
                ? OperationResult<CloseReport>.Fail("helper_failed", "The session helper returned an empty result.")
                : OperationResult<CloseReport>.Ok(report);
        }
        catch (JsonException ex)
        {
            return OperationResult<CloseReport>.Fail("helper_failed", "The session helper returned invalid JSON: " + ex.Message);
        }
    }

    /// <summary>The executable that hosts Vestigo, plus a DLL argument when started through <c>dotnet</c>.</summary>
    private static (string Executable, List<string> Prefix) HelperHost()
    {
        string host = Environment.ProcessPath ?? throw new ProcessLaunchException("start_failed", "Cannot determine the Vestigo executable.");
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string dll = Path.Combine(AppContext.BaseDirectory, "Vestigo.dll");
            return (host, [dll]);
        }

        return (host, []);
    }
}
