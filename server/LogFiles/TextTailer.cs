using System.Text;

namespace Vestigo.LogFiles;

/// <summary>Position in a text file: byte offset, length when read, and a file identity hint.</summary>
/// <param name="Offset">Byte offset where the next read starts.</param>
/// <param name="Length">File length in bytes when the cursor was produced.</param>
/// <param name="Identity">Creation time ticks (UTC) or 0 when unknown. A change means the file was replaced.</param>
public sealed record TextCursor(long Offset, long Length, long Identity);

/// <summary>Lines read from a text file plus the cursor to continue from.</summary>
public sealed record TextSlice(
    IReadOnlyList<string> Lines,
    TextCursor Cursor,
    bool Truncated,
    bool Rotated,
    bool HasMore,
    string Encoding);

/// <summary>
///     Reads the end of a text file or continues from a cursor. Works on any seekable stream so it can be tested
///     without files. Detects UTF-8 BOM, UTF-16 LE/BE BOM, and otherwise assumes UTF-8.
/// </summary>
public static class TextTailer
{
    /// <summary>Returns the last <paramref name="maxLines" /> lines, examining at most <paramref name="maxBytes" /> bytes.</summary>
    public static TextSlice Tail(Stream stream, long identity, int maxLines, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 4);

        long length = stream.Length;
        FileEncoding file = Detect(stream, length);
        long window = Math.Min(length - file.BomLength, maxBytes);
        long start = length - window;
        if (file.UnitSize == 2 && (start - file.BomLength) % 2 != 0)
        {
            start++;
        }

        bool cutBefore = start > file.BomLength;
        long readFrom = cutBefore ? start - file.UnitSize : start;
        byte[] buffer = ReadRange(stream, readFrom, length);

        int skip = 0;
        bool boundary = true;
        if (cutBefore)
        {
            boundary = EndsLine(buffer, file);
            skip = file.UnitSize;
            if (file.UnitSize == 1)
            {
                while (skip < buffer.Length && (buffer[skip] & 0xC0) == 0x80)
                {
                    skip++;
                }
            }
        }

        string text = file.Encoding.GetString(buffer, skip, buffer.Length - skip);
        List<string> lines = SplitAll(text);
        bool truncated = cutBefore;
        if (cutBefore && !boundary && lines.Count > 1)
        {
            lines.RemoveAt(0);
        }

        if (lines.Count > maxLines)
        {
            lines.RemoveRange(0, lines.Count - maxLines);
            truncated = true;
        }

        return new TextSlice(lines, new TextCursor(length, length, identity), truncated, false, false, file.Name);
    }

    /// <summary>
    ///     Returns complete lines after <paramref name="cursor" />. A final line without a terminator is held back
    ///     until it is completed. When the file shrank or was replaced, reading restarts at the beginning and
    ///     <see cref="TextSlice.Rotated" /> is set.
    /// </summary>
    public static TextSlice ReadSince(Stream stream, long identity, TextCursor cursor, int maxLines, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 4);

        long length = stream.Length;
        FileEncoding file = Detect(stream, length);
        long offset = Math.Max(cursor.Offset, 0);
        bool rotated = offset > length
                       || (cursor.Identity != 0 && identity != 0 && cursor.Identity != identity);
        if (rotated)
        {
            offset = 0;
        }

        offset = Math.Max(offset, file.BomLength);
        if (file.UnitSize == 2 && (offset - file.BomLength) % 2 != 0)
        {
            offset--;
        }

        long window = Math.Min(length - offset, maxBytes);
        if (window <= 0)
        {
            return new TextSlice([], new TextCursor(Math.Min(offset, length), length, identity), false, rotated, false, file.Name);
        }

        bool more = offset + window < length;
        byte[] buffer = ReadRange(stream, offset, offset + window);
        int usable = TrimIncompleteTail(buffer, file);
        string text = file.Encoding.GetString(buffer, 0, usable);

        List<string> lines = [];
        int position = 0;
        while (lines.Count < maxLines)
        {
            int newline = text.IndexOf('\n', position);
            if (newline < 0)
            {
                break;
            }

            lines.Add(StripCarriageReturn(text.AsSpan(position, newline - position)));
            position = newline + 1;
        }

        bool truncated = false;
        if (lines.Count == 0 && more && text.Length > 0)
        {
            // A single line longer than the byte cap: hand it out in pieces rather than stalling.
            lines.Add(StripCarriageReturn(text));
            position = text.Length;
            truncated = true;
        }

        // Advance by raw bytes: re-encoding decoded text is wrong for bytes that are invalid in the assumed encoding.
        long consumed = truncated ? usable : BytesThroughNewline(buffer, usable, lines.Count, file);
        long next = offset + consumed;
        bool hasMore = lines.Count >= maxLines || more;
        return new TextSlice(lines, new TextCursor(next, length, identity), truncated, rotated, hasMore && next < length, file.Name);
    }

    private static int BytesThroughNewline(byte[] buffer, int usable, int newlines, FileEncoding file)
    {
        int step = file.UnitSize;
        int seen = 0;
        for (int i = 0; i + step <= usable && seen < newlines; i += step)
        {
            bool isNewline = file.Name switch
            {
                "utf-16le" => buffer[i] == 0x0A && buffer[i + 1] == 0,
                "utf-16be" => buffer[i] == 0 && buffer[i + 1] == 0x0A,
                _ => buffer[i] == 0x0A
            };
            if (isNewline && ++seen == newlines)
            {
                return i + step;
            }
        }

        return 0;
    }

    private static List<string> SplitAll(string text)
    {
        List<string> lines = [];
        if (text.Length == 0)
        {
            return lines;
        }

        int position = 0;
        while (position < text.Length)
        {
            int newline = text.IndexOf('\n', position);
            if (newline < 0)
            {
                lines.Add(StripCarriageReturn(text.AsSpan(position)));
                break;
            }

            lines.Add(StripCarriageReturn(text.AsSpan(position, newline - position)));
            position = newline + 1;
        }

        return lines;
    }

    private static string StripCarriageReturn(ReadOnlySpan<char> line) =>
        (line.Length > 0 && line[^1] == '\r' ? line[..^1] : line).ToString();

    private static bool EndsLine(byte[] buffer, FileEncoding file) => file.Name switch
    {
        "utf-16le" => buffer.Length >= 2 && buffer[0] == 0x0A && buffer[1] == 0,
        "utf-16be" => buffer.Length >= 2 && buffer[0] == 0 && buffer[1] == 0x0A,
        _ => buffer.Length >= 1 && buffer[0] == 0x0A
    };

    /// <summary>Drops a trailing partial code point so decoding never produces replacement characters.</summary>
    private static int TrimIncompleteTail(byte[] buffer, FileEncoding file)
    {
        if (file.UnitSize == 2)
        {
            int even = buffer.Length & ~1;
            if (even >= 2)
            {
                int last = file.Name == "utf-16le" ? buffer[even - 1] : buffer[even - 2];
                if (last is >= 0xD8 and <= 0xDB)
                {
                    even -= 2;
                }
            }

            return even;
        }

        for (int back = 1; back <= Math.Min(3, buffer.Length); back++)
        {
            byte value = buffer[buffer.Length - back];
            if ((value & 0xC0) == 0x80)
            {
                continue;
            }

            int needed = value >= 0xF0 ? 4 : value >= 0xE0 ? 3 : value >= 0xC0 ? 2 : 1;
            return needed > back ? buffer.Length - back : buffer.Length;
        }

        return buffer.Length;
    }

    private static byte[] ReadRange(Stream stream, long from, long to)
    {
        byte[] buffer = new byte[checked((int)(to - from))];
        stream.Seek(from, SeekOrigin.Begin);
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static FileEncoding Detect(Stream stream, long length)
    {
        byte[] head = new byte[(int)Math.Min(3, length)];
        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(head);
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            return new FileEncoding(new UTF8Encoding(false), "utf-8", 3, 1);
        }

        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
        {
            return new FileEncoding(new UnicodeEncoding(false, false), "utf-16le", 2, 2);
        }

        if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
        {
            return new FileEncoding(new UnicodeEncoding(true, false), "utf-16be", 2, 2);
        }

        return new FileEncoding(new UTF8Encoding(false), "utf-8", 0, 1);
    }

    private sealed record FileEncoding(Encoding Encoding, string Name, int BomLength, int UnitSize);
}
