using System.Net;

using Nefarius.Utilities.ETW.Symbols;

using Vestigo.Configuration;
using Vestigo.Etws;

namespace Vestigo.Server.Tests;

public class SymbolServerAuthTests
{
    [Theory]
    [InlineData(SymbolServerAuthScheme.Header, "X-Api-Key", null, "secret-value")]
    [InlineData(SymbolServerAuthScheme.Bearer, null, null, "token-value")]
    [InlineData(SymbolServerAuthScheme.Basic, null, "user", "pass")]
    public void Handler_SetsTheExpectedHeader(SymbolServerAuthScheme scheme, string? header, string? user, string secret)
    {
        VestigoOptions options = ValidAuth(scheme, header, user, secret);
        options.Validate();
        SymbolServerAuthHandler handler = new(options);
        HttpRequestMessage request = new(HttpMethod.Get, "https://symbols.example/download/symbols/a.pdb");

        handler.ApplyCredentials(request);

        if (scheme == SymbolServerAuthScheme.Header)
        {
            Assert.Equal(secret, request.Headers.GetValues(header!).Single());
        }
        else if (scheme == SymbolServerAuthScheme.Bearer)
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(secret, request.Headers.Authorization.Parameter);
        }
        else
        {
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            Assert.NotEqual(secret, request.Headers.Authorization.Parameter);
        }
    }

    [Fact]
    public void Handler_DoesNotAddCredentialsForAnotherOrigin()
    {
        VestigoOptions options = ValidAuth(SymbolServerAuthScheme.Bearer, null, null, "token-value");
        options.Validate();
        SymbolServerAuthHandler handler = new(options);
        HttpRequestMessage request = new(HttpMethod.Get, "https://blob.example/file");

        handler.ApplyCredentials(request);

        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task Handler_DropsCredentialsAfterCrossOriginRedirect()
    {
        RecordingHandler inner = new(
            new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("https://blob.example/payload") }
            },
            new HttpResponseMessage(HttpStatusCode.OK));
        VestigoOptions options = ValidAuth(SymbolServerAuthScheme.Header, "X-Api-Key", null, "secret-value");
        options.Validate();
        using SymbolServerAuthHandler handler = new(options) { InnerHandler = inner };
        using HttpMessageInvoker invoker = new(handler);

        HttpResponseMessage response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://symbols.example/download/symbols/a.pdb"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Requests.Count);
        Assert.Contains("secret-value", inner.Requests[0].Headers.GetValues("X-Api-Key"));
        Assert.False(inner.Requests[1].Headers.Contains("X-Api-Key"));
    }

    [Fact]
    public async Task Handler_KeepsCredentialsOnSameOriginRedirect()
    {
        RecordingHandler inner = new(
            new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new Uri("https://symbols.example/download/symbols/other.pdb") }
            },
            new HttpResponseMessage(HttpStatusCode.OK));
        VestigoOptions options = ValidAuth(SymbolServerAuthScheme.Bearer, null, null, "token-value");
        options.Validate();
        using SymbolServerAuthHandler handler = new(options) { InnerHandler = inner };
        using HttpMessageInvoker invoker = new(handler);

        await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://symbols.example/download/symbols/a.pdb"),
            CancellationToken.None);

        Assert.All(inner.Requests, request => Assert.Equal("token-value", request.Headers.Authorization!.Parameter));
    }

    [Fact]
    public async Task Handler_StopsAfterTheRedirectLimit()
    {
        HttpResponseMessage[] hops = Enumerable.Range(0, 8)
            .Select(_ => new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("https://symbols.example/download/symbols/next") }
            })
            .ToArray();
        RecordingHandler inner = new(hops);
        VestigoOptions options = ValidAuth(SymbolServerAuthScheme.Bearer, null, null, "token-value");
        options.Validate();
        using SymbolServerAuthHandler handler = new(options) { InnerHandler = inner };
        using HttpMessageInvoker invoker = new(handler);

        HttpResponseMessage response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://symbols.example/download/symbols/a.pdb"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(SymbolServerAuthHandler.MaxRedirects + 1, inner.Requests.Count);
    }

    [Fact]
    public void Options_ConvertUserinfoAndRejectConflicts()
    {
        VestigoOptions converted = new() { SymbolServer = "https://user:pass@symbols.example/symbols" };
        converted.Validate();
        Assert.Equal("https://symbols.example/symbols", converted.SymbolServer);
        Assert.Equal(SymbolServerAuthScheme.Basic, converted.SymbolServerAuth.Scheme);
        Assert.Equal("user", converted.SymbolServerAuth.Username);
        Assert.Equal("pass", converted.SymbolServerAuth.ResolvedSecret);

        VestigoOptions conflict = ValidAuth(SymbolServerAuthScheme.Bearer, null, null, "token");
        conflict.SymbolServer = "https://user:pass@symbols.example/symbols";
        Assert.Throws<InvalidOperationException>(conflict.Validate);

        Assert.Throws<InvalidOperationException>(() =>
            ValidAuth(SymbolServerAuthScheme.Header, null, null, "x").Also(o => o.Validate()));
        Assert.Throws<InvalidOperationException>(() =>
            ValidAuth(SymbolServerAuthScheme.Basic, null, null, "x").Also(o => o.Validate()));
        Assert.Throws<InvalidOperationException>(() =>
        {
            VestigoOptions both = ValidAuth(SymbolServerAuthScheme.Bearer, null, null, "x");
            both.SymbolServerAuth.SecretFile = @"D:\secrets\token";
            both.Validate();
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            VestigoOptions http = ValidAuth(SymbolServerAuthScheme.Bearer, null, null, "x");
            http.SymbolServer = "http://symbols.example/symbols";
            http.Validate();
        });
        VestigoOptions loopback = ValidAuth(SymbolServerAuthScheme.Bearer, null, null, "x");
        loopback.SymbolServer = "http://127.0.0.1:8080/symbols";
        loopback.Validate();

        VestigoOptions query = new() { SymbolServer = "https://symbols.example/symbols?apiKey=hidden" };
        query.Validate();
        Assert.Contains("query string", query.GetSymbolServerQueryWarning(), StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", query.GetSymbolServerQueryWarning()!, StringComparison.Ordinal);
    }

    [Fact]
    public void LooksLikeAuthStatus_Maps401()
    {
        Assert.True(SymbolCatalogService.LooksLikeAuthStatus("Response status code does not indicate success: 401 (Unauthorized)."));
        Assert.True(SymbolCatalogService.LooksLikeAuthStatus("403 Forbidden"));
        Assert.True(SymbolCatalogService.IsUnauthorized(PdbResolutionFailure.DownloadFailed, "401 Unauthorized"));
        Assert.True(SymbolCatalogService.LooksLikeAuthStatus("Unauthorized"));
        Assert.True(SymbolCatalogService.LooksLikeAuthStatus("Forbidden"));
        Assert.False(SymbolCatalogService.LooksLikeAuthStatus("404 Not Found"));
        Assert.False(SymbolCatalogService.LooksLikeAuthStatus("file401.pdb"));
        Assert.False(SymbolCatalogService.LooksLikeAuthStatus("x403y"));
        Assert.False(SymbolCatalogService.LooksLikeAuthStatus("error_1401"));
        Assert.False(SymbolCatalogService.LooksLikeAuthStatus("Pdb403Name"));
    }

    private static VestigoOptions ValidAuth(
        SymbolServerAuthScheme scheme,
        string? header,
        string? user,
        string secret) =>
        new()
        {
            SymbolServer = "https://symbols.example/download/symbols",
            SymbolServerAuth =
            {
                Scheme = scheme,
                HeaderName = header,
                Username = user,
                Secret = secret
            }
        };

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(Clone(request));
            HttpResponseMessage response = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return Task.FromResult(response);
        }

        private static HttpRequestMessage Clone(HttpRequestMessage request)
        {
            HttpRequestMessage copy = new(request.Method, request.RequestUri);
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return copy;
        }
    }
}

file static class OptionsExtensions
{
    public static VestigoOptions Also(this VestigoOptions options, Action<VestigoOptions> action)
    {
        action(options);
        return options;
    }
}
