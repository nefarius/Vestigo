using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.LogFiles;

/// <summary>MCP tools that read one JSON document inside a configured root.</summary>
[McpServerToolType]
public sealed class JsonTools(VestigoOptions options, RootPolicy roots)
{
    [McpServerTool(Name = "read_json_path")]
    [Description("Reads one JSON value, or a bounded set of values, from a UTF-8 JSON document under a root with Read access. The query supports $.name, quoted keys, array indexes, and one trailing .* or [*] wildcard. It does not filter, slice, or walk descendants. Line-oriented NDJSON belongs to search_text. Matched values are not written to the log.")]
    public OperationResult<JsonQueryResult> ReadJsonPath(
        [Description("File path: root:relative\\path or an absolute path inside a root.")] string path,
        [Description("Query such as $.status, $['dashed-key'], $.items[0].name, or $.items[*].")] string query,
        [Description("Optional byte cap, clamped down to Vestigo:Logs:MaxJsonBytes. The file is not read when it is larger.")] int? maxBytes = null) =>
        Read(path, maxBytes, (root, documentBytes, sha) => ProjectQuery(path, query, root, documentBytes, sha));

    [McpServerTool(Name = "get_json_summary")]
    [Description("Returns the root type, SHA-256, and top-level shape of a UTF-8 JSON document under a root with Read access. Object properties include type and serialized size. Array elements are counted, not listed. Values are not written to the log.")]
    public OperationResult<JsonSummary> GetJsonSummary(
        [Description("File path: root:relative\\path or an absolute path inside a root.")] string path,
        [Description("Optional byte cap, clamped down to Vestigo:Logs:MaxJsonBytes. The file is not read when it is larger.")] int? maxBytes = null) =>
        Read(path, maxBytes, (root, documentBytes, sha) => OperationResult<JsonSummary>.Ok(Summarize(path, root, documentBytes, sha)));

    private OperationResult<JsonQueryResult> ProjectQuery(
        string path,
        string query,
        JsonNode? root,
        long documentBytes,
        string sha)
    {
        OperationResult<JsonSelection> selected = JsonPathEvaluator.Select(root, query, options.Logs.MaxJsonMatches);
        if (!selected.Success || selected.Value is null)
        {
            return OperationResult<JsonQueryResult>.Fail(selected.ErrorCode!, selected.Error!);
        }

        List<JsonMatch> matches = [];
        foreach (JsonHit hit in selected.Value.Hits)
        {
            string raw = JsonPathEvaluator.Raw(hit.Value);
            long size = Encoding.UTF8.GetByteCount(raw);
            if (!selected.Value.Wildcard && size > options.Logs.MaxJsonValueBytes)
            {
                return OperationResult<JsonQueryResult>.Fail(
                    "value_too_large",
                    $"Matched value at {hit.MatchedAt} is {size} bytes, above the {options.Logs.MaxJsonValueBytes} byte limit.");
            }

            bool omit = size > options.Logs.MaxJsonValueBytes;
            matches.Add(new JsonMatch(
                hit.MatchedAt,
                JsonPathEvaluator.TypeName(hit.Value),
                omit ? null : CloneValue(hit.Value),
                size,
                omit));
        }

        (IReadOnlyList<string> keys, bool keysTruncated) = TopLevelKeys(root);
        return OperationResult<JsonQueryResult>.Ok(new JsonQueryResult(
            path,
            documentBytes,
            sha,
            JsonPathEvaluator.TypeName(root),
            keys,
            keysTruncated,
            matches,
            selected.Value.Truncated));
    }

    private JsonSummary Summarize(string path, JsonNode? root, long documentBytes, string sha)
    {
        if (root is JsonArray array)
        {
            return new JsonSummary(path, documentBytes, sha, "array", array.Count, null, [], false);
        }

        if (root is not JsonObject document)
        {
            return new JsonSummary(path, documentBytes, sha, JsonPathEvaluator.TypeName(root), null, null, [], false);
        }

        List<JsonMemberSummary> members = [];
        int count = 0;
        foreach (KeyValuePair<string, JsonNode?> property in document)
        {
            count++;
            if (members.Count >= options.Logs.MaxJsonMatches)
            {
                continue;
            }

            members.Add(new JsonMemberSummary(
                property.Key,
                JsonPathEvaluator.TypeName(property.Value),
                Encoding.UTF8.GetByteCount(JsonPathEvaluator.Raw(property.Value))));
        }

        return new JsonSummary(
            path,
            documentBytes,
            sha,
            "object",
            null,
            count,
            members,
            count > members.Count);
    }

    private (IReadOnlyList<string> Keys, bool Truncated) TopLevelKeys(JsonNode? root)
    {
        if (root is not JsonObject document)
        {
            return ([], false);
        }

        List<string> keys = [];
        int count = 0;
        foreach (KeyValuePair<string, JsonNode?> property in document)
        {
            count++;
            if (keys.Count < options.Logs.MaxJsonMatches)
            {
                keys.Add(property.Key);
            }
        }

        return (keys, count > keys.Count);
    }

    private OperationResult<T> Read<T>(string path, int? maxBytes, Func<JsonNode?, long, string, OperationResult<T>> project)
    {
        if (maxBytes is < 1)
        {
            return OperationResult<T>.Fail("invalid_request", "maxBytes must be positive.");
        }

        int limit = maxBytes is null
            ? options.Logs.MaxJsonBytes
            : Math.Min(maxBytes.Value, options.Logs.MaxJsonBytes);
        PathCheck check = roots.ResolveExisting(path, RootAccess.Read);
        if (!check.Success)
        {
            return OperationResult<T>.Fail(check.ErrorCode!, check.Error!);
        }

        if (!File.Exists(check.FullPath))
        {
            return OperationResult<T>.Fail("not_a_file", "Path is not a file.");
        }

        long length;
        try
        {
            length = new FileInfo(check.FullPath!).Length;
        }
        catch (IOException exception)
        {
            return OperationResult<T>.Fail("io_error", exception.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult<T>.Fail("access_denied", "Windows denied access to the file.");
        }

        if (length > limit)
        {
            return OperationResult<T>.Fail(
                "file_too_large",
                $"JSON document is {length} bytes, above the {limit} byte limit.");
        }

        try
        {
            using FileStream stream = new(
                check.FullPath!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            long size = stream.Length;
            if (size > limit)
            {
                return OperationResult<T>.Fail(
                    "file_too_large",
                    $"JSON document is {size} bytes, above the {limit} byte limit.");
            }

            if (size > int.MaxValue)
            {
                return OperationResult<T>.Fail("file_too_large", "JSON document is too large to read.");
            }

            byte[] bytes = new byte[size];
            stream.ReadExactly(bytes);
            string sha = Convert.ToHexString(SHA256.HashData(bytes));
            try
            {
                return project(
                    JsonNode.Parse(
                        bytes,
                        documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }),
                    bytes.LongLength,
                    sha);
            }
            catch (JsonException exception)
            {
                if (exception.LineNumber is null &&
                    exception.Message.Contains("Duplicate property", StringComparison.Ordinal))
                {
                    return OperationResult<T>.Fail("invalid_json", "JSON object contains a duplicate property name.");
                }

                long offset = ByteOffset(bytes, exception);
                return OperationResult<T>.Fail("invalid_json", $"JSON is invalid at byte {offset}.");
            }
        }
        catch (FileNotFoundException)
        {
            return OperationResult<T>.Fail("not_found", "File disappeared while reading.");
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult<T>.Fail("access_denied", "Windows denied access to the file.");
        }
        catch (IOException exception)
        {
            return OperationResult<T>.Fail("io_error", exception.Message);
        }
    }

    /// <summary>
    ///     Copies a JSON value into an element that stays valid after the source is released.
    ///     A missing node is JSON null, which remains a present value.
    /// </summary>
    private static JsonElement CloneValue(JsonNode? node)
    {
        if (node is null)
        {
            using JsonDocument document = JsonDocument.Parse("null");
            return document.RootElement.Clone();
        }

        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>Converts a parser line and byte-in-line into a document byte offset.</summary>
    internal static long ByteOffset(ReadOnlySpan<byte> utf8, JsonException exception)
    {
        long line = exception.LineNumber ?? 0;
        long column = exception.BytePositionInLine ?? 0;
        long offset = 0;
        long currentLine = 0;
        for (int index = 0; index < utf8.Length && currentLine < line; index++)
        {
            offset++;
            if (utf8[index] == (byte)'\n')
            {
                currentLine++;
            }
        }

        long position = offset + Math.Max(0, column);
        return Math.Clamp(position, 0, utf8.Length);
    }
}
