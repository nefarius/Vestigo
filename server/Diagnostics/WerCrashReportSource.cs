namespace Vestigo.Diagnostics;

/// <summary>One fixed WER store. <see cref="Profile" /> is an account label, not a directory.</summary>
internal sealed record WerStore(string Scope, string Store, string Directory, string? Profile);

/// <summary>
///     Lists ReportArchive and ReportQueue metadata. It reads <c>Report.wer</c> only and records dump file names and lengths.
/// </summary>
internal sealed class WerCrashReportSource : ICrashReportSource
{
    public const int MaxFolders = 2000;
    private readonly Func<IReadOnlyList<WerStore>> _stores;
    private readonly DiagnosticsOptions _options;

    public WerCrashReportSource(Vestigo.Configuration.VestigoOptions options)
        : this(WindowsWerLocations.Discover, options.Diagnostics)
    {
    }

    internal WerCrashReportSource(Func<IReadOnlyList<WerStore>> stores, DiagnosticsOptions options)
    {
        _stores = stores;
        _options = options;
    }

    public OperationResult<CrashReportList> List(string scope, int limit, DateTimeOffset? since)
    {
        List<CrashReport> matches = [];
        bool truncated = false;
        bool partial = false;
        int skipped = 0;
        int visited = 0;
        byte[] buffer = new byte[(int)Math.Min(_options.MaxCrashReportFileBytes, int.MaxValue)];
        foreach (WerStore store in _stores())
        {
            if (!store.Scope.Equals(scope, StringComparison.OrdinalIgnoreCase) && !scope.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Directory.Exists(store.Directory))
            {
                continue;
            }

            IEnumerable<string> folders;
            try
            {
                folders = Directory.EnumerateDirectories(store.Directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                partial = true;
                skipped++;
                continue;
            }

            foreach (string folder in folders)
            {
                if (visited >= MaxFolders)
                {
                    truncated = true;
                    break;
                }

                visited++;
                if (IsReparse(folder))
                {
                    continue;
                }

                try
                {
                    if (!TryRead(store, folder, buffer, since, out CrashReport? report, out bool unreadable))
                    {
                        if (unreadable)
                        {
                            partial = true;
                            skipped++;
                        }

                        continue;
                    }

                    matches.Add(report!);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    partial = true;
                    skipped++;
                }
            }

            if (truncated)
            {
                break;
            }
        }

        List<CrashReport> ordered = matches
            .OrderByDescending(report => report.EventTime ?? DateTimeOffset.MinValue)
            .ThenBy(report => report.Folder, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<CrashReport> page = ordered.Take(limit).ToList();
        return OperationResult<CrashReportList>.Ok(new CrashReportList(
            ordered.Count,
            page.Count,
            truncated || ordered.Count > page.Count,
            partial,
            skipped,
            page));
    }

    private static bool TryRead(WerStore store, string folder, byte[] buffer, DateTimeOffset? since, out CrashReport? report, out bool unreadable)
    {
        report = null;
        unreadable = false;
        string wer = Path.Combine(folder, "Report.wer");
        if (Directory.Exists(wer))
        {
            unreadable = true;
            return false;
        }

        if (!File.Exists(wer) || IsReparse(wer))
        {
            return false;
        }

        int read;
        bool fileTruncated;
        try
        {
            using FileStream stream = new(wer, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            read = stream.Read(buffer, 0, buffer.Length);
            fileTruncated = stream.ReadByte() >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable = true;
            return false;
        }

        ParsedWerReport parsed = WerReportParser.Parse(buffer.AsSpan(0, read), fileTruncated);
        if (since is { } earliest && (parsed.EventTime is null || parsed.EventTime < earliest))
        {
            return false;
        }

        report = new CrashReport(
            store.Scope,
            store.Store,
            store.Profile,
            Cap(Path.GetFileName(folder), 240),
            parsed.EventType,
            parsed.EventTime,
            parsed.AppName,
            parsed.AppPath,
            parsed.ReportId,
            parsed.Truncated,
            parsed.Signatures,
            DumpFiles(folder));
        return true;
    }

    private static IReadOnlyList<CrashDumpFile> DumpFiles(string folder)
    {
        List<CrashDumpFile> files = [];
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFiles(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return files;
        }

        foreach (string entry in entries)
        {
            if (files.Count == 8 || IsReparse(entry))
            {
                continue;
            }

            string extension = Path.GetExtension(entry);
            if (!extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".mdmp", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".hdmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long length;
            try
            {
                length = new FileInfo(entry).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            files.Add(new CrashDumpFile(Cap(Path.GetFileName(entry), 240), length));
        }

        return files;
    }

    private static bool IsReparse(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];
}
