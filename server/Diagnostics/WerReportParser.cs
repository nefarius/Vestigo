using System.Globalization;
using System.Text;

namespace Vestigo.Diagnostics;

/// <summary>Fields read from one <c>Report.wer</c>. The parser does not open dump files.</summary>
internal sealed record ParsedWerReport(
    string? EventType,
    DateTimeOffset? EventTime,
    string? AppName,
    string? AppPath,
    string? ReportId,
    bool Truncated,
    IReadOnlyList<CrashSignature> Signatures);

/// <summary>Parses a bounded <c>Report.wer</c> buffer. Values are capped and raw text is not retained.</summary>
internal static class WerReportParser
{
    public const int MaxSignatures = 16;
    private const int MaxLine = 4096;

    public static ParsedWerReport Parse(ReadOnlySpan<byte> bytes, bool truncated)
    {
        string text = Decode(bytes);
        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, string> names = [];
        Dictionary<int, string> values = [];
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.Length > MaxLine)
            {
                continue;
            }

            int split = line.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            string key = line[..split].Trim();
            string value = Cap(line[(split + 1)..].Trim(), 512) ?? "";
            if (TrySignature(key, out int index, out bool name))
            {
                if (index is >= 0 and < 64)
                {
                    (name ? names : values)[index] = name ? Cap(value, 128) ?? "" : value;
                }

                continue;
            }

            fields.TryAdd(key, value);
        }

        List<CrashSignature> signatures = [];
        foreach (int index in names.Keys.Union(values.Keys).OrderBy(item => item))
        {
            if (signatures.Count == MaxSignatures)
            {
                break;
            }

            names.TryGetValue(index, out string? signatureName);
            values.TryGetValue(index, out string? signatureValue);
            if (string.IsNullOrEmpty(signatureName) && string.IsNullOrEmpty(signatureValue))
            {
                continue;
            }

            signatures.Add(new CrashSignature(signatureName ?? "", signatureValue ?? ""));
        }

        return new ParsedWerReport(
            Cap(First(fields, "EventType"), 64),
            EventTime(First(fields, "EventTime")),
            Cap(First(fields, "AppName", "NsAppName"), 260),
            Cap(First(fields, "AppPath"), 1024),
            Cap(First(fields, "ReportIdentifier"), 80),
            truncated,
            signatures);
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        int zeros = 0;
        int samples = 0;
        for (int index = 1; index < bytes.Length && samples < 32; index += 2)
        {
            samples++;
            if (bytes[index] == 0)
            {
                zeros++;
            }
        }

        return zeros >= 8 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
    }

    private static bool TrySignature(string key, out int index, out bool name)
    {
        index = 0;
        name = false;
        string prefix = key.StartsWith("DynamicSig[", StringComparison.OrdinalIgnoreCase) ? "DynamicSig["
            : key.StartsWith("Sig[", StringComparison.OrdinalIgnoreCase) ? "Sig["
            : "";
        if (prefix.Length == 0)
        {
            return false;
        }

        int end = key.IndexOf(']', prefix.Length);
        if (end < 0 || !int.TryParse(key[prefix.Length..end], NumberStyles.None, CultureInfo.InvariantCulture, out index))
        {
            return false;
        }

        string tail = key[(end + 1)..];
        if (tail.Equals(".Name", StringComparison.OrdinalIgnoreCase))
        {
            name = true;
            return true;
        }

        return tail.Equals(".Value", StringComparison.OrdinalIgnoreCase);
    }

    private static string? First(Dictionary<string, string> fields, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (fields.TryGetValue(key, out string? value) && value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static DateTimeOffset? EventTime(string? value)
    {
        if (value is null || !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong fileTime) || fileTime > long.MaxValue)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(DateTime.FromFileTimeUtc((long)fileTime), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? Cap(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        string trimmed = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
