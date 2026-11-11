using Microsoft.Win32;

namespace Vestigo.Diagnostics;

/// <summary>
///     Lists <c>MEMORY.DMP</c>, the dedicated dump file, and <c>Minidump\*.dmp</c> in file-name order.
///     Reparse points are skipped. Dump bytes and bugcheck fields are not returned.
/// </summary>
internal sealed class WindowsKernelDumpInventory(ICrashReportSource reports) : IKernelDumpInventory
{
    public const int MaxDumps = 50;
    private const string CrashControl = @"SYSTEM\CurrentControlSet\Control\CrashControl";

    public OperationResult<KernelDumpList> List(int limit)
    {
        int bounded = Math.Clamp(limit, 1, MaxDumps);
        int? enabled = null;
        string? enabledNote = null;
        string? dumpFile = null;
        string? minidumpDir = null;
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(CrashControl, writable: false);
            if (key?.GetValue("CrashDumpEnabled") is int mode)
            {
                enabled = mode;
            }
            else if (key is null)
            {
                enabledNote = "not_found";
            }

            dumpFile = key?.GetValue("DumpFile") as string;
            minidumpDir = key?.GetValue("MinidumpDir") as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            enabledNote = "unavailable";
        }

        string windows = Environment.GetEnvironmentVariable("SystemRoot") ?? Environment.SystemDirectory;
        string memory = Path.Combine(windows, "MEMORY.DMP");
        List<Discovered> found = [];
        AddFile(found, "memory", memory);
        string? dedicated = Expand(dumpFile);
        if (dedicated is not null && !string.Equals(dedicated, memory, StringComparison.OrdinalIgnoreCase))
        {
            AddFile(found, "dedicated", dedicated);
        }

        string? miniDirectory = Expand(minidumpDir) ?? Path.Combine(windows, "Minidump");
        bool directoryCapped = false;
        try
        {
            if (Directory.Exists(miniDirectory))
            {
                using IEnumerator<string> files = Directory.EnumerateFiles(miniDirectory, "*.dmp")
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .GetEnumerator();
                while (files.MoveNext())
                {
                    string path = files.Current;
                    string name = Path.GetFileName(path);
                    if (name.Contains("..", StringComparison.Ordinal) || Path.GetFileName(name) != name)
                    {
                        continue;
                    }

                    AddFile(found, "minidump/" + name, path);
                    if (found.Count >= MaxDumps)
                    {
                        directoryCapped = files.MoveNext();
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            enabledNote ??= "minidump_unavailable";
        }

        bool truncated = found.Count > bounded || directoryCapped;
        Discovered[] page = found.Take(bounded).ToArray();
        Dictionary<string, List<KernelDumpWerMatch>> matches = MatchWer(page, out string? werNote);
        List<KernelDump> dumps = [];
        foreach (Discovered item in page)
        {
            dumps.Add(Describe(item, matches));
        }

        return OperationResult<KernelDumpList>.Ok(new KernelDumpList(
            enabled,
            enabled is int modeValue ? KernelDumpHeaderReader.CrashDumpEnabledName(modeValue) : null,
            enabledNote,
            werNote,
            dumps.Count,
            truncated,
            dumps));
    }

    private static void AddFile(List<Discovered> found, string id, string path)
    {
        if (found.Count >= MaxDumps || found.Exists(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) != 0)
            {
                return;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            found.Add(new Discovered(id, path, null, null, null, "unavailable"));
            return;
        }

        long? length = null;
        DateTimeOffset? written = null;
        byte[]? prefix = null;
        string? note = null;
        try
        {
            FileInfo info = new(path);
            length = info.Length;
            written = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            prefix = new byte[Math.Min(KernelDumpHeaderReader.PrefixBytes, (int)Math.Min(info.Length, int.MaxValue))];
            stream.ReadExactly(prefix);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            prefix = null;
            note = "unavailable";
        }

        found.Add(new Discovered(id, path, length, written, prefix, note));
    }

    private Dictionary<string, List<KernelDumpWerMatch>> MatchWer(IReadOnlyList<Discovered> dumps, out string? note)
    {
        Dictionary<string, List<KernelDumpWerMatch>> matches = new(StringComparer.OrdinalIgnoreCase);
        OperationResult<CrashReportList> listed = reports.List("all", 50, null);
        if (!listed.Success)
        {
            note = listed.ErrorCode;
            return matches;
        }

        note = listed.Value?.Partial == true || listed.Value?.Truncated == true ? "truncated" : null;
        foreach (CrashReport report in listed.Value?.Reports ?? [])
        {
            foreach (CrashDumpFile file in report.DumpFiles)
            {
                if (!dumps.Any(dump => string.Equals(Path.GetFileName(dump.Path), file.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!matches.TryGetValue(file.Name, out List<KernelDumpWerMatch>? rows))
                {
                    rows = [];
                    matches[file.Name] = rows;
                }

                if (rows.Count < 8)
                {
                    rows.Add(new KernelDumpWerMatch(report.Store, report.Folder, report.ReportId));
                }
            }
        }

        return matches;
    }

    private static KernelDump Describe(Discovered item, Dictionary<string, List<KernelDumpWerMatch>> matches)
    {
        KernelDumpHeader? header = item.Prefix is null ? null : KernelDumpHeaderReader.Read(item.Prefix);
        if (header is not null && item.Note is not null)
        {
            header = header with { Note = item.Note };
        }
        else if (item.Note is not null)
        {
            header = new KernelDumpHeader(null, null, null, null, null, null, null, item.Note);
        }

        string name = Path.GetFileName(item.Path);
        matches.TryGetValue(name, out List<KernelDumpWerMatch>? rows);
        return new KernelDump(
            item.Id,
            item.Id == "memory" ? "memory" : item.Id == "dedicated" ? "dedicated" : "minidump",
            name,
            item.Bytes,
            item.LastWriteTimeUtc,
            KernelDumpHeaderReader.FileKind(header),
            header,
            rows ?? []);
    }

    private static string? Expand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(value.Trim());
            return Path.IsPathRooted(expanded) ? Path.GetFullPath(expanded) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private sealed record Discovered(string Id, string Path, long? Bytes, DateTimeOffset? LastWriteTimeUtc, byte[]? Prefix, string? Note);
}
