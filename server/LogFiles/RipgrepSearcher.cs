using System.Security.Cryptography;
using System.Text;

using Vestigo.Configuration;

namespace Vestigo.LogFiles;

/// <summary>One line returned by a search: a match or, when context was requested, a neighbouring line.</summary>
/// <param name="Path">Absolute path of the file that contains the line.</param>
/// <param name="Line">One-based line number.</param>
/// <param name="Text">Line text, cut to a window around the first match when it exceeds the configured length.</param>
/// <param name="IsMatch">False for a context line.</param>
/// <param name="TextTruncated">True when <paramref name="Text" /> was cut.</param>
public sealed record SearchLine(string Path, long Line, string Text, bool IsMatch, bool TextTruncated);

/// <summary>Result of one search. Counts cover only what ripgrep produced before the search ended or was stopped.</summary>
/// <param name="Lines">Matches and context lines in the order ripgrep emitted them.</param>
/// <param name="MatchCount">Number of matching lines in <paramref name="Lines" />.</param>
/// <param name="Truncated">True when the result limit was reached and the search was stopped.</param>
/// <param name="TimedOut">True when the time limit was reached and the search was stopped.</param>
/// <param name="BytesSearched">Bytes ripgrep searched, known only when it finished on its own.</param>
/// <param name="ElapsedMilliseconds">Wall-clock time of the search.</param>
/// <param name="Warnings">First few ripgrep stderr messages, such as files it could not read.</param>
public sealed record SearchResult(
    IReadOnlyList<SearchLine> Lines,
    int MatchCount,
    bool Truncated,
    bool TimedOut,
    long? BytesSearched,
    long ElapsedMilliseconds,
    IReadOnlyList<string> Warnings);

/// <summary>
///     Runs the bundled ripgrep against a path that the caller has already confined to a configured root and
///     bounds the work: concurrency, wall-clock time and the number of lines returned.
/// </summary>
public sealed class RipgrepSearcher(VestigoOptions options, IRipgrepProcess process, ILogger<RipgrepSearcher> logger)
    : IDisposable
{
    private const int MaxWarnings = 5;

    private readonly SemaphoreSlim _slots = new(options.Logs.MaxConcurrentSearches, options.Logs.MaxConcurrentSearches);

    /// <summary>How long a call waits for a free slot before failing with <c>search_busy</c>.</summary>
    internal TimeSpan SlotWait { get; set; } = TimeSpan.FromSeconds(5);

    public void Dispose() => _slots.Dispose();

    public async Task<OperationResult<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        LogsOptions logs = options.Logs;
        OperationResult<IReadOnlyList<string>> arguments = RipgrepArguments.Build(request, logs);
        if (!arguments.Success)
        {
            return OperationResult<SearchResult>.Fail(arguments.ErrorCode!, arguments.Error!);
        }

        string executable = ResolveExecutablePath();
        if (!File.Exists(executable))
        {
            return OperationResult<SearchResult>.Fail(
                "executable_missing",
                $"ripgrep was not found at '{executable}'. Reinstall Vestigo or set Vestigo:Logs:RipgrepPath.");
        }

        if (!TryVerifyHash(executable, out string? hashError))
        {
            return OperationResult<SearchResult>.Fail("hash_mismatch", hashError!);
        }

        if (!await _slots.WaitAsync(SlotWait, cancellationToken).ConfigureAwait(false))
        {
            return OperationResult<SearchResult>.Fail(
                "search_busy",
                $"{logs.MaxConcurrentSearches} searches are already running. Try again shortly.");
        }

        try
        {
            return await RunAsync(executable, arguments.Value!, logs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }

    internal string ResolveExecutablePath() =>
        string.IsNullOrWhiteSpace(options.Logs.RipgrepPath)
            ? Path.Combine(AppContext.BaseDirectory, @"tools\rg.exe")
            : options.Logs.RipgrepPath;

    internal bool TryVerifyHash(string executable, out string? error)
    {
        error = null;
        string? expected = options.Logs.RipgrepSha256;
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        try
        {
            using FileStream stream = File.OpenRead(executable);
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            error = "ripgrep SHA-256 does not match Vestigo:Logs:RipgrepSha256.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        return false;
    }

    private async Task<OperationResult<SearchResult>> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        LogsOptions logs,
        CancellationToken cancellationToken)
    {
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        using CancellationTokenSource timeout = new(logs.MaxSearchDuration);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        List<SearchLine> lines = [];
        List<string> warnings = [];
        List<string> errorLines = [];
        int matches = 0;
        long? bytesSearched = null;
        bool truncated = false;
        int exitCode = -1;
        bool exited = false;

        try
        {
            await foreach (RipgrepLine line in process.RunAsync(
                               executable,
                               arguments,
                               code =>
                               {
                                   exitCode = code;
                                   exited = true;
                               },
                               stop.Token).ConfigureAwait(false))
            {
                if (line.IsError)
                {
                    if (!string.IsNullOrWhiteSpace(line.Text))
                    {
                        string message = line.Text.Trim();
                        if (errorLines.Count < MaxWarnings)
                        {
                            errorLines.Add(message);
                        }

                        if (warnings.Count < MaxWarnings)
                        {
                            warnings.Add(message);
                        }
                    }

                    continue;
                }

                if (!RipgrepOutputParser.TryParse(line.Text, out RipgrepEvent? parsed) || parsed is null)
                {
                    continue;
                }

                if (parsed.Kind == "summary")
                {
                    bytesSearched = parsed.BytesSearched;
                    continue;
                }

                bool isMatch = parsed.Kind == "match";
                (string text, bool cut) = Shorten(parsed.Text ?? string.Empty, parsed.FirstMatchByte, logs.MaxSearchLineLength);
                lines.Add(new SearchLine(parsed.Path ?? string.Empty, parsed.LineNumber, text, isMatch, cut));
                if (isMatch)
                {
                    matches++;
                }

                if (lines.Count >= logs.MaxSearchResults)
                {
                    truncated = true;
                    stop.Cancel();
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Our own stop (limit or timeout) or the caller leaving; handled below.
        }

        cancellationToken.ThrowIfCancellationRequested();
        bool timedOut = timeout.IsCancellationRequested && !truncated;
        clock.Stop();

        if (!truncated && !timedOut && exited && exitCode >= 2 && lines.Count == 0)
        {
            string message = errorLines.Count > 0 ? string.Join(" ", errorLines.Take(MaxWarnings)) : "ripgrep failed.";
            logger.LogWarning("ripgrep exited with code {ExitCode}", exitCode);
            return OperationResult<SearchResult>.Fail(
                message.Contains("regex parse error", StringComparison.OrdinalIgnoreCase) ? "invalid_pattern" : "search_failed",
                message);
        }

        return OperationResult<SearchResult>.Ok(new SearchResult(
            lines,
            matches,
            truncated,
            timedOut,
            bytesSearched,
            clock.ElapsedMilliseconds,
            warnings));
    }

    /// <summary>Cuts an over-long line to a window that still contains the first match.</summary>
    internal static (string Text, bool Truncated) Shorten(string text, int firstMatchByte, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return (text, false);
        }

        int start = 0;
        if (firstMatchByte > 0)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            int clamped = Math.Min(firstMatchByte, bytes.Length);
            int matchIndex = Encoding.UTF8.GetString(bytes, 0, clamped).Length;
            start = Math.Clamp(matchIndex - (maxLength / 4), 0, text.Length - maxLength);
        }

        string window = text.Substring(start, maxLength);
        if (start > 0 && char.IsLowSurrogate(window[0]))
        {
            window = window[1..];
        }

        if (window.Length > 0 && char.IsHighSurrogate(window[^1]))
        {
            window = window[..^1];
        }

        return (window, true);
    }
}
