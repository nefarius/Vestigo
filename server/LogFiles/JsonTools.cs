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
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 64
    };

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
        JsonElement root,
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
            string raw = hit.Value.GetRawText();
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
                JsonPathEvaluator.TypeName(hit.Value.ValueKind),
                omit ? null : JsonNode.Parse(raw),
                size,
                omit));
        }

        (IReadOnlyList<string> keys, bool keysTruncated) = TopLevelKeys(root);
        return OperationResult<JsonQueryResult>.Ok(new JsonQueryResult(
            path,
            documentBytes,
            sha,
            JsonPathEvaluator.TypeName(root.ValueKind),
            keys,
            keysTruncated,
            matches,
            selected.Value.Truncated));
    }

    private JsonSummary Summarize(string path, JsonElement root, long documentBytes, string sha)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            int length = 0;
            foreach (JsonElement _ in root.EnumerateArray())
            {
                length++;
            }

            return new JsonSummary(path, documentBytes, sha, "array", length, null, [], false);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new JsonSummary(path, documentBytes, sha, JsonPathEvaluator.TypeName(root.ValueKind), null, null, [], false);
        }

        List<JsonMemberSummary> members = [];
        int count = 0;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            count++;
            if (members.Count >= options.Logs.MaxJsonMatches)
            {
                continue;
            }

            members.Add(new JsonMemberSummary(
                property.Name,
                JsonPathEvaluator.TypeName(property.Value.ValueKind),
                Encoding.UTF8.GetByteCount(property.Value.GetRawText())));
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

    private (IReadOnlyList<string> Keys, bool Truncated) TopLevelKeys(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ([], false);
        }

        List<string> keys = [];
        int count = 0;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            count++;
            if (keys.Count < options.Logs.MaxJsonMatches)
            {
                keys.Add(property.Name);
            }
        }

        return (keys, count > keys.Count);
    }

    private OperationResult<T> Read<T>(string path, int? maxBytes, Func<JsonElement, long, string, OperationResult<T>> project)
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
            if (stream.Length > limit)
            {
                return OperationResult<T>.Fail(
                    "file_too_large",
                    $"JSON document is {stream.Length} bytes, above the {limit} byte limit.");
            }

            if (stream.Length > int.MaxValue)
            {
                return OperationResult<T>.Fail("file_too_large", "JSON document is too large to read.");
            }

            byte[] bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            string sha = Convert.ToHexString(SHA256.HashData(bytes));
            try
            {
                using JsonDocument document = JsonDocument.Parse(bytes, ParseOptions);
                return project(document.RootElement, bytes.LongLength, sha);
            }
            catch (JsonException exception)
            {
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
