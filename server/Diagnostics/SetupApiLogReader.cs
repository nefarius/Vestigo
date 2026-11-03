using System.Globalization;
using System.Text;

namespace Vestigo.Diagnostics;

/// <summary>
///     Reads <c>%windir%\INF\setupapi.dev.log</c> and rotated <c>setupapi.dev.*.log</c> files.
///     The length of each file is captured when it is opened. Bytes past <paramref name="maxBytes" /> are not read.
/// </summary>
internal sealed class SetupApiLogReader : ISetupApiLogSource
{
    private readonly string? _directory;

    public SetupApiLogReader()
    {
    }

    internal SetupApiLogReader(string directory) => _directory = directory;

    public OperationResult<InstallHistory> Read(string instanceId, DateTimeOffset? since, int limit, bool includeRaw, long maxBytes)
    {
        if (maxBytes < 1)
        {
            return OperationResult<InstallHistory>.Fail("invalid_request", "The SetupAPI read cap must be positive.");
        }

        string directory = ResolveDirectory();
        if (!Directory.Exists(directory))
        {
            return OperationResult<InstallHistory>.Fail("io_error", "The SetupAPI log directory is not available.");
        }

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(directory)
                .EnumerateFiles()
                .Where(file => IsDevLog(file.Name))
                .OrderBy(SortKey)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult<InstallHistory>.Fail(
                ex is UnauthorizedAccessException ? "access_denied" : "io_error",
                "The SetupAPI log could not be read.");
        }

        List<InstallSection> sections = [];
        bool truncated = false;
        foreach (LogSlice slice in SelectNewestWithinBudget(files, maxBytes, ref truncated))
        {
            try
            {
                foreach (InstallSection section in ReadFile(slice, instanceId, includeRaw))
                {
                    sections.Add(section);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return OperationResult<InstallHistory>.Fail(
                    ex is UnauthorizedAccessException ? "access_denied" : "io_error",
                    "The SetupAPI log could not be read.");
            }
        }

        if (since is { } start)
        {
            sections = sections.Where(section => InWindow(section, start)).ToList();
        }

        sections.Sort(static (left, right) => When(left).CompareTo(When(right)));
        if (sections.Count > limit)
        {
            sections.RemoveRange(0, sections.Count - limit);
        }

        return OperationResult<InstallHistory>.Ok(new InstallHistory(instanceId, truncated, sections));
    }

    internal static IReadOnlyList<string> ReadLines(Stream stream, long maxBytes, Encoding encoding)
    {
        using ByteLimitedStream limited = new(stream, maxBytes);
        using StreamReader reader = new(limited, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        List<string> lines = [];
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static List<LogSlice> SelectNewestWithinBudget(List<FileInfo> oldestFirst, long maxBytes, ref bool truncated)
    {
        long remaining = maxBytes;
        List<LogSlice> chosen = [];
        for (int index = oldestFirst.Count - 1; index >= 0; index--)
        {
            if (remaining <= 0)
            {
                truncated = true;
                break;
            }

            FileInfo file = oldestFirst[index];
            long length = file.Length;
            if (length > remaining)
            {
                truncated = true;
                chosen.Add(new LogSlice(file, length - remaining, remaining));
                break;
            }

            chosen.Add(new LogSlice(file, 0, length));
            remaining -= length;
        }

        chosen.Reverse();
        return chosen;
    }

    private static IReadOnlyList<InstallSection> ReadFile(LogSlice slice, string instanceId, bool includeRaw)
    {
        using FileStream stream = new(slice.File.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Encoding encoding = Detect(stream);
        stream.Position = slice.Skip;
        List<string> lines = [..ReadLines(stream, slice.Take, encoding)];
        if (slice.Skip > 0 && lines.Count > 0)
        {
            lines.RemoveAt(0);
        }

        return SetupApiSectionParser.Parse(lines, slice.File.Name, instanceId, includeRaw);
    }

    private readonly record struct LogSlice(FileInfo File, long Skip, long Take);

    private static Encoding Detect(Stream stream)
    {
        byte[] sample = new byte[64];
        int read = stream.Read(sample, 0, sample.Length);
        stream.Position = 0;
        if (read >= 2 && sample[0] == 0xFF && sample[1] == 0xFE)
        {
            return Encoding.Unicode;
        }

        for (int index = 1; index < read; index += 2)
        {
            if (sample[index] == 0)
            {
                return Encoding.Unicode;
            }
        }

        return Encoding.UTF8;
    }

    private static bool InWindow(InstallSection section, DateTimeOffset since)
    {
        DateTimeOffset? when = section.EndedAt ?? section.StartedAt;
        return when is null || when >= since;
    }

    private static DateTimeOffset When(InstallSection section) => section.StartedAt ?? section.EndedAt ?? DateTimeOffset.MinValue;

    private string ResolveDirectory()
    {
        if (_directory is not null)
        {
            return _directory;
        }

        string? windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
        {
            windows = Environment.GetEnvironmentVariable("SystemRoot");
        }

        return Path.Combine(windows ?? string.Empty, "INF");
    }

    private static bool IsDevLog(string name) =>
        name.Equals("setupapi.dev.log", StringComparison.OrdinalIgnoreCase) ||
        (name.StartsWith("setupapi.dev.", StringComparison.OrdinalIgnoreCase) &&
         name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) &&
         name.Length > "setupapi.dev.log".Length);

    private static DateTimeOffset SortKey(FileInfo file)
    {
        if (file.Name.Equals("setupapi.dev.log", StringComparison.OrdinalIgnoreCase))
        {
            return DateTimeOffset.MaxValue;
        }

        string middle = file.Name["setupapi.dev.".Length..^".log".Length];
        if (DateTime.TryParseExact(middle, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime stamp))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(stamp, DateTimeKind.Utc));
        }

        return new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
    }

    private sealed class ByteLimitedStream(Stream inner, long maxBytes) : Stream
    {
        private long _read;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => maxBytes;

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long remaining = maxBytes - _read;
            if (remaining <= 0)
            {
                return 0;
            }

            if (buffer.Length > remaining)
            {
                buffer = buffer[..(int)Math.Min(remaining, int.MaxValue)];
            }

            int read = inner.Read(buffer);
            _read += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
