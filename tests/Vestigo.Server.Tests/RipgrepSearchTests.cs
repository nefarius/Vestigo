using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging.Abstractions;

using Vestigo.Build;
using Vestigo.Configuration;
using Vestigo.LogFiles;
using Vestigo.Modules;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

public class RipgrepPinTests
{
    [Fact]
    public void Pin_IsWellFormedAndMatchesServerDefault()
    {
        Assert.Matches("^[0-9a-f]{64}$", RipgrepPin.ArchiveSha256);
        Assert.Matches("^[0-9a-f]{64}$", RipgrepPin.ExecutableSha256);
        Assert.Equal(LogsOptions.DefaultRipgrepSha256, RipgrepPin.ExecutableSha256);
        Assert.Equal(RipgrepPin.ExecutableSha256, new LogsOptions().RipgrepSha256);
        Assert.True(Uri.TryCreate(RipgrepPin.ArchiveUrl, UriKind.Absolute, out Uri? url));
        Assert.Equal(Uri.UriSchemeHttps, url.Scheme);
        Assert.Equal("github.com", url.Host);
        Assert.Contains($"/{RipgrepPin.Version}/", RipgrepPin.ArchiveUrl, StringComparison.Ordinal);
        Assert.StartsWith($"ripgrep-{RipgrepPin.Version}-x86_64-pc-windows-msvc/", RipgrepPin.ArchiveEntryPath, StringComparison.Ordinal);
        Assert.Equal("rg.exe", RipgrepPin.Executable);
    }
}

public class RipgrepArgumentsTests
{
    private static SearchRequest Request(string pattern = "ERROR", string path = @"C:\logs", string[]? globs = null) =>
        new(path, pattern, false, false, false, 0, 0, null, globs ?? []);

    [Fact]
    public void Build_PutsPatternAfterDashEAndPathAfterDoubleDash()
    {
        OperationResult<IReadOnlyList<string>> result = RipgrepArguments.Build(
            Request("-evil --pre=calc", @"C:\logs\-weird.log", ["*.log", "!old*"]) with
            {
                FixedStrings = true,
                IgnoreCase = true,
                WholeWord = true,
                ContextBefore = 2,
                ContextAfter = 3,
                MaxMatchesPerFile = 5
            },
            new LogsOptions());

        Assert.True(result.Success);
        string[] arguments = [.. result.Value!];
        Assert.Contains("--no-config", arguments);
        Assert.Contains("--json", arguments);
        Assert.Contains("--no-follow", arguments);
        Assert.Contains("--fixed-strings", arguments);
        Assert.Contains("--ignore-case", arguments);
        Assert.Contains("--word-regexp", arguments);
        Assert.Contains("--before-context=2", arguments);
        Assert.Contains("--after-context=3", arguments);
        Assert.Contains("--max-count=5", arguments);
        Assert.Contains("--glob=*.log", arguments);
        Assert.Contains("--glob=!old*", arguments);
        Assert.Equal(["-e", "-evil --pre=calc", "--", @"C:\logs\-weird.log"], arguments[^4..]);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--pre", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ClampsContextToConfiguredLimit()
    {
        LogsOptions options = new() { MaxSearchContextLines = 4 };
        OperationResult<IReadOnlyList<string>> result = RipgrepArguments.Build(
            Request() with { ContextBefore = 99, ContextAfter = 99 },
            options);

        Assert.Contains("--before-context=4", result.Value!);
        Assert.Contains("--after-context=4", result.Value!);
    }

    [Fact]
    public void Build_RejectsUnsafeOrOversizedInput()
    {
        LogsOptions options = new();
        Assert.Equal("invalid_request", RipgrepArguments.Build(Request(""), options).ErrorCode);
        Assert.Equal("invalid_request", RipgrepArguments.Build(Request("a\nb"), options).ErrorCode);
        Assert.Equal("invalid_request", RipgrepArguments.Build(Request(new string('a', RipgrepArguments.MaxPatternLength + 1)), options).ErrorCode);
        Assert.Equal("invalid_request", RipgrepArguments.Build(Request(globs: [""]), options).ErrorCode);
        Assert.Equal("invalid_request", RipgrepArguments.Build(Request(globs: ["a\nb"]), options).ErrorCode);
        Assert.Equal("invalid_request", RipgrepArguments.Build(Request(globs: [.. Enumerable.Repeat("*.log", RipgrepArguments.MaxGlobs + 1)]), options).ErrorCode);
        Assert.Equal("invalid_request", RipgrepArguments.Build(Request() with { MaxMatchesPerFile = 0 }, options).ErrorCode);
    }
}

public class RipgrepOutputParserTests
{
    [Fact]
    public void TryParse_ReadsMatchAndContextWithTextOrBytes()
    {
        Assert.True(RipgrepOutputParser.TryParse(
            """{"type":"match","data":{"path":{"text":"C:\\a.log"},"lines":{"text":"ERROR one\r\n"},"line_number":2,"absolute_offset":6,"submatches":[{"match":{"text":"ERROR"},"start":0,"end":5}]}}""",
            out RipgrepEvent? match));
        Assert.Equal("match", match!.Kind);
        Assert.Equal(@"C:\a.log", match.Path);
        Assert.Equal(2, match.LineNumber);
        Assert.Equal("ERROR one", match.Text);
        Assert.Equal(0, match.FirstMatchByte);

        // "caf\xE9" is not UTF-8, so ripgrep sends base64 bytes.
        Assert.True(RipgrepOutputParser.TryParse(
            """{"type":"context","data":{"path":{"text":"x"},"lines":{"bytes":"Y2Fm6Q=="},"line_number":1,"submatches":[]}}""",
            out RipgrepEvent? context));
        Assert.Equal("context", context!.Kind);
        Assert.Equal(-1, context.FirstMatchByte);
        Assert.StartsWith("caf", context.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_ReadsSummaryAndIgnoresEverythingElse()
    {
        Assert.True(RipgrepOutputParser.TryParse(
            """{"type":"summary","data":{"stats":{"bytes_searched":1234}}}""",
            out RipgrepEvent? summary));
        Assert.Equal(1234, summary!.BytesSearched);

        Assert.False(RipgrepOutputParser.TryParse("""{"type":"begin","data":{"path":{"text":"x"}}}""", out _));
        Assert.False(RipgrepOutputParser.TryParse("not json", out _));
        Assert.False(RipgrepOutputParser.TryParse("{broken", out _));
        Assert.False(RipgrepOutputParser.TryParse("", out _));
    }
}

public class RipgrepLineReaderTests
{
    [Fact]
    public async Task Read_EmitsRecordsWithinTheCapIncludingCrlf()
    {
        await using MemoryStream stream = new("one\r\ntwo\nthree"u8.ToArray());

        RipgrepLine[] lines = await Read(stream, maxBytes: 16, isError: false);

        Assert.Equal(["one", "two", "three"], lines.Select(line => line.Text));
        Assert.All(lines, line => Assert.False(line.IsError));
    }

    [Fact]
    public async Task Read_SkipsOversizedRecordsAndKeepsTheNextOne()
    {
        await using MemoryStream stream = new("ok\nthis-record-is-too-long\nnext\n"u8.ToArray());

        RipgrepLine[] lines = await Read(stream, maxBytes: 8, isError: false);

        Assert.Equal(3, lines.Length);
        Assert.Equal("ok", lines[0].Text);
        Assert.False(lines[0].IsError);
        Assert.Equal(RipgrepLineReader.OversizedRecordWarning, lines[1].Text);
        Assert.True(lines[1].IsError);
        Assert.Equal("next", lines[2].Text);
        Assert.False(lines[2].IsError);
    }

    [Fact]
    public async Task Read_DiscardsAnOversizedRecordThatSpansChunksAndEof()
    {
        byte[] payload = "short\n"u8.ToArray().Concat(Enumerable.Repeat((byte)'x', 9000)).Concat("\nafter\n"u8.ToArray()).ToArray();
        await using MemoryStream stream = new(payload);

        RipgrepLine[] lines = await Read(stream, maxBytes: 16, isError: false);

        Assert.Equal(
            ["short", RipgrepLineReader.OversizedRecordWarning, "after"],
            lines.Select(line => line.Text));
        Assert.False(lines[0].IsError);
        Assert.True(lines[1].IsError);
        Assert.False(lines[2].IsError);
    }

    [Fact]
    public async Task Read_WarnsOncePerOversizedRecordAndForATrailingOverflow()
    {
        byte[] payload = "yyyyyyyyyy\nzzzzzzzzzz"u8.ToArray();
        await using MemoryStream stream = new(payload);

        RipgrepLine[] lines = await Read(stream, maxBytes: 4, isError: false);

        Assert.Equal(2, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.True(line.IsError);
            Assert.Equal(RipgrepLineReader.OversizedRecordWarning, line.Text);
        });
    }

    [Fact]
    public void RecordByteLimit_GrowsWithMaxSearchLineLength()
    {
        Assert.True(RipgrepLineReader.RecordByteLimit(1000) > 1000);
        Assert.True(RipgrepLineReader.RecordByteLimit(2000) > RipgrepLineReader.RecordByteLimit(1000));
    }

    private static async Task<RipgrepLine[]> Read(Stream stream, int maxBytes, bool isError)
    {
        List<RipgrepLine> lines = [];
        await foreach (RipgrepLine line in RipgrepLineReader.ReadAsync(stream, isError, maxBytes, CancellationToken.None))
        {
            lines.Add(line);
        }

        return [.. lines];
    }
}

public class RipgrepSearcherTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vestigo-rg-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string FakeExecutable()
    {
        string path = Path.Combine(_dir, "rg.exe");
        File.WriteAllText(path, "not really ripgrep");
        return path;
    }

    private RipgrepSearcher Searcher(FakeRipgrepProcess process, Action<LogsOptions>? configure = null)
    {
        VestigoOptions options = new();
        options.Logs.RipgrepPath = FakeExecutable();
        options.Logs.RipgrepSha256 = null;
        configure?.Invoke(options.Logs);
        return new RipgrepSearcher(options, process, NullLogger<RipgrepSearcher>.Instance);
    }

    private static SearchRequest Request() => new(@"C:\logs", "ERROR", false, false, false, 0, 0, null, []);

    private static string Match(int line, string text) =>
        $$$"""{"type":"match","data":{"path":{"text":"C:\\logs\\a.log"},"lines":{"text":"{{{text}}}\n"},"line_number":{{{line}}},"submatches":[{"match":{"text":"E"},"start":0,"end":1}]}}""";

    private const string Summary = """{"type":"summary","data":{"stats":{"bytes_searched":99}}}""";

    [Fact]
    public async Task Search_ReturnsMatchesAndSummary()
    {
        FakeRipgrepProcess process = new() { Lines = [Match(2, "ERROR one"), Match(4, "ERROR two"), Summary], ExitCode = 0 };

        OperationResult<SearchResult> result = await Searcher(process).SearchAsync(Request(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Value!.MatchCount);
        Assert.Equal(["ERROR one", "ERROR two"], result.Value.Lines.Select(line => line.Text));
        Assert.Equal(99, result.Value.BytesSearched);
        Assert.False(result.Value.Truncated);
        Assert.False(result.Value.TimedOut);
        Assert.Equal(@"C:\logs", process.Arguments![^1]);
    }

    [Fact]
    public async Task Search_NoMatchesIsSuccessWithEmptyResult()
    {
        FakeRipgrepProcess process = new() { Lines = [Summary], ExitCode = 1 };

        OperationResult<SearchResult> result = await Searcher(process).SearchAsync(Request(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Value!.Lines);
    }

    [Fact]
    public async Task Search_StopsAtResultLimitAndKillsProcess()
    {
        FakeRipgrepProcess process = new()
        {
            Lines = [Match(1, "ERROR a"), Match(2, "ERROR b"), Match(3, "ERROR c"), Match(4, "ERROR d")],
            WaitUntilCancelled = true
        };

        OperationResult<SearchResult> result = await Searcher(process, logs => logs.MaxSearchResults = 2)
            .SearchAsync(Request(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Value!.Truncated);
        Assert.Equal(2, result.Value.Lines.Count);
        Assert.True(process.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Search_ReturnsPartialResultsWhenTimeLimitIsReached()
    {
        FakeRipgrepProcess process = new() { Lines = [Match(1, "ERROR a")], WaitUntilCancelled = true };

        OperationResult<SearchResult> result = await Searcher(process, logs => logs.MaxSearchDuration = TimeSpan.FromMilliseconds(200))
            .SearchAsync(Request(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Value!.TimedOut);
        Assert.False(result.Value.Truncated);
        Assert.Single(result.Value.Lines);
    }

    [Fact]
    public async Task Search_PropagatesCallerCancellation()
    {
        FakeRipgrepProcess process = new() { WaitUntilCancelled = true };
        using CancellationTokenSource source = new(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Searcher(process).SearchAsync(Request(), source.Token));
    }

    [Fact]
    public async Task Search_MapsRegexErrorsAndOtherFailures()
    {
        FakeRipgrepProcess regex = new()
        {
            ErrorLines = ["rg: regex parse error:", "    (?:()", "error: unclosed group"],
            ExitCode = 2
        };
        OperationResult<SearchResult> invalid = await Searcher(regex).SearchAsync(Request(), CancellationToken.None);
        Assert.Equal("invalid_pattern", invalid.ErrorCode);

        FakeRipgrepProcess other = new() { ErrorLines = ["rg: C:\\logs: Access is denied."], ExitCode = 2 };
        OperationResult<SearchResult> failed = await Searcher(other).SearchAsync(Request(), CancellationToken.None);
        Assert.Equal("search_failed", failed.ErrorCode);
    }

    [Fact]
    public async Task Search_PartialErrorsWithMatchesBecomeWarnings()
    {
        FakeRipgrepProcess process = new()
        {
            Lines = [Match(1, "ERROR a")],
            ErrorLines = ["rg: C:\\logs\\locked.log: Access is denied."],
            ExitCode = 2
        };

        OperationResult<SearchResult> result = await Searcher(process).SearchAsync(Request(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Value!.Warnings);
    }

    [Fact]
    public async Task Search_RejectsMissingExecutableAndHashMismatch()
    {
        FakeRipgrepProcess process = new();
        VestigoOptions missing = new();
        missing.Logs.RipgrepPath = Path.Combine(_dir, "absent.exe");
        RipgrepSearcher none = new(missing, process, NullLogger<RipgrepSearcher>.Instance);
        Assert.Equal("executable_missing", (await none.SearchAsync(Request(), CancellationToken.None)).ErrorCode);

        OperationResult<SearchResult> mismatch = await Searcher(process, logs => logs.RipgrepSha256 = new string('a', 64))
            .SearchAsync(Request(), CancellationToken.None);
        Assert.Equal("hash_mismatch", mismatch.ErrorCode);
        Assert.Null(process.Arguments);
    }

    [Fact]
    public async Task Search_RejectsWhenAllSlotsAreBusy()
    {
        FakeRipgrepProcess busy = new() { WaitUntilCancelled = true };
        RipgrepSearcher searcher = Searcher(busy, logs => logs.MaxConcurrentSearches = 1);
        searcher.SlotWait = TimeSpan.FromMilliseconds(100);

        Task<OperationResult<SearchResult>> first = searcher.SearchAsync(Request(), CancellationToken.None);
        await busy.Started.Task;
        OperationResult<SearchResult> second = await searcher.SearchAsync(Request(), CancellationToken.None);

        Assert.Equal("search_busy", second.ErrorCode);
        Assert.False(first.IsCompleted);
        busy.Release();
        Assert.True((await first).Success);
    }

    [Fact]
    public void Shorten_KeepsWindowAroundFirstMatchWithoutSplittingSurrogates()
    {
        string text = new string('a', 500) + "NEEDLE" + string.Concat(Enumerable.Repeat("\U0001F600", 400));
        (string shortened, bool cut) = RipgrepSearcher.Shorten(text, 500, 100);

        Assert.True(cut);
        Assert.Contains("NEEDLE", shortened, StringComparison.Ordinal);
        Assert.True(shortened.Length <= 100);
        Assert.False(char.IsLowSurrogate(shortened[0]));
        Assert.False(char.IsHighSurrogate(shortened[^1]));

        Assert.Equal(("short", false), RipgrepSearcher.Shorten("short", 0, 100));
    }
}

/// <summary>
///     Runs the real rg.exe. Skipped unless <c>VESTIGO_TEST_RIPGREP</c> points at one; CI does not download it here
///     because the build job already verifies the pinned digest.
/// </summary>
public class RipgrepIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vestigo-rg-int-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string? Executable => Environment.GetEnvironmentVariable("VESTIGO_TEST_RIPGREP");

    private SearchTools Tools(Action<LogsOptions>? configure = null)
    {
        VestigoOptions options = new() { Roots = [new RootDefinition { Name = "logs", Path = _root, Access = RootAccess.Read }] };
        options.Logs.RipgrepPath = Executable;
        options.Logs.RipgrepSha256 = null;
        configure?.Invoke(options.Logs);
        return new SearchTools(
            options,
            new RootPolicy(options),
            new RipgrepSearcher(options, new RipgrepProcess(options), NullLogger<RipgrepSearcher>.Instance));
    }

    [Fact]
    public async Task SearchText_FindsMatchesInUtf8AndUtf16FilesWithContext()
    {
        if (string.IsNullOrEmpty(Executable))
        {
            return;
        }

        File.WriteAllText(Path.Combine(_root, "a.log"), "alpha\nERROR one\nbeta\nERROR two\n");
        File.WriteAllText(Path.Combine(_root, "wide.log"), "x\r\nERROR wide\r\n", System.Text.Encoding.Unicode);
        File.WriteAllText(Path.Combine(_root, "skip.txt"), "ERROR text");

        OperationResult<SearchResult> result = await Tools().SearchText(
            "error", "logs:.", ignoreCase: true, contextAfter: 1, globs: ["*.log"]);

        Assert.True(result.Success, result.Error);
        Assert.Equal(3, result.Value!.MatchCount);
        Assert.Contains(result.Value.Lines, line => !line.IsMatch && line.Text == "beta");
        Assert.Contains(result.Value.Lines, line => line.IsMatch && line.Text == "ERROR wide");
        Assert.DoesNotContain(result.Value.Lines, line => line.Path.EndsWith("skip.txt", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(result.Value.BytesSearched);
    }

    [Fact]
    public async Task SearchText_StopsAtLimitAndReportsBadPatterns()
    {
        if (string.IsNullOrEmpty(Executable))
        {
            return;
        }

        File.WriteAllText(Path.Combine(_root, "big.log"), string.Concat(Enumerable.Repeat("ERROR line\n", 50_000)));

        OperationResult<SearchResult> limited = await Tools(logs => logs.MaxSearchResults = 10)
            .SearchText("ERROR", "logs:big.log");
        Assert.True(limited.Success, limited.Error);
        Assert.True(limited.Value!.Truncated);
        Assert.Equal(10, limited.Value.Lines.Count);

        OperationResult<SearchResult> bad = await Tools().SearchText("(", "logs:big.log");
        Assert.Equal("invalid_pattern", bad.ErrorCode);

        OperationResult<SearchResult> none = await Tools().SearchText("nomatch-zzz", "logs:big.log");
        Assert.True(none.Success);
        Assert.Empty(none.Value!.Lines);
    }

    [Fact]
    public async Task SearchText_SkipsAnOversizedJsonRecordAndKeepsALaterMatch()
    {
        if (string.IsNullOrEmpty(Executable))
        {
            return;
        }

        File.WriteAllText(Path.Combine(_root, "huge.log"), "ERROR " + new string('x', 200_000) + "\nERROR short\n");

        OperationResult<SearchResult> result = await Tools(logs => logs.MaxSearchLineLength = 80)
            .SearchText("ERROR", "logs:huge.log");

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Value!.Lines, line => line.IsMatch && line.Text == "ERROR short");
        Assert.DoesNotContain(result.Value.Lines, line => line.Text.Contains('x', StringComparison.Ordinal));
        Assert.Contains(result.Value.Warnings, warning => warning == RipgrepLineReader.OversizedRecordWarning);
    }
}

public class SearchToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vestigo-search-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("vestigo-search-out-").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private SearchTools Tools(FakeRipgrepProcess process, RootAccess access = RootAccess.Read)
    {
        VestigoOptions options = new() { Roots = [new RootDefinition { Name = "logs", Path = _root, Access = access }] };
        options.Logs.RipgrepPath = Path.Combine(_root, "rg.exe");
        options.Logs.RipgrepSha256 = null;
        File.WriteAllText(options.Logs.RipgrepPath, "fake");
        return new SearchTools(
            options,
            new RootPolicy(options),
            new RipgrepSearcher(options, process, NullLogger<RipgrepSearcher>.Instance));
    }

    [Fact]
    public async Task SearchText_ResolvesNamedRootAndPassesResolvedPathLast()
    {
        File.WriteAllText(Path.Combine(_root, "a.log"), "ERROR");
        FakeRipgrepProcess process = new() { ExitCode = 1 };

        OperationResult<SearchResult> result = await Tools(process).SearchText("ERROR", "logs:a.log");

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(_root, "a.log"), process.Arguments![^1], ignoreCase: true);
    }

    [Fact]
    public async Task SearchText_RejectsPathsOutsideRootsMissingPathsAndRootsWithoutRead()
    {
        FakeRipgrepProcess process = new();
        SearchTools tools = Tools(process);

        Assert.Equal("outside_root", (await tools.SearchText("x", _outside)).ErrorCode);
        Assert.Equal("outside_root", (await tools.SearchText("x", @"logs:..\" + Path.GetFileName(_outside))).ErrorCode);
        Assert.Equal("not_found", (await tools.SearchText("x", "logs:missing.log")).ErrorCode);
        Assert.Equal("access_denied", (await Tools(process, RootAccess.Write).SearchText("x", "logs:a.log")).ErrorCode);
        Assert.Null(process.Arguments);
    }

    [Fact]
    public void LogsModule_ExposesSearchToolAndRegistersServices()
    {
        LogsModule module = new();
        Assert.Contains(typeof(SearchTools), module.ToolTypes);
        Assert.True(module.EnabledByDefault);
    }
}

internal sealed class FakeRipgrepProcess : IRipgrepProcess
{
    public List<string> Lines { get; set; } = [];

    public List<string> ErrorLines { get; set; } = [];

    public int ExitCode { get; set; }

    /// <summary>When true the process keeps running after its lines until it is cancelled, like a long search.</summary>
    public bool WaitUntilCancelled { get; set; }

    public IReadOnlyList<string>? Arguments { get; private set; }

    /// <summary>The token the searcher passed, so tests can see that it was cancelled to kill the process.</summary>
    public CancellationToken Token { get; private set; }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Lets a process that is waiting for cancellation finish normally.</summary>
    public void Release() => _release.TrySetResult();

    public async IAsyncEnumerable<RipgrepLine> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<int>? onExited,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Arguments = arguments;
        Token = cancellationToken;
        Started.TrySetResult();
        foreach (string error in ErrorLines)
        {
            yield return new RipgrepLine(error, true);
        }

        foreach (string line in Lines)
        {
            yield return new RipgrepLine(line, false);
        }

        if (WaitUntilCancelled)
        {
            await _release.Task.WaitAsync(cancellationToken);
        }

        onExited?.Invoke(ExitCode);
    }
}
