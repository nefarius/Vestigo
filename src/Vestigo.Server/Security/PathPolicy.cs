namespace Vestigo.Security;

/// <summary>
///     Canonicalizes client paths and confines them to configured roots.
/// </summary>
public sealed class PathPolicy(Vestigo.Configuration.VestigoOptions options)
{
    public PathCheck RequireExistingFile(string requestedPath, IReadOnlyCollection<string> extensions)
    {
        PathCheck located = Locate(requestedPath, options.AllowedInputRoots, "input");
        if (!located.Success)
        {
            return located;
        }

        if (!HasExtension(located.FullPath!, extensions))
        {
            return PathCheck.Fail("unsupported_extension", $"'{Path.GetFileName(requestedPath)}' uses an unsupported extension.");
        }

        if (!File.Exists(located.FullPath))
        {
            return PathCheck.Fail("not_found", $"File does not exist: '{located.FullPath}'.");
        }

        long length = new FileInfo(located.FullPath).Length;
        if (length > options.MaxInputBytes)
        {
            return PathCheck.Fail("file_too_large", $"'{Path.GetFileName(located.FullPath)}' exceeds the input size limit.");
        }

        return located;
    }

    public PathCheck RequireOutputFile(string requestedPath, IReadOnlyCollection<string> extensions, bool overwrite)
    {
        PathCheck located = Locate(requestedPath, options.AllowedOutputRoots, "output");
        if (!located.Success)
        {
            return located;
        }

        if (!HasExtension(located.FullPath!, extensions))
        {
            return PathCheck.Fail("unsupported_extension", $"Output '{Path.GetFileName(requestedPath)}' uses an unsupported extension.");
        }

        if (File.Exists(located.FullPath) && !overwrite)
        {
            return PathCheck.Fail("already_exists", $"Output already exists: '{located.FullPath}'.");
        }

        string? directory = Path.GetDirectoryName(located.FullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return PathCheck.Fail("invalid_path", "Output path does not have a directory.");
        }

        Directory.CreateDirectory(directory);
        return located;
    }

    public IReadOnlyList<PathCheck> RequireExistingFiles(
        IEnumerable<string> requestedPaths,
        IReadOnlyCollection<string> extensions)
    {
        List<PathCheck> results = [];
        foreach (string path in requestedPaths)
        {
            results.Add(RequireExistingFile(path, extensions));
        }

        return results;
    }

    private static PathCheck Locate(string requestedPath, IReadOnlyList<string> roots, string rootKind)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return PathCheck.Fail("invalid_path", "Path is required.");
        }

        if (roots.Count == 0)
        {
            return PathCheck.Fail("roots_not_configured", $"No allowed {rootKind} roots are configured.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(requestedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return PathCheck.Fail("invalid_path", "Path is not a valid filesystem path.");
        }

        foreach (string root in roots)
        {
            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(root);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            string prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
            if (fullPath.Equals(fullRoot, PathComparison()) ||
                fullPath.StartsWith(prefix, PathComparison()))
            {
                return PathCheck.Ok(fullPath);
            }
        }

        return PathCheck.Fail("outside_root", $"Path is outside the allowed {rootKind} roots.");
    }

    private static bool HasExtension(string path, IReadOnlyCollection<string> extensions) =>
        extensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

/// <summary>
///     Outcome of a path-policy check.
/// </summary>
public sealed record PathCheck(bool Success, string? FullPath, string? ErrorCode, string? Error)
{
    public static PathCheck Ok(string fullPath) => new(true, fullPath, null, null);

    public static PathCheck Fail(string code, string error) => new(false, null, code, error);
}
