using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.LogFiles;

/// <summary>MCP tool that filters large text files on the machine with the bundled ripgrep.</summary>
[McpServerToolType]
public sealed class SearchTools(VestigoOptions options, RootPolicy roots, RipgrepSearcher searcher)
{
    [McpServerTool(Name = "search_text")]
    [Description("Searches a text file, or every file under a directory, inside a root with Read access using the bundled ripgrep, and returns only the matching lines (plus optional context). Use it to filter large or numerous log and trace files on the machine instead of downloading or tailing them. The pattern is a Rust regular expression unless fixedStrings is true. Hidden files are searched, .gitignore rules are not applied, symbolic links and junctions are not followed, and binary files are skipped. UTF-8 and UTF-16 files with a byte order mark are decoded. Results are bounded by Vestigo:Logs (lines returned, context lines, line length, run time); truncated or timedOut is true when a bound stopped the search early, so narrow the pattern, path or globs and retry.")]
    public Task<OperationResult<SearchResult>> SearchText(
        [Description("Regular expression (or literal text with fixedStrings) to look for. A line matches when any part of it matches.")] string pattern,
        [Description("File or directory: root:relative\\path or an absolute path inside a root. A directory is searched recursively.")] string path,
        [Description("Treat the pattern as literal text instead of a regular expression.")] bool fixedStrings = false,
        [Description("Match case-insensitively. Default is case-sensitive.")] bool ignoreCase = false,
        [Description("Only match whole words.")] bool wholeWord = false,
        [Description("Lines of context to return before each match. Clamped to Vestigo:Logs:MaxSearchContextLines.")] int contextBefore = 0,
        [Description("Lines of context to return after each match. Clamped to Vestigo:Logs:MaxSearchContextLines.")] int contextAfter = 0,
        [Description("Stop searching each file after this many matching lines.")] int? maxMatchesPerFile = null,
        [Description("Optional file-name globs applied below a directory, for example *.log or !*.old.log. A leading ! excludes. Matched case-insensitively against the path.")] string[]? globs = null,
        CancellationToken cancellationToken = default)
    {
        PathCheck check = roots.ResolveExisting(path, RootAccess.Read);
        if (!check.Success)
        {
            return Task.FromResult(OperationResult<SearchResult>.Fail(check.ErrorCode!, check.Error!));
        }

        return searcher.SearchAsync(
            new SearchRequest(
                check.FullPath!,
                pattern,
                fixedStrings,
                ignoreCase,
                wholeWord,
                Math.Max(contextBefore, 0),
                Math.Max(contextAfter, 0),
                maxMatchesPerFile,
                globs ?? []),
            cancellationToken);
    }
}
