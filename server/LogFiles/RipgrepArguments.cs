using System.Globalization;

using Vestigo.Configuration;

namespace Vestigo.LogFiles;

/// <summary>What a client asked ripgrep to search for, after path resolution.</summary>
public sealed record SearchRequest(
    string FullPath,
    string Pattern,
    bool FixedStrings,
    bool IgnoreCase,
    bool WholeWord,
    int ContextBefore,
    int ContextAfter,
    int? MaxMatchesPerFile,
    IReadOnlyList<string> Globs);

/// <summary>
///     Builds the ripgrep argument list. Clients never pass free-form arguments: the pattern follows <c>-e</c>, each
///     glob is attached with <c>--glob=</c>, and the path comes last after <c>--</c>, so no value can become a flag.
/// </summary>
public static class RipgrepArguments
{
    public const int MaxPatternLength = 1024;
    public const int MaxGlobs = 16;
    public const int MaxGlobLength = 256;

    public static OperationResult<IReadOnlyList<string>> Build(SearchRequest request, LogsOptions options)
    {
        if (string.IsNullOrEmpty(request.Pattern))
        {
            return Invalid("pattern is required.");
        }

        if (request.Pattern.Length > MaxPatternLength)
        {
            return Invalid($"pattern is longer than {MaxPatternLength} characters.");
        }

        if (request.Pattern.Contains('\0') || request.Pattern.Contains('\n') || request.Pattern.Contains('\r'))
        {
            return Invalid("pattern cannot contain line breaks or NUL.");
        }

        if (request.Globs.Count > MaxGlobs)
        {
            return Invalid($"at most {MaxGlobs} globs are allowed.");
        }

        foreach (string glob in request.Globs)
        {
            if (string.IsNullOrWhiteSpace(glob) || glob.Length > MaxGlobLength ||
                glob.Any(static c => c is '\0' or '\n' or '\r'))
            {
                return Invalid($"each glob must be 1 to {MaxGlobLength} characters without line breaks.");
            }
        }

        if (request.MaxMatchesPerFile is < 1)
        {
            return Invalid("maxMatchesPerFile must be at least 1.");
        }

        List<string> arguments =
        [
            "--no-config",
            "--json",
            "--hidden",
            "--no-ignore",
            "--no-follow",
            "--threads=2",
            "--glob-case-insensitive"
        ];

        if (request.FixedStrings)
        {
            arguments.Add("--fixed-strings");
        }

        if (request.IgnoreCase)
        {
            arguments.Add("--ignore-case");
        }

        if (request.WholeWord)
        {
            arguments.Add("--word-regexp");
        }

        int before = Math.Clamp(request.ContextBefore, 0, options.MaxSearchContextLines);
        int after = Math.Clamp(request.ContextAfter, 0, options.MaxSearchContextLines);
        if (before > 0)
        {
            arguments.Add("--before-context=" + before.ToString(CultureInfo.InvariantCulture));
        }

        if (after > 0)
        {
            arguments.Add("--after-context=" + after.ToString(CultureInfo.InvariantCulture));
        }

        if (request.MaxMatchesPerFile is int perFile)
        {
            arguments.Add("--max-count=" + perFile.ToString(CultureInfo.InvariantCulture));
        }

        foreach (string glob in request.Globs)
        {
            arguments.Add("--glob=" + glob);
        }

        arguments.Add("-e");
        arguments.Add(request.Pattern);
        arguments.Add("--");
        arguments.Add(request.FullPath);
        return OperationResult<IReadOnlyList<string>>.Ok(arguments);
    }

    private static OperationResult<IReadOnlyList<string>> Invalid(string message) =>
        OperationResult<IReadOnlyList<string>>.Fail("invalid_request", message);
}
