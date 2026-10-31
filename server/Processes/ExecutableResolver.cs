using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Processes;

/// <summary>An executable or script that passed resolution.</summary>
public sealed record ResolvedExecutable(string Path, bool FromRoot);

/// <summary>
///     Resolves the <c>path</c> given to <c>run_process</c>: files under roots with Execute access, or (when
///     enabled) bare names found on PATH.
/// </summary>
public sealed class ExecutableResolver
{
    private static readonly string[] BareExtensions = [".exe", ".com", ".cmd", ".bat"];

    private readonly RootPolicy _roots;
    private readonly ProcessesOptions _options;
    private readonly Func<string?> _path;
    private readonly string? _pathExt;
    private readonly Func<string, bool> _fileExists;

    public ExecutableResolver(RootPolicy roots, VestigoOptions options)
        : this(
            roots,
            options.Processes,
            () => Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT"),
            File.Exists)
    {
    }

    internal ExecutableResolver(
        RootPolicy roots,
        ProcessesOptions options,
        Func<string?> path,
        string? pathExt,
        Func<string, bool> fileExists)
    {
        _roots = roots;
        _options = options;
        _path = path;
        _pathExt = pathExt;
        _fileExists = fileExists;
    }

    public static bool IsBareName(string requested) =>
        requested.Length > 0 && requested.IndexOfAny(['\\', '/', ':']) < 0;

    public OperationResult<ResolvedExecutable> Resolve(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || requested.Contains('\0'))
        {
            return OperationResult<ResolvedExecutable>.Fail("invalid_path", "path is required.");
        }

        requested = requested.Trim();
        if (!IsBareName(requested))
        {
            PathCheck check = _roots.Resolve(requested, RootAccess.Execute);
            if (!check.Success)
            {
                return OperationResult<ResolvedExecutable>.Fail(check.ErrorCode!, check.Error!);
            }

            return _fileExists(check.FullPath!)
                ? OperationResult<ResolvedExecutable>.Ok(new ResolvedExecutable(check.FullPath!, true))
                : OperationResult<ResolvedExecutable>.Fail("not_found", $"File does not exist: '{check.FullPath}'.");
        }

        if (!_options.AllowPathLookup)
        {
            return OperationResult<ResolvedExecutable>.Fail(
                "path_lookup_disabled",
                "Bare executable names are disabled (Vestigo:Processes:AllowPathLookup). Use a file under a root with Execute access.");
        }

        if (requested.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || requested.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<ResolvedExecutable>.Fail(
                "invalid_path",
                "Bare names must be an executable file name; scripts must live under a root with Execute access.");
        }

        string? found = FindOnPath(requested);
        return found is null
            ? OperationResult<ResolvedExecutable>.Fail("not_found", $"'{requested}' was not found on PATH.")
            : OperationResult<ResolvedExecutable>.Ok(new ResolvedExecutable(found, false));
    }

    /// <summary>Searches PATH (never the current directory) for a name, trying PATHEXT when it has no extension.</summary>
    public string? FindOnPath(string name)
    {
        string[] candidates = Path.HasExtension(name)
            ? [name]
            : AllowedExtensions().Select(extension => name + extension).ToArray();

        foreach (string directory in (_path() ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string trimmed = directory.Trim('"');
            if (!Path.IsPathRooted(trimmed))
            {
                continue;
            }

            foreach (string candidate in candidates)
            {
                string full;
                try
                {
                    full = Path.Combine(trimmed, candidate);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (_fileExists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }

    private IEnumerable<string> AllowedExtensions()
    {
        IEnumerable<string> configured = (_pathExt ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(extension => extension.ToLowerInvariant())
            .Where(extension => BareExtensions.Contains(extension));
        return configured.Concat(BareExtensions).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
