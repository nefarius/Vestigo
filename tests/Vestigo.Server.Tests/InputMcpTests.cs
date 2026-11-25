using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Vestigo.Configuration;
using Vestigo.Input;
using Vestigo.Jobs;
using Vestigo.Processes;

namespace Vestigo.Server.Tests;

[Collection("VestigoHost")]
public class InputMcpTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-input-mcp").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");
    private readonly QueueLauncher _launcher = new();

    public InputMcpTests()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _previous);
        try
        {
            Directory.Delete(_settings, recursive: true);
        }
        catch (IOException)
        {
            // The test host can still be releasing a file in the temp tree.
        }
    }

    [Fact]
    public async Task SendInput_ListsRequireFocusAndSerializesOutcomes()
    {
        await using WebApplicationFactory<Program> factory = Create();
        using HttpClient client = factory.CreateClient();

        JsonElement listed = await Rpc(client, "tools/list", new { });
        JsonElement tool = listed.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(candidate => candidate.GetProperty("name").GetString() == "send_input");
        JsonElement requireFocus = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("requireFocus");
        Assert.Equal("boolean", requireFocus.GetProperty("type").GetString());
        Assert.False(requireFocus.GetProperty("default").GetBoolean());
        Assert.Contains("Default false", requireFocus.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("qpcTicks", tool.GetProperty("description").GetString(), StringComparison.Ordinal);

        _launcher.NextSendJson = JsonSerializer.Serialize(Submitted(), SessionHelper.Json);
        JsonElement omitted = await Call(client, new { actions = new[] { new { type = "key", keys = "a" } } });
        AssertTiming(omitted, "9007199254740997");
        Assert.False(DecodedBatch(_launcher.LastSend!).GetProperty("requireFocus").GetBoolean());

        _launcher.NextSendJson = JsonSerializer.Serialize(Submitted(), SessionHelper.Json);
        JsonElement explicitFalse = await Call(client, new { actions = new[] { new { type = "key", keys = "a" } }, focusWindow = "0xA1", requireFocus = false });
        Assert.Equal(InputInjectionStatus.Submitted, Injection(explicitFalse).GetProperty("status").GetString());
        Assert.False(DecodedBatch(_launcher.LastSend!).GetProperty("requireFocus").GetBoolean());

        _launcher.NextSendJson = JsonSerializer.Serialize(Rejected(), SessionHelper.Json);
        JsonElement rejected = await Call(client, new { actions = new[] { new { type = "key", keys = "a" } }, focusWindow = "0xA1", requireFocus = true });
        Assert.False(rejected.TryGetProperty("isError", out JsonElement rejectedError) && rejectedError.GetBoolean());
        Assert.Equal(InputInjectionStatus.Rejected, Injection(rejected).GetProperty("status").GetString());
        Assert.Equal(0, Injection(rejected).GetProperty("submittedEvents").GetInt32());
        Assert.Equal("0xA1", Injection(rejected).GetProperty("focusRequested").GetString());
        Assert.Equal("0xB2", Injection(rejected).GetProperty("focusActual").GetString());
        Assert.False(Injection(rejected).TryGetProperty("timing", out _));
        Assert.True(DecodedBatch(_launcher.LastSend!).GetProperty("requireFocus").GetBoolean());

        _launcher.NextSendJson = JsonSerializer.Serialize(Partial(), SessionHelper.Json);
        JsonElement partial = await Call(client, new { actions = new[] { new { type = "key", keys = "a", holdMilliseconds = 40 } }, focusWindow = "0xA1", requireFocus = true });
        Assert.False(partial.TryGetProperty("isError", out JsonElement partialError) && partialError.GetBoolean());
        JsonElement partialInjection = Injection(partial);
        Assert.Equal(InputInjectionStatus.Partial, partialInjection.GetProperty("status").GetString());
        Assert.Equal(InputStopCodes.FocusLost, partialInjection.GetProperty("stopCode").GetString());
        Assert.Equal(1, partialInjection.GetProperty("cleanupReleasedEvents").GetInt32());
        Assert.Equal(JsonValueKind.String, partialInjection.GetProperty("timing").GetProperty("firstSubmitted").GetProperty("qpcTicks").ValueKind);
        Assert.Equal("9007199254740997", partialInjection.GetProperty("timing").GetProperty("firstSubmitted").GetProperty("qpcTicks").GetString());
        Assert.True(partialInjection.GetProperty("timing").GetProperty("includesCleanupReleases").GetBoolean());
    }

    private WebApplicationFactory<Program> Create()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        string json = JsonSerializer.Serialize(new
        {
            Vestigo = new
            {
                ListenAddress = "127.0.0.1",
                ListenPort = port,
                ApiTokens = Array.Empty<string>(),
                Modules = new { Desktop = new { Enabled = true }, Input = new { Enabled = true } },
                Input = new { AllowInput = true },
                Desktop = new { AllowScreenshots = true }
            }
        });
        File.WriteAllText(Path.Combine(_settings, "appsettings.json"), json);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IProcessLauncher>();
                services.AddSingleton<IProcessLauncher>(_launcher);
            });
        });
    }

    private static InputResult Submitted() => new(
        1,
        3,
        null,
        2,
        null,
        "0xA1",
        null,
        4,
        5,
        Timing: Timing(false));

    private static InputResult Rejected() => new(
        0,
        3,
        null,
        0,
        "0xA1",
        "0xB2",
        false,
        Status: InputInjectionStatus.Rejected,
        StopCode: InputStopCodes.FocusNotGranted,
        StopReason: "The requested window was not the foreground window immediately before input submission.",
        StoppedAtActionIndex: 0,
        StoppedAtActionKind: InputKinds.Key);

    private static InputResult Partial() => new(
        0,
        3,
        null,
        2,
        "0xA1",
        "0xB2",
        false,
        Status: InputInjectionStatus.Partial,
        StopCode: InputStopCodes.FocusLost,
        StopReason: "The requested window was no longer the foreground window. Later actions were not submitted.",
        StoppedAtActionIndex: 0,
        StoppedAtActionKind: InputKinds.Key,
        CleanupReleasedEvents: 1,
        Timing: Timing(true));

    private static InputTiming Timing(bool cleanup) => new(
        InputTiming.QueryPerformanceCounter,
        "10000000",
        cleanup,
        new InputTimestamp(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), "9007199254740997"),
        new InputTimestamp(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(40), "9007199254740999"));

    private static async Task<JsonElement> Call(HttpClient client, object arguments)
    {
        JsonElement response = await Rpc(client, "tools/call", new { name = "send_input", arguments });
        Assert.False(response.TryGetProperty("error", out JsonElement error), error.ToString());
        return response.GetProperty("result");
    }

    private static JsonElement Injection(JsonElement result)
    {
        string text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.GetProperty("injection").Clone();
    }

    private static void AssertTiming(JsonElement result, string ticks)
    {
        JsonElement timing = Injection(result).GetProperty("timing").GetProperty("firstSubmitted").GetProperty("qpcTicks");
        Assert.Equal(JsonValueKind.String, timing.ValueKind);
        Assert.Equal(ticks, timing.GetString());
    }

    private static JsonElement DecodedBatch(string arguments)
    {
        string encoded = arguments.Split('\n').Last(line => line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal));
        using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> Rpc(HttpClient client, string method, object parameters)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/mcp");
        request.Headers.Add("Accept", "application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", method);
        JsonObject payload = JsonSerializer.SerializeToNode(parameters)!.AsObject();
        if (payload["name"] is JsonValue name)
        {
            request.Headers.Add("Mcp-Name", name.GetValue<string>());
        }

        JsonObject metadata = payload["_meta"]?.AsObject() ?? new JsonObject();
        metadata["io.modelcontextprotocol/protocolVersion"] = "2026-07-28";
        metadata["io.modelcontextprotocol/clientCapabilities"] = new JsonObject();
        metadata["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "input-tests", ["version"] = "1.0" };
        payload["_meta"] = metadata;
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = payload }),
            Encoding.UTF8,
            "application/json");
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, content);
        if (!content.TrimStart().StartsWith('{'))
        {
            content = string.Join(
                "\n",
                content.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].Trim()));
        }

        using JsonDocument json = JsonDocument.Parse(content);
        return json.RootElement.Clone();
    }

    private sealed class QueueLauncher : IProcessLauncher
    {
        public string? NextSendJson { get; set; }

        public string? LastSend { get; private set; }

        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) => OperationResult<bool>.Ok(true);

        public Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken)
        {
            string arguments = string.Join('\n', request.Arguments);
            if (request.Arguments.Contains(SessionHelper.SendInputCommand))
            {
                LastSend = arguments;
                File.WriteAllText(output.OutputPath, NextSendJson ?? "{}");
                return Task.FromResult(0);
            }

            File.WriteAllText(output.OutputPath, """{"error":"not used","errorCode":"helper_failed"}""");
            return Task.FromResult(2);
        }
    }
}
