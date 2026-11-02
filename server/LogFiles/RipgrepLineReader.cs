using System.Text;

namespace Vestigo.LogFiles;

/// <summary>
///     Splits a ripgrep stdout or stderr byte stream into records at newlines, without buffering more than
///     <see cref="RecordByteLimit" /> bytes per record. An oversize record is discarded through its next newline
///     and reported as a warning so it never reaches <see cref="RipgrepOutputParser" />.
/// </summary>
internal static class RipgrepLineReader
{
    internal const string OversizedRecordWarning =
        "skipped a ripgrep record longer than Vestigo:Logs:MaxSearchLineLength.";

    /// <summary>
    ///     UTF-8 and JSON wrapping around a match line of <paramref name="maxSearchLineLength" /> characters,
    ///     plus a path and ripgrep's envelope.
    /// </summary>
    internal static int RecordByteLimit(int maxSearchLineLength) =>
        checked(Math.Max(maxSearchLineLength, 80) * 4 + 8192);

    public static async IAsyncEnumerable<RipgrepLine> ReadAsync(
        Stream source,
        bool isError,
        int maxBytes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        byte[] chunk = new byte[4096];
        byte[] buffer = new byte[maxBytes];
        int length = 0;
        bool overflow = false;
        int read;
        while ((read = await source.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            int offset = 0;
            while (offset < read)
            {
                int newline = Array.IndexOf(chunk, (byte)'\n', offset, read - offset);
                int end = newline >= 0 ? newline : read;
                int take = end - offset;
                if (!overflow)
                {
                    if (length + take > maxBytes)
                    {
                        overflow = true;
                        length = 0;
                    }
                    else if (take > 0)
                    {
                        chunk.AsSpan(offset, take).CopyTo(buffer.AsSpan(length));
                        length += take;
                    }
                }

                if (newline < 0)
                {
                    break;
                }

                yield return Complete(buffer, ref length, ref overflow, isError);
                offset = newline + 1;
            }
        }

        if (overflow)
        {
            yield return Warning();
        }
        else if (length > 0)
        {
            yield return Decode(buffer.AsSpan(0, length), isError);
        }
    }

    private static RipgrepLine Complete(byte[] buffer, ref int length, ref bool overflow, bool isError)
    {
        if (overflow)
        {
            overflow = false;
            length = 0;
            return Warning();
        }

        RipgrepLine line = Decode(buffer.AsSpan(0, length), isError);
        length = 0;
        return line;
    }

    private static RipgrepLine Decode(ReadOnlySpan<byte> payload, bool isError)
    {
        if (payload.Length > 0 && payload[^1] == (byte)'\r')
        {
            payload = payload[..^1];
        }

        return new RipgrepLine(Encoding.UTF8.GetString(payload), isError);
    }

    private static RipgrepLine Warning() => new(OversizedRecordWarning, true);
}
