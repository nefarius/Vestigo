using System.ComponentModel;

using Microsoft.Win32.SafeHandles;

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

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    ReadWindows(store, buffer, since, matches, ref truncated, ref partial, ref skipped, ref visited);
                }
                else
                {
                    ReadPortable(store, buffer, since, matches, ref truncated, ref partial, ref skipped, ref visited);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                partial = true;
                skipped++;
                continue;
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

    private static void ReadWindows(
        WerStore store,
        byte[] buffer,
        DateTimeOffset? since,
        List<CrashReport> matches,
        ref bool truncated,
        ref bool partial,
        ref int skipped,
        ref int visited)
    {
        switch (WerDirectory.Open(store.Directory, out WerDirectory.OpenedDirectory opened))
        {
            case WerDirectory.OpenStatus.Missing:
                return;
            case WerDirectory.OpenStatus.Failed or WerDirectory.OpenStatus.Reparse:
                partial = true;
                skipped++;
                return;
        }

        using (opened)
        {
            if (!WerDirectory.TryResolve(store.Directory, out string resolved)
                || !WerDirectory.SamePath(opened.FinalPath, resolved))
            {
                partial = true;
                skipped++;
                return;
            }

            foreach (WerDirectory.Entry folder in WerDirectory.Children(opened.Handle))
            {
                if (!folder.Directory)
                {
                    continue;
                }

                if (visited >= MaxFolders)
                {
                    truncated = true;
                    return;
                }

                visited++;
                if (folder.Reparse)
                {
                    continue;
                }

                try
                {
                    using SafeFileHandle handle = WerDirectory.OpenChild(opened.Handle, folder.FileId);
                    if (WerDirectory.IsReparse(handle))
                    {
                        continue;
                    }

                    if (!WerDirectory.TryFinalPath(handle, out string? final) || final is null || !WerDirectory.IsUnder(final, opened.FinalPath))
                    {
                        partial = true;
                        skipped++;
                        continue;
                    }

                    if (!TryRead(store, handle, final, folder.Name, buffer, since, out CrashReport? report, out bool unreadable))
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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    partial = true;
                    skipped++;
                }
            }
        }
    }

    private static void ReadPortable(
        WerStore store,
        byte[] buffer,
        DateTimeOffset? since,
        List<CrashReport> matches,
        ref bool truncated,
        ref bool partial,
        ref int skipped,
        ref int visited)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(store.Directory);
            if (attributes.HasFlag(FileAttributes.ReparsePoint) || !attributes.HasFlag(FileAttributes.Directory))
            {
                partial = true;
                skipped++;
                return;
            }
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            partial = true;
            skipped++;
            return;
        }

        try
        {
            foreach (string folder in Directory.EnumerateDirectories(store.Directory))
            {
                if (visited >= MaxFolders)
                {
                    truncated = true;
                    return;
                }

                visited++;
                if (!TryReparse(folder, out bool reparse))
                {
                    partial = true;
                    skipped++;
                    continue;
                }

                if (reparse)
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            partial = true;
            skipped++;
        }
    }

    private static bool TryRead(
        WerStore store,
        SafeFileHandle folder,
        string folderFinal,
        string folderName,
        byte[] buffer,
        DateTimeOffset? since,
        out CrashReport? report,
        out bool unreadable)
    {
        report = null;
        unreadable = false;
        WerDirectory.Entry? reportEntry = null;
        List<CrashDumpFile> dumps = [];
        foreach (WerDirectory.Entry entry in WerDirectory.Children(folder))
        {
            if (entry.Name.Equals("Report.wer", StringComparison.OrdinalIgnoreCase))
            {
                reportEntry = entry;
                continue;
            }

            if (entry.Directory || entry.Reparse || dumps.Count == 8 || entry.EndOfFile < 0 || !IsDumpName(entry.Name))
            {
                continue;
            }

            dumps.Add(new CrashDumpFile(Cap(entry.Name, 240), entry.EndOfFile));
        }

        if (reportEntry is not { } wer)
        {
            return false;
        }

        if (wer.Directory)
        {
            unreadable = true;
            return false;
        }

        if (wer.Reparse)
        {
            return false;
        }

        try
        {
            using SafeFileHandle file = WerDirectory.OpenChild(folder, wer.FileId);
            if (WerDirectory.IsReparse(file))
            {
                return false;
            }

            if (!WerDirectory.TryFinalPath(file, out string? final) || final is null || !WerDirectory.IsUnder(final, folderFinal))
            {
                unreadable = true;
                return false;
            }

            using Stream stream = WerDirectory.Read(file);
            (int read, bool fileTruncated) = ReadBounded(stream, buffer);
            return Finish(store, folderName, buffer, read, fileTruncated, dumps, since, out report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            unreadable = true;
            return false;
        }
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
            (read, fileTruncated) = ReadBounded(stream, buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable = true;
            return false;
        }

        return Finish(store, Path.GetFileName(folder), buffer, read, fileTruncated, DumpFiles(folder), since, out report);
    }

    private static bool Finish(
        WerStore store,
        string folderName,
        byte[] buffer,
        int read,
        bool fileTruncated,
        IReadOnlyList<CrashDumpFile> dumps,
        DateTimeOffset? since,
        out CrashReport? report)
    {
        report = null;
        ParsedWerReport parsed = WerReportParser.Parse(buffer.AsSpan(0, read), fileTruncated);
        if (since is { } earliest && (parsed.EventTime is null || parsed.EventTime < earliest))
        {
            return false;
        }

        report = new CrashReport(
            store.Scope,
            store.Store,
            store.Profile,
            Cap(folderName, 240),
            parsed.EventType,
            parsed.EventTime,
            parsed.AppName,
            parsed.AppPath,
            parsed.ReportId,
            parsed.Truncated,
            parsed.Signatures,
            dumps);
        return true;
    }

    private static bool IsDumpName(string name)
    {
        string extension = Path.GetExtension(name);
        return extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mdmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".hdmp", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<CrashDumpFile> DumpFiles(string folder)
    {
        List<CrashDumpFile> files = [];
        try
        {
            foreach (string entry in Directory.EnumerateFiles(folder))
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return files;
        }

        return files;
    }

    /// <summary>Fills <paramref name="buffer" /> until it is full or the stream ends. <c>Truncated</c> means another byte remained.</summary>
    internal static (int Read, bool Truncated) ReadBounded(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0)
            {
                return (read, false);
            }

            read += count;
        }

        return (read, stream.ReadByte() >= 0);
    }

    private static bool IsReparse(string path) => !TryReparse(path, out bool reparse) || reparse;

    /// <summary>False when the attributes cannot be read. A true result distinguishes a confirmed reparse point.</summary>
    private static bool TryReparse(string path, out bool reparse)
    {
        try
        {
            reparse = File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            reparse = false;
            return false;
        }
    }

    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];
}
