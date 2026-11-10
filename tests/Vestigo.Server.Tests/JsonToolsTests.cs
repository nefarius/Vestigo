using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Vestigo.Configuration;
using Vestigo.LogFiles;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

public class JsonToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vestigo-json-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("$.status", "\"pending\"", "$.status")]
    [InlineData("$.items[0].name", "\"device\"", "$.items[0].name")]
    [InlineData("$['dashed-key']", "1", "$['dashed-key']")]
    [InlineData("$['a.b']", "\"dot\"", "$['a.b']")]
    [InlineData("$['a\\'b']", "true", "$['a\\'b']")]
    [InlineData("$.flag", "null", "$.flag")]
    public void Evaluator_SelectsDottedIndexesAndQuotedKeys(string query, string expected, string path)
    {
        using JsonDocument document = JsonDocument.Parse(
            """{"status":"pending","items":[{"name":"device"}],"dashed-key":1,"a.b":"dot","a'b":true,"flag":null}""");

        JsonSelection selection = Select(document.RootElement, query, 10);

        JsonHit hit = Assert.Single(selection.Hits);
        Assert.False(selection.Wildcard);
        Assert.Equal(path, hit.MatchedAt);
        Assert.Equal(expected, hit.Value.GetRawText());
    }

    [Fact]
    public void Evaluator_TrailingWildcardPreservesDocumentOrderAndCapsMatches()
    {
        using JsonDocument document = JsonDocument.Parse("""{"b":1,"a":[2,3]}""");

        JsonSelection all = Select(document.RootElement, "$.*", 10);
        Assert.Equal(["$.b", "$.a"], all.Hits.Select(hit => hit.MatchedAt));
        Assert.False(all.Truncated);

        JsonSelection elements = Select(document.RootElement, "$.a[*]", 10);
        Assert.Equal(["$.a[0]", "$.a[1]"], elements.Hits.Select(hit => hit.MatchedAt));

        JsonSelection capped = Select(document.RootElement, "$.a[*]", 1);
        Assert.True(capped.Truncated);
        Assert.Equal("$.a[0]", Assert.Single(capped.Hits).MatchedAt);
    }

    [Fact]
    public void Evaluator_EmptyWildcardMatchesNothing()
    {
        using JsonDocument document = JsonDocument.Parse("[]");

        JsonSelection selection = Select(document.RootElement, "$[*]", 10);

        Assert.True(selection.Wildcard);
        Assert.Empty(selection.Hits);
        Assert.False(selection.Truncated);
    }

    [Theory]
    [InlineData("$.items[4]")]
    [InlineData("$.missing")]
    [InlineData("$.items.name")]
    public void Evaluator_MissingValueDoesNotMatch(string query)
    {
        using JsonDocument document = JsonDocument.Parse("""{"items":[{"name":"device"}]}""");

        OperationResult<JsonSelection> result = JsonPathEvaluator.Select(document.RootElement, query, 10);

        Assert.Equal("query_no_match", result.ErrorCode);
    }

    [Theory]
    [InlineData("$.items[?(@.name=='device')]")]
    [InlineData("$..name")]
    [InlineData("$.items[0:1]")]
    [InlineData("$.items[*].name")]
    [InlineData("name")]
    public void Evaluator_RejectsUnsupportedSyntax(string query)
    {
        using JsonDocument document = JsonDocument.Parse("""{"items":[{"name":"device"}]}""");

        OperationResult<JsonSelection> result = JsonPathEvaluator.Select(document.RootElement, query, 10);

        Assert.Equal("invalid_query", result.ErrorCode);
        Assert.Contains(JsonPathEvaluator.SupportedSubset, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteOffset_UsesLineAndByteInLine()
    {
        JsonException exception = new("bad", path: null, lineNumber: 1, bytePositionInLine: 1);

        Assert.Equal(4, JsonTools.ByteOffset("ab\ncd"u8, exception));
    }

    [Fact]
    public void JsonTools_ReadAndSummarizeSnapshot()
    {
        const string json = """{"status":"pending","count":2,"flag":true,"empty":null,"items":[1]}""";
        Write("state.json", json);
        JsonTools tools = Tools();

        OperationResult<JsonQueryResult> read = tools.ReadJsonPath("logs:state.json", "$.items[*]");
        OperationResult<JsonSummary> summary = tools.GetJsonSummary("logs:state.json");

        Assert.True(read.Success, read.Error);
        Assert.Equal(["$.items[0]"], read.Value!.Matches.Select(match => match.MatchedAt));
        Assert.Equal("number", read.Value.Matches[0].Type);
        Assert.Equal("1", read.Value.Matches[0].Value!.ToJsonString());
        Assert.False(read.Value.Matches[0].ValueOmitted);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), read.Value.Sha256);
        Assert.Equal(["status", "count", "flag", "empty", "items"], read.Value.TopLevelKeys);

        Assert.True(summary.Success, summary.Error);
        Assert.Equal("object", summary.Value!.RootType);
        Assert.Equal(5, summary.Value.MemberCount);
        Assert.Equal(
            [("status", "string"), ("count", "number"), ("flag", "boolean"), ("empty", "null"), ("items", "array")],
            summary.Value.Members.Select(member => (member.Name, member.Type)));
    }

    [Fact]
    public void JsonTools_SummarizeArrayAndScalar()
    {
        Write("list.json", "[1,2,3]");
        Write("flag.json", "true");
        JsonTools tools = Tools();

        JsonSummary list = tools.GetJsonSummary("logs:list.json").Value!;
        JsonSummary flag = tools.GetJsonSummary("logs:flag.json").Value!;

        Assert.Equal("array", list.RootType);
        Assert.Equal(3, list.Length);
        Assert.Empty(list.Members);
        Assert.Equal("boolean", flag.RootType);
        Assert.Null(flag.Length);
        Assert.Null(flag.MemberCount);
    }

    [Fact]
    public void JsonTools_SingleOversizedValueFailsWithoutReturningIt()
    {
        Write("blob.json", """{"name":"abcdef"}""");

        OperationResult<JsonQueryResult> result = Tools(options => options.MaxJsonValueBytes = 4)
            .ReadJsonPath("logs:blob.json", "$");

        Assert.Equal("value_too_large", result.ErrorCode);
        Assert.DoesNotContain("abcdef", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonTools_WildcardOmitsOnlyOversizedValues()
    {
        Write("mixed.json", """{"a":1,"b":"abcdef"}""");

        OperationResult<JsonQueryResult> result = Tools(options => options.MaxJsonValueBytes = 4)
            .ReadJsonPath("logs:mixed.json", "$.*");

        Assert.True(result.Success, result.Error);
        Assert.False(result.Value!.Matches[0].ValueOmitted);
        Assert.Equal("1", result.Value.Matches[0].Value!.ToJsonString());
        Assert.True(result.Value.Matches[1].ValueOmitted);
        Assert.Null(result.Value.Matches[1].Value);
        Assert.Equal("string", result.Value.Matches[1].Type);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void JsonTools_FileLimitIsCheckedBeforeReading(int underBy)
    {
        byte[] bytes = """{"a":1}"""u8.ToArray();
        string file = Path.Combine(_root, "exact.json");
        File.WriteAllBytes(file, bytes);
        JsonTools tools = Tools(options => options.MaxJsonBytes = bytes.Length - underBy);

        if (underBy == 0)
        {
            Assert.True(tools.GetJsonSummary("logs:exact.json").Success);
            return;
        }

        using FileStream locked = new(file, FileMode.Open, FileAccess.Read, FileShare.None);
        OperationResult<JsonSummary> result = tools.GetJsonSummary("logs:exact.json");

        Assert.Equal("file_too_large", result.ErrorCode);
    }

    [Fact]
    public void JsonTools_CallerCapCannotRaiseTheConfiguredLimit()
    {
        Write("state.json", """{"a":1}""");
        JsonTools tools = Tools(options => options.MaxJsonBytes = 4);

        Assert.Equal("file_too_large", tools.ReadJsonPath("logs:state.json", "$", maxBytes: 1000).ErrorCode);
        Assert.Equal("invalid_request", tools.ReadJsonPath("logs:state.json", "$", maxBytes: 0).ErrorCode);
    }

    [Fact]
    public void JsonTools_InvalidJsonReportsByteOffsetAndDoesNotThrow()
    {
        Write("broken.json", """{"a":1}x""");

        OperationResult<JsonSummary> result = Tools().GetJsonSummary("logs:broken.json");

        Assert.Equal("invalid_json", result.ErrorCode);
        Assert.Contains("byte ", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("{\"a\":1}", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonTools_UsesRootErrors()
    {
        Write("state.json", "{}");
        Directory.CreateDirectory(Path.Combine(_root, "dir"));
        JsonTools tools = Tools();

        Assert.Equal("not_found", tools.GetJsonSummary("logs:missing.json").ErrorCode);
        Assert.Equal("outside_root", tools.GetJsonSummary(@"logs:..\evil.json").ErrorCode);
        Assert.Equal("not_a_file", tools.GetJsonSummary("logs:dir").ErrorCode);
        Assert.Equal("invalid_query", tools.ReadJsonPath("logs:state.json", "").ErrorCode);
    }

    [Fact]
    public void LogsOptions_RejectNonPositiveJsonLimits() =>
        Assert.Throws<InvalidOperationException>(
            new VestigoOptions { Logs = new LogsOptions { MaxJsonMatches = 0 } }.Validate);

    private JsonTools Tools(Action<LogsOptions>? configure = null)
    {
        LogsOptions logs = new();
        configure?.Invoke(logs);
        VestigoOptions options = new()
        {
            Roots = [new RootDefinition { Name = "logs", Path = _root, Access = RootAccess.Read }],
            Logs = logs
        };
        return new JsonTools(options, new RootPolicy(options));
    }

    private void Write(string name, string json) =>
        File.WriteAllText(Path.Combine(_root, name), json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static JsonSelection Select(JsonElement root, string query, int maxMatches)
    {
        OperationResult<JsonSelection> result = JsonPathEvaluator.Select(root, query, maxMatches);
        Assert.True(result.Success, result.Error);
        return result.Value!;
    }
}
