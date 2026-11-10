using System.Text.Json;
using System.Text.Json.Nodes;

using Json.Path;

namespace Vestigo.LogFiles;

/// <summary>One JSON value selected by <see cref="JsonPathEvaluator" />.</summary>
/// <param name="MatchedAt">Normalized JSONPath location of the value.</param>
/// <param name="Value">Selected JSON value. A JSON null is a C# null.</param>
public sealed record JsonHit(string MatchedAt, JsonNode? Value);

/// <summary>Values selected from one document.</summary>
/// <param name="Hits">Matches in document order, after the match cap.</param>
/// <param name="Truncated">True when the match cap dropped later values.</param>
/// <param name="Wildcard">True when the query ended in a wildcard.</param>
public sealed record JsonSelection(IReadOnlyList<JsonHit> Hits, bool Truncated, bool Wildcard);

/// <summary>
///     Evaluates a bounded subset of RFC 9535 JSONPath through JsonPath.Net: <c>$</c>, names, indexes,
///     and one trailing <c>.*</c> or <c>[*]</c> wildcard. Filters, slices, unions, and recursive descent are rejected.
/// </summary>
public static class JsonPathEvaluator
{
    /// <summary>Sentence returned with <c>invalid_query</c>.</summary>
    public const string SupportedSubset =
        "Supported queries are $, $.name, $['key'], $[0], and one trailing .* or [*] wildcard. Filters, recursive descent, slices, and unions are not supported.";

    private const int MaxQueryLength = 2048;

    /// <summary>Selects values from <paramref name="root" />. An empty wildcard match is success.</summary>
    public static OperationResult<JsonSelection> Select(JsonNode? root, string? query, int maxMatches)
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

        JsonPath path;
        try
        {
            path = JsonPath.Parse(query.Trim());
        }
        catch (PathParseException)
        {
            return Fail("Unsupported query syntax.");
        }

        if (path.Scope != PathScope.Global)
        {
            return Fail("Query must start with $.");
        }

        OperationResult<bool> allowed = Allow(path);
        if (!allowed.Success)
        {
            return OperationResult<JsonSelection>.Fail(allowed.ErrorCode!, allowed.Error!);
        }

        bool wildcard = allowed.Value;
        if (root is null)
        {
            if (!wildcard && path.Segments.Any())
            {
                return OperationResult<JsonSelection>.Fail("query_no_match", "Query did not match a value.");
            }

            JsonHit[] rootHit = wildcard ? [] : [new("$", null)];
            return OperationResult<JsonSelection>.Ok(new JsonSelection(rootHit, false, wildcard));
        }

        // Matches is deferred. Keep one extra node so truncation is still visible, then stop.
        List<Node> matches = [];
        foreach (Node match in path.Evaluate(root).Matches)
        {
            matches.Add(match);
            if (matches.Count > maxMatches)
            {
                break;
            }
        }

        if (!wildcard && matches.Count == 0)
        {
            return OperationResult<JsonSelection>.Fail("query_no_match", "Query did not match a value.");
        }

        bool truncated = matches.Count > maxMatches;
        if (truncated)
        {
            matches.RemoveRange(maxMatches, matches.Count - maxMatches);
        }

        List<JsonHit> hits = matches.Select(match => new JsonHit(match.Location?.ToString() ?? "$", match.Value)).ToList();
        return OperationResult<JsonSelection>.Ok(new JsonSelection(hits, truncated, wildcard));
    }

    /// <summary>Stable type name for a JSON value.</summary>
    public static string TypeName(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => "unknown"
        },
        _ => "unknown"
    };

    /// <summary>UTF-8 JSON text of a value. A missing node is JSON null.</summary>
    public static string Raw(JsonNode? node) => node?.ToJsonString() ?? "null";

    private static OperationResult<bool> Allow(JsonPath path)
    {
        PathSegment[] segments = path.Segments.ToArray();
        bool wildcard = false;
        int position = 0;
        foreach (PathSegment segment in segments)
        {
            position++;
            if (segment.IsRecursive)
            {
                return OperationResult<bool>.Fail("invalid_query", "Recursive descent is not supported. " + SupportedSubset);
            }

            ISelector[] selectors = segment.Selectors.ToArray();
            if (selectors.Length != 1)
            {
                return OperationResult<bool>.Fail("invalid_query", "Unions are not supported. " + SupportedSubset);
            }

            bool last = position == segments.Length;
            switch (selectors[0])
            {
                case NameSelector:
                    break;
                case IndexSelector indexSelector when indexSelector.Index >= 0:
                    break;
                case IndexSelector:
                    return OperationResult<bool>.Fail("invalid_query", "Negative indexes are not supported. " + SupportedSubset);
                case WildcardSelector when last:
                    wildcard = true;
                    break;
                case WildcardSelector:
                    return OperationResult<bool>.Fail("invalid_query", "A wildcard must be the last part of the query. " + SupportedSubset);
                default:
                    return OperationResult<bool>.Fail("invalid_query", "Filters and slices are not supported. " + SupportedSubset);
            }
        }

        return OperationResult<bool>.Ok(wildcard);
    }

    private static OperationResult<JsonSelection> Fail(string message) =>
        OperationResult<JsonSelection>.Fail("invalid_query", message + " " + SupportedSubset);
}
