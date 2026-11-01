using System.Text.Json;

using Vestigo.Configuration;
using Vestigo.Jobs;
using Vestigo.Security;

namespace Vestigo.Processes;

/// <summary>
///     Starts <c>Vestigo.exe --session-helper ...</c> as the console user and parses the single JSON document it
///     writes to stdout. Used by window close and by desktop enumeration, which only see the caller's desktop.
/// </summary>
public sealed class SessionHelperClient(IProcessLauncher launcher)
{
    public async Task<OperationResult<T>> RunAsync<T>(
        IReadOnlyList<string> helperArgs,
        bool elevated,
        TimeSpan timeout,
        long maxOutputBytes,
        CancellationToken cancellationToken)
    {
        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated);
        if (!available.Success)
        {
            return OperationResult<T>.Fail(available.ErrorCode!, available.Error!);
        }

        string directory;
        try
        {
            directory = StateDirectories.EnsureSubdirectory(Path.Combine(VestigoPaths.SpoolDirectory, "helper"));
        }
        catch (InvalidOperationException ex)
        {
            return OperationResult<T>.Fail("untrusted_path", ex.Message);
        }

        string outputPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(outputPath, []);
        try
        {
            (string executable, List<string> prefix) = HelperHost();
            List<string> arguments = [.. prefix, .. helperArgs];
            LaunchRequest request = new(
                executable,
                arguments,
                Path.GetDirectoryName(executable),
                new Dictionary<string, string>
                {
                    ["VESTIGO_SETTINGS_DIR"] = VestigoPaths.SettingsDirectory
                },
                LaunchContext.Interactive,
                elevated);

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            JobContext output = new("helper", outputPath, maxOutputBytes);
            int exitCode = await launcher.RunAsync(request, output, linked.Token).ConfigureAwait(false);
            return Parse<T>(await File.ReadAllTextAsync(outputPath, cancellationToken).ConfigureAwait(false), exitCode);
        }
        catch (ProcessLaunchException ex)
        {
            return OperationResult<T>.Fail(ex.Code, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OperationResult<T>.Fail("helper_timeout", "The session helper did not finish in time.");
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

    internal static OperationResult<T> Parse<T>(string text, int exitCode)
    {
        string? line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(candidate => candidate.StartsWith('{'));
        if (line is null)
        {
            return OperationResult<T>.Fail("helper_failed", $"The session helper exited with code {exitCode} and printed no result.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                return OperationResult<T>.Fail("invalid_request", error.GetString() ?? "The session helper rejected the request.");
            }

            T? value = document.RootElement.Deserialize<T>(SessionHelper.Json);
            return value is null
                ? OperationResult<T>.Fail("helper_failed", "The session helper returned an empty result.")
                : OperationResult<T>.Ok(value);
        }
        catch (JsonException ex)
        {
            return OperationResult<T>.Fail("helper_failed", "The session helper returned invalid JSON: " + ex.Message);
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
