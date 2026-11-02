using System.Text;
using System.Text.Json;

namespace Vestigo.LogFiles;

/// <summary>One decoded ripgrep <c>--json</c> message that Vestigo cares about.</summary>
/// <param name="Kind">"match", "context" or "summary".</param>
/// <param name="Path">File path for match and context messages.</param>
/// <param name="LineNumber">One-based line number, or 0 when ripgrep did not report one.</param>
/// <param name="Text">Line text without its terminator.</param>
/// <param name="FirstMatchByte">UTF-8 byte offset of the first submatch within the line, or -1.</param>
/// <param name="BytesSearched">Total bytes searched, set on the summary message.</param>
public sealed record RipgrepEvent(
    string Kind,
    string? Path,
    long LineNumber,
    string? Text,
    int FirstMatchByte,
    long? BytesSearched);

/// <summary>Parses ripgrep <c>--json</c> lines. Messages of other kinds are ignored.</summary>
public static class RipgrepOutputParser
{
    public static bool TryParse(string line, out RipgrepEvent? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("type", out JsonElement typeElement) ||
                !root.TryGetProperty("data", out JsonElement data))
            {
                return false;
            }

            string? type = typeElement.GetString();
            switch (type)
            {
                case "match":
                case "context":
                    result = new RipgrepEvent(
                        type,
                        data.TryGetProperty("path", out JsonElement path) ? ReadArbitraryData(path) : null,
                        data.TryGetProperty("line_number", out JsonElement number) && number.ValueKind == JsonValueKind.Number
                            ? number.GetInt64()
                            : 0,
                        data.TryGetProperty("lines", out JsonElement lines)
                            ? TrimTerminator(ReadArbitraryData(lines))
                            : null,
                        FirstSubmatchStart(data),
                        null);
                    return true;
                case "summary":
                    long? bytes = data.TryGetProperty("stats", out JsonElement stats) &&
                                  stats.TryGetProperty("bytes_searched", out JsonElement searched) &&
                                  searched.ValueKind == JsonValueKind.Number
                        ? searched.GetInt64()
                        : null;
                    result = new RipgrepEvent("summary", null, 0, null, -1, bytes);
                    return true;
                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>ripgrep encodes non-UTF-8 data as <c>{"bytes": "&lt;base64&gt;"}</c> instead of <c>{"text": ...}</c>.</summary>
    private static string? ReadArbitraryData(JsonElement element)
    {
        if (element.TryGetProperty("text", out JsonElement text))
        {
            return text.GetString();
        }

        if (element.TryGetProperty("bytes", out JsonElement bytes) && bytes.GetString() is string base64)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return null;
    }

    private static int FirstSubmatchStart(JsonElement data)
    {
        if (data.TryGetProperty("submatches", out JsonElement submatches) &&
            submatches.ValueKind == JsonValueKind.Array &&
            submatches.GetArrayLength() > 0 &&
            submatches[0].TryGetProperty("start", out JsonElement start) &&
            start.ValueKind == JsonValueKind.Number)
        {
            return start.GetInt32();
        }

        return -1;
    }

    private static string? TrimTerminator(string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (text.EndsWith('\n'))
        {
            text = text[..^1];
        }

        return text.EndsWith('\r') ? text[..^1] : text;
    }
}
