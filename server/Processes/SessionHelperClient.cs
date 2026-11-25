using System.Runtime.InteropServices;
using System.Text.Json;

using Vestigo.Configuration;
using Vestigo.Jobs;
using Vestigo.Security;
using Vestigo.Software;

namespace Vestigo.Processes;

/// <summary>
///     Starts a session helper as the console user and parses the single JSON document it writes to stdout.
///     Window close, desktop enumeration, and screenshots use <c>Vestigo.exe</c>. <c>send_input</c> uses
///     <c>Vestigo.InputHelper.exe</c> when that file is present and trusted in the installation directory.
/// </summary>
public sealed class SessionHelperClient(IProcessLauncher launcher)
{
    public const string InputHelperExecutableName = "Vestigo.InputHelper.exe";

    /// <summary>
    ///     Test hook for the already-resolved <c>send-input</c> executable. Null uses
    ///     <see cref="ResolveInputHelper(string)" />. An empty result uses <c>Vestigo.exe</c>.
    /// </summary>
    internal Func<string?>? InputExecutableOverride { get; set; }

    public Task<OperationResult<T>> RunAsync<T>(
        IReadOnlyList<string> helperArgs,
        bool elevated,
        TimeSpan timeout,
        long maxOutputBytes,
        CancellationToken cancellationToken,
        string? desktop = null,
        SafeHandle? standardInput = null,
        Action? releaseInheritedInput = null,
        IReadOnlyDictionary<string, string>? environment = null) =>
        RunCoreAsync<T>(helperArgs, elevated, timeout, maxOutputBytes, cancellationToken, desktop, standardInput, releaseInheritedInput, environment, input: false);

    /// <summary>
    ///     Runs <c>send-input</c> once. The executable is chosen before the process starts. A timeout, launch
    ///     failure, or malformed response is returned as-is and is never replayed on the other helper.
    /// </summary>
    public Task<OperationResult<T>> RunInputAsync<T>(
        IReadOnlyList<string> helperArgs,
        bool elevated,
        TimeSpan timeout,
        long maxOutputBytes,
        CancellationToken cancellationToken,
        string? desktop = null,
        SafeHandle? standardInput = null,
        Action? releaseInheritedInput = null) =>
        RunCoreAsync<T>(helperArgs, elevated, timeout, maxOutputBytes, cancellationToken, desktop, standardInput, releaseInheritedInput, environment: null, input: true);

    internal static string? ResolveInputHelper(string installDirectory) =>
        ResolveInputHelper(
            installDirectory,
            IsDispatchableExecutable,
            OperatingSystem.IsWindows() ? ExecutableTrust.Check : null);

    internal static string? ResolveInputHelper(
        string installDirectory,
        Func<string, bool> dispatchable,
        Func<string, string?>? trust)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return null;
        }

        string directory = Path.GetFullPath(installDirectory);
        string path = Path.GetFullPath(Path.Combine(directory, InputHelperExecutableName));
        string? parent = Path.GetDirectoryName(path);
        if (parent is null || !SameDirectory(parent, directory))
        {
            return null;
        }

        if (!string.Equals(Path.GetFileName(path), InputHelperExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!dispatchable(path))
        {
            return null;
        }

        if (trust?.Invoke(path) is not null)
        {
            return null;
        }

        return path;
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
            if (document.RootElement.TryGetProperty("success", out _))
            {
                T? command = document.RootElement.Deserialize<T>(SessionHelper.Json);
                return command is null
                    ? OperationResult<T>.Fail("helper_failed", "The session helper returned an empty result.")
                    : OperationResult<T>.Ok(command);
            }

            // A zero exit writes the command payload. Display apply rejections carry their own error field
            // and snapshots; only a nonzero exit is the helper error envelope.
            if (exitCode != 0
                && document.RootElement.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.String)
            {
                string code = document.RootElement.TryGetProperty("errorCode", out JsonElement errorCode) &&
                              errorCode.GetString() is { Length: > 0 } parsed
                    ? parsed
                    : "invalid_request";
                string? reason = document.RootElement.TryGetProperty("reason", out JsonElement reasonElement) &&
                                 reasonElement.ValueKind == JsonValueKind.String
                    ? reasonElement.GetString()
                    : null;
                return OperationResult<T>.Fail(code, error.GetString() ?? "The session helper rejected the request.", reason);
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

    internal static (string Executable, List<string> Prefix) Host() => HelperHost();

    private async Task<OperationResult<T>> RunCoreAsync<T>(
        IReadOnlyList<string> helperArgs,
        bool elevated,
        TimeSpan timeout,
        long maxOutputBytes,
        CancellationToken cancellationToken,
        string? desktop,
        SafeHandle? standardInput,
        Action? releaseInheritedInput,
        IReadOnlyDictionary<string, string>? environment,
        bool input)
    {
        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated);
        if (!available.Success)
        {
            return available.FailAs<T>();
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
            (string executable, List<string> prefix) = input ? InputHost() : HelperHost();
            List<string> arguments = [.. prefix, .. helperArgs];
            LaunchRequest request = new(
                executable,
                arguments,
                Path.GetDirectoryName(executable),
                environment ?? new Dictionary<string, string>(),
                LaunchContext.Interactive,
                elevated,
                desktop,
                StandardInput: standardInput,
                ReleaseInheritedInput: releaseInheritedInput);

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            JobContext output = new("helper", outputPath, maxOutputBytes);
            int exitCode = await launcher.RunAsync(request, output, linked.Token).ConfigureAwait(false);
            return Parse<T>(await File.ReadAllTextAsync(outputPath, cancellationToken).ConfigureAwait(false), exitCode);
        }
        catch (ProcessLaunchException ex)
        {
            return OperationResult<T>.Fail(ex.Code, ex.Message, ex.Reason);
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

    private (string Executable, List<string> Prefix) InputHost()
    {
        string? selected = InputExecutableOverride is not null
            ? InputExecutableOverride()
            : ResolveInputHelper(AppContext.BaseDirectory);
        return string.IsNullOrWhiteSpace(selected) ? HelperHost() : (selected, []);
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

    private static bool IsDispatchableExecutable(string path)
    {
        try
        {
            if (!File.Exists(path) || Directory.Exists(path))
            {
                return false;
            }

            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SameDirectory(string left, string right)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            comparison);
    }
}
