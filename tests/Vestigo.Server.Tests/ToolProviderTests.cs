using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;

using Vestigo.Configuration;
using Vestigo.Extensibility;
using Vestigo.Plugins;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

public sealed class ToolProviderTests
{
    private const string Session = "session_1234567890abcdef";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static HttpContextAccessor Http(string? owner = "owner-a", string? session = Session)
    {
        DefaultHttpContext context = new();
        if (owner is not null) context.User = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner)], "test"));
        if (session is not null) context.Request.Headers[PluginSessionIdentity.HeaderName] = session;
        return new() { HttpContext = context };
    }

    private static PluginToolRegistry Registry(FakeProvider provider, HttpContextAccessor? http = null, IReadOnlySet<string>? staticNames = null) =>
        new([provider], new(["module 'Fixture'"], staticNames ?? new HashSet<string>()), new(http ?? Http()));

    [Fact]
    public void Api11_StillAcceptsApi10AndRejectsFutureMinorOrMajor()
    {
        Assert.Equal(new Version(1, 1), PluginApi.Version);
        Assert.True(PluginApi.IsCompatible(new(1, 0)));
        Assert.True(PluginApi.IsCompatible(new(1, 1)));
        Assert.False(PluginApi.IsCompatible(new(1, 2)));
        Assert.False(PluginApi.IsCompatible(new(2, 0)));
    }

    [Fact]
    public void Owners_AreCredentialSpecificOpaqueAndRestartScoped()
    {
        VestigoOptions options = new() { ApiTokens = ["first-secret", "second-secret", "first-secret"] };
        ApiTokenOwners owners = new(options);
        string first = owners.GetVerifiedOwner("first-secret");
        Assert.Equal(first, owners.GetVerifiedOwner("first-secret"));
        Assert.NotEqual(first, owners.GetVerifiedOwner("second-secret"));
        Assert.NotEqual(first, new ApiTokenOwners(options).GetVerifiedOwner("first-secret"));
        Assert.DoesNotContain("secret", first);
        Assert.Matches("\\A[0-9a-f]{32}\\z", first);
    }

    [Fact]
    public void Identity_UsesOwnerAndHeaderOrMetadataAndNeverClientClaims()
    {
        HttpContextAccessor http = Http();
        PluginRequestIdentity resolver = new(http);
        PluginToolContext context = resolver.Resolve(null);
        Assert.Equal(new PluginSessionIdentity("owner-a", Session), context.SessionIdentity);
        http.HttpContext!.Request.Headers.Remove(PluginSessionIdentity.HeaderName);
        Assert.Equal(context, resolver.Resolve(new JsonObject { [PluginSessionIdentity.MetadataKey] = Session }));
        http.HttpContext.User = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner-b")], "test"));
        Assert.NotEqual(context.SessionIdentity, resolver.Resolve(new JsonObject { [PluginSessionIdentity.MetadataKey] = Session }).SessionIdentity);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("session_1234567890abcdef\n")]
    [InlineData("session_1234567890abc/def")]
    public void Identity_RejectsMalformedHeader(string value)
    {
        Assert.Contains("session_identity_invalid", Assert.Throws<McpProtocolException>(() => new PluginRequestIdentity(Http(session: value)).Resolve(null)).Message);
    }

    [Fact]
    public void Identity_RejectsUnauthenticatedConflictingRepeatedOrNonStringIds()
    {
        Assert.Throws<McpProtocolException>(() => new PluginRequestIdentity(Http(owner: null)).Resolve(null));
        Assert.Throws<McpProtocolException>(() => new PluginRequestIdentity(Http()).Resolve(new JsonObject { [PluginSessionIdentity.MetadataKey] = "different_session_123456" }));
        HttpContextAccessor repeated = Http();
        repeated.HttpContext!.Request.Headers[PluginSessionIdentity.HeaderName] = new Microsoft.Extensions.Primitives.StringValues([Session, Session]);
        Assert.Throws<McpProtocolException>(() => new PluginRequestIdentity(repeated).Resolve(null));
        Assert.Throws<McpProtocolException>(() => new PluginRequestIdentity(Http(session: null)).Resolve(new JsonObject { [PluginSessionIdentity.MetadataKey] = 3 }));
        Assert.Null(new PluginRequestIdentity(Http(owner: null, session: null)).Resolve(null).SessionIdentity);
    }

    [Theory]
    [InlineData("Bad_")]
    [InlineData("missing_separator")]
    [InlineData("bad_\n")]
    public void Registration_RejectsInvalidPrefixes(string prefix)
    {
        Assert.Contains("provider_registration_invalid", Assert.Throws<InvalidOperationException>(() => Registry(new(prefix))).Message);
    }

    [Fact]
    public void Registration_RejectsStaticCollisionsDuplicateAndOverlappingNamespaces()
    {
        Assert.Throws<InvalidOperationException>(() => Registry(new("list_"), staticNames: new HashSet<string> { "list_jobs" }));
        foreach (string second in new[] { "fixture_", "fixture_nested_" })
            Assert.Throws<InvalidOperationException>(() => new PluginToolRegistry([new FakeProvider(), new FakeProvider(second)],
                new(["one", "two"], new HashSet<string>()), new(Http())));
    }

    [Fact]
    public async Task Catalog_PreservesSchemasMetadataAndDoesNotMutateProviderDescriptors()
    {
        FakeProvider provider = new();
        ListToolsResult result = await Registry(provider).ListAsync(new(), Cancellation);
        Tool tool = Assert.Single(result.Tools);
        Assert.Equal("fixture_echo", tool.Name);
        Assert.Equal("echo", provider.Tools[0].Name);
        JsonObject expected = JsonSerializer.SerializeToNode(provider.Tools[0])!.AsObject();
        expected["name"] = "fixture_echo";
        Assert.True(JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(tool)));
    }

    [Fact]
    public async Task Call_UnprefixesOnlyNameStripsHostMetadataAndPreservesFullResult()
    {
        FakeProvider provider = new();
        CallToolRequestParams request = JsonSerializer.Deserialize<CallToolRequestParams>("""
            {"name":"fixture_echo","arguments":{"value":{"nested":[1,"two",null]}},"_meta":{"vestigo/sessionId":"session_1234567890abcdef","other":"retained"}}
            """)!;
        Assert.Same(provider.Result, await Registry(provider).CallAsync(request, Cancellation));
        Assert.Equal("echo", provider.Received!.Name);
        Assert.Equal(request.Arguments!["value"].GetRawText(), provider.Received.Arguments!["value"].GetRawText());
        Assert.False(provider.Received.Meta!.ContainsKey(PluginSessionIdentity.MetadataKey));
        Assert.Equal("retained", provider.Received.Meta["other"]!.GetValue<string>());
        Assert.Equal("fixture_echo", request.Name);
        Assert.True(request.Meta!.ContainsKey(PluginSessionIdentity.MetadataKey));
        Assert.Equal(new PluginSessionIdentity("owner-a", Session), provider.Context!.SessionIdentity);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SessionRequired_HidesToolsAndRejectsCallsButNotOptionalProviders(bool authenticated)
    {
        FakeProvider provider = new() { RequiresSessionIdentity = true };
        PluginToolRegistry registry = Registry(provider, Http(owner: authenticated ? "owner-a" : null, session: null));
        Assert.Empty((await registry.ListAsync(new(), Cancellation)).Tools);
        Assert.Contains("session_identity_required", (await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await registry.CallAsync(new() { Name = "fixture_echo" }, Cancellation))).Message);
        provider = new();
        Assert.Single((await Registry(provider, Http(owner: null, session: null)).ListAsync(new(), Cancellation)).Tools);
    }

    [Fact]
    public async Task CatalogRefresh_RejectsRemovedToolsAndAcceptsNewNames()
    {
        FakeProvider provider = new();
        PluginToolRegistry registry = Registry(provider);
        Assert.Single((await registry.ListAsync(new(), Cancellation)).Tools);
        provider.Tools[0].Name = "changed";
        Assert.Equal("fixture_changed", Assert.Single((await registry.ListAsync(new(), Cancellation)).Tools).Name);
        await Assert.ThrowsAsync<McpProtocolException>(async () => await registry.CallAsync(new() { Name = "fixture_echo" }, Cancellation));
        Assert.Null(provider.Received);
    }

    [Fact]
    public async Task Catalog_RejectsDuplicatesAndProviderFailuresWithoutLeakingSecrets()
    {
        FakeProvider provider = new();
        provider.Tools.Add(provider.Tools[0]);
        Assert.Contains("provider_catalog_invalid", (await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await Registry(provider).ListAsync(new(), Cancellation))).Message);
        provider = new() { ThrowOnList = true };
        McpProtocolException error = await Assert.ThrowsAsync<McpProtocolException>(async () => await Registry(provider).ListAsync(new(), Cancellation));
        Assert.DoesNotContain("secret", error.ToString());
        provider.ThrowOnList = false;
        provider.ThrowOnCall = true;
        error = await Assert.ThrowsAsync<McpProtocolException>(async () => await Registry(provider).CallAsync(new() { Name = "fixture_echo" }, Cancellation));
        Assert.Contains("provider_call_failed", error.Message);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public async Task Cancellation_IsPassedThroughWithoutWrappingOrRetry()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Registry(new()).ListAsync(new(), cancellation.Token));
    }

    private sealed class FakeProvider(string prefix = "fixture_") : IVestigoToolProvider
    {
        public string Prefix => prefix;
        public bool RequiresSessionIdentity { get; init; }
        public List<Tool> Tools { get; } = [JsonSerializer.Deserialize<Tool>("""
            {"name":"echo","title":"Title","inputSchema":{"type":"object","properties":{"value":{}}},"outputSchema":{"type":"object"},"annotations":{"readOnlyHint":true},"_meta":{"keep":true}}
            """)!];
        public CallToolResult Result { get; } = JsonSerializer.Deserialize<CallToolResult>("""
            {"content":[{"type":"text","text":"error detail"},{"type":"image","data":"AA==","mimeType":"image/png"}],"structuredContent":{"keep":true},"isError":true,"_meta":{"keep":"result"}}
            """)!;
        public bool ThrowOnList { get; set; }
        public bool ThrowOnCall { get; set; }
        public CallToolRequestParams? Received { get; private set; }
        public PluginToolContext? Context { get; private set; }
        public ValueTask<IReadOnlyList<Tool>> ListToolsAsync(PluginToolContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnList) throw new InvalidOperationException("plugin-secret");
            return ValueTask.FromResult<IReadOnlyList<Tool>>(Tools);
        }
        public ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams request, PluginToolContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnCall) throw new InvalidOperationException("plugin-secret");
            Received = request;
            Context = context;
            return ValueTask.FromResult(Result);
        }
    }
}
