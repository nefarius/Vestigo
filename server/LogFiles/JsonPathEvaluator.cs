using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Vestigo.LogFiles;

/// <summary>One JSON value selected by <see cref="JsonPathEvaluator" />. The element is valid only while its document is alive.</summary>
/// <param name="MatchedAt">Canonical path of the value.</param>
/// <param name="Value">Selected JSON value.</param>
public sealed record JsonHit(string MatchedAt, JsonElement Value);

/// <summary>Values selected from one document.</summary>
/// <param name="Hits">Matches in document order, after the match cap.</param>
/// <param name="Truncated">True when the match cap dropped later values.</param>
/// <param name="Wildcard">True when the query ended in a wildcard.</param>
public sealed record JsonSelection(IReadOnlyList<JsonHit> Hits, bool Truncated, bool Wildcard);

/// <summary>
///     Evaluates a bounded JSONPath-like subset: <c>$</c>, dotted identifiers, quoted keys, array indexes,
///     and one trailing <c>.*</c> or <c>[*]</c> wildcard. It does not filter, slice, or walk descendants.
/// </summary>
public static class JsonPathEvaluator
{
    /// <summary>Sentence returned with <c>invalid_query</c>.</summary>
    public const string SupportedSubset =
        "Supported queries are $, $.name, $['key'], $[0], and one trailing .* or [*] wildcard. Filters, recursive descent, slices, and unions are not supported.";

    private const int MaxQueryLength = 2048;

    /// <summary>Selects values from <paramref name="root" />. An empty wildcard match is success.</summary>
    public static OperationResult<JsonSelection> Select(JsonElement root, string? query, int maxMatches)
    {
        if (maxMatches < 1)
        {
            return OperationResult<JsonSelection>.Fail("invalid_request", "The match limit must be positive.");
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return Fail("Query is empty.");
        }

        if (query.Length > MaxQueryLength)
        {
            return Fail($"Query is longer than {MaxQueryLength} characters.");
        }

        OperationResult<List<Segment>> parsed = Parse(query.Trim());
        if (!parsed.Success || parsed.Value is null)
        {
            return OperationResult<JsonSelection>.Fail(parsed.ErrorCode!, parsed.Error!);
        }

        List<JsonHit> hits = [new("$", root)];
        bool wildcard = false;
        foreach (Segment segment in parsed.Value)
        {
            if (segment.Kind == SegmentKind.Wildcard)
            {
                wildcard = true;
                hits = Expand(hits);
                break;
            }

            List<JsonHit>? next = Apply(hits, segment);
            if (next is null)
            {
                return OperationResult<JsonSelection>.Fail("query_no_match", "Query did not match a value.");
            }

            hits = next;
        }

        bool truncated = hits.Count > maxMatches;
        if (truncated)
        {
            hits.RemoveRange(maxMatches, hits.Count - maxMatches);
        }

        return OperationResult<JsonSelection>.Ok(new JsonSelection(hits, truncated, wildcard));
    }

    /// <summary>Stable type name for a JSON value.</summary>
    public static string TypeName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "unknown"
    };

    private static OperationResult<List<Segment>> Parse(string query)
    {
        if (query[0] != '$')
        {
            return Invalid("Query must start with $.");
        }

        List<Segment> segments = [];
        int index = 1;
        while (index < query.Length)
        {
            if (segments.Count > 0 && segments[^1].Kind == SegmentKind.Wildcard)
            {
                return Invalid("A wildcard must be the last part of the query.");
            }

            char current = query[index];
            if (current == '.')
            {
                index++;
                if (index < query.Length && query[index] == '*')
                {
                    segments.Add(Segment.Wildcard());
                    index++;
                    continue;
                }

                if (index < query.Length && query[index] == '.')
                {
                    return Invalid("Recursive descent is not supported.");
                }

                int start = index;
                if (index >= query.Length || !IsIdentifierStart(query[index]))
                {
                    return Invalid("A dot must be followed by a name or *.");
                }

                index++;
                while (index < query.Length && IsIdentifierPart(query[index]))
                {
                    index++;
                }

                segments.Add(Segment.Property(query[start..index]));
                continue;
            }

            if (current == '[')
            {
                OperationResult<Segment> bracket = ReadBracket(query, ref index);
                if (!bracket.Success || bracket.Value is null)
                {
                    return OperationResult<List<Segment>>.Fail(bracket.ErrorCode!, bracket.Error!);
                }

                segments.Add(bracket.Value);
                continue;
            }

            if (current == '?' || current == '@')
            {
                return Invalid("Filters are not supported.");
            }

            return Invalid("Unsupported query syntax.");
        }

        return OperationResult<List<Segment>>.Ok(segments);
    }

    private static OperationResult<Segment> ReadBracket(string query, ref int index)
    {
        index++;
        if (index >= query.Length)
        {
            return OperationResult<Segment>.Fail("invalid_query", "A bracket is not closed. " + SupportedSubset);
        }

        if (query[index] == '*')
        {
            index++;
            return Consume(query, ref index, ']')
                ? OperationResult<Segment>.Ok(Segment.Wildcard())
                : InvalidSegment("A wildcard bracket must be [*].");
        }

        if (query[index] is '\'' or '"')
        {
            OperationResult<string> key = ReadQuoted(query, ref index);
            if (!key.Success || key.Value is null)
            {
                return OperationResult<Segment>.Fail(key.ErrorCode!, key.Error!);
            }

            return Consume(query, ref index, ']')
                ? OperationResult<Segment>.Ok(Segment.Property(key.Value))
                : InvalidSegment("A quoted key must end with ].");
        }

        if (query[index] == ':')
        {
            return InvalidSegment("Slices are not supported.");
        }

        int start = index;
        if (!char.IsAsciiDigit(query[index]))
        {
            return InvalidSegment("A bracket must contain an index, a quoted key, or *.");
        }

        while (index < query.Length && char.IsAsciiDigit(query[index]))
        {
            index++;
        }

        if (index < query.Length && query[index] == ':')
        {
            return InvalidSegment("Slices are not supported.");
        }

        if (!int.TryParse(query[start..index], NumberStyles.None, CultureInfo.InvariantCulture, out int element))
        {
            return InvalidSegment("Array index does not fit in a 32-bit integer.");
        }

        return Consume(query, ref index, ']')
            ? OperationResult<Segment>.Ok(Segment.At(element))
            : InvalidSegment("An array index must end with ].");
    }

    private static OperationResult<string> ReadQuoted(string query, ref int index)
    {
        char quote = query[index];
        index++;
        StringBuilder key = new();
        while (index < query.Length)
        {
            char current = query[index++];
            if (current == '\\')
            {
                if (index >= query.Length)
                {
                    return OperationResult<string>.Fail("invalid_query", "A quoted key has a trailing escape. " + SupportedSubset);
                }

                char escaped = query[index++];
                if (escaped != quote && escaped != '\\')
                {
                    return OperationResult<string>.Fail(
                        "invalid_query",
                        "Only a quote or backslash can be escaped in a key. " + SupportedSubset);
                }

                key.Append(escaped);
                continue;
            }

            if (current == quote)
            {
                return OperationResult<string>.Ok(key.ToString());
            }

            key.Append(current);
        }

        return OperationResult<string>.Fail("invalid_query", "A quoted key is not closed. " + SupportedSubset);
    }

    private static bool Consume(string query, ref int index, char expected)
    {
        if (index >= query.Length || query[index] != expected)
        {
            return false;
        }

        index++;
        return true;
    }

    private static List<JsonHit>? Apply(List<JsonHit> current, Segment segment)
    {
        List<JsonHit> next = [];
        foreach (JsonHit hit in current)
        {
            if (segment.Kind == SegmentKind.Property)
            {
                if (hit.Value.ValueKind != JsonValueKind.Object ||
                    !hit.Value.TryGetProperty(segment.Name!, out JsonElement child))
                {
                    return null;
                }

                next.Add(new JsonHit(PropertyPath(hit.MatchedAt, segment.Name!), child));
                continue;
            }

            if (hit.Value.ValueKind != JsonValueKind.Array || !TryElement(hit.Value, segment.Index, out JsonElement element))
            {
                return null;
            }

            next.Add(new JsonHit(hit.MatchedAt + "[" + segment.Index.ToString(CultureInfo.InvariantCulture) + "]", element));
        }

        return next;
    }

    private static bool TryElement(JsonElement array, int index, out JsonElement element)
    {
        int current = 0;
        foreach (JsonElement child in array.EnumerateArray())
        {
            if (current == index)
            {
                element = child;
                return true;
            }

            current++;
        }

        element = default;
        return false;
    }

    private static List<JsonHit> Expand(List<JsonHit> current)
    {
        List<JsonHit> next = [];
        foreach (JsonHit hit in current)
        {
            if (hit.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in hit.Value.EnumerateObject())
                {
                    next.Add(new JsonHit(PropertyPath(hit.MatchedAt, property.Name), property.Value));
                }
            }
            else if (hit.Value.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement child in hit.Value.EnumerateArray())
                {
                    next.Add(new JsonHit(hit.MatchedAt + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", child));
                    index++;
                }
            }
        }

        return next;
    }

    private static string PropertyPath(string parent, string name) =>
        IsIdentifier(name) ? parent + "." + name : parent + "['" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "']";

    private static bool IsIdentifier(string name)
    {
        if (name.Length == 0 || !IsIdentifierStart(name[0]))
        {
            return false;
        }

        for (int index = 1; index < name.Length; index++)
        {
            if (!IsIdentifierPart(name[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdentifierStart(char value) => value is '_' or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

    private static bool IsIdentifierPart(char value) => IsIdentifierStart(value) || char.IsAsciiDigit(value);

    private static OperationResult<JsonSelection> Fail(string message) =>
        OperationResult<JsonSelection>.Fail("invalid_query", message + " " + SupportedSubset);

    private static OperationResult<List<Segment>> Invalid(string message) =>
        OperationResult<List<Segment>>.Fail("invalid_query", message + " " + SupportedSubset);

    private static OperationResult<Segment> InvalidSegment(string message) =>
        OperationResult<Segment>.Fail("invalid_query", message + " " + SupportedSubset);

    private enum SegmentKind
    {
        Property,
        Index,
        Wildcard
    }

    private sealed record Segment(SegmentKind Kind, string? Name, int Index)
    {
        public static Segment Property(string name) => new(SegmentKind.Property, name, 0);

        public static Segment At(int index) => new(SegmentKind.Index, null, index);

        public static Segment Wildcard() => new(SegmentKind.Wildcard, null, 0);
    }
}
