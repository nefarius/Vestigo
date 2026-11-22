using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Vestigo.SystemInfo;

namespace Vestigo.Server.Tests;

[Collection("VestigoHost")]
public class LogonConsentEndpointTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-logon-host").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");

    public LogonConsentEndpointTests()
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
        }
    }

    [Fact]
    public async Task Get_RendersConsentFormWithSecurityHeadersAndNoPassword()
    {
        await using WebApplicationFactory<Program> factory = Create();
        using HttpClient client = factory.CreateClient();
        LogonConsentLink link = Issue(factory);
        using HttpResponseMessage response = await client.GetAsync(new Uri(link.Url).PathAndQuery);

        string html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Contains("Approve this request", html, StringComparison.Ordinal);
        Assert.Contains("lab", html, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", html, StringComparison.Ordinal);
        Assert.Contains("formnovalidate", html, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-password", html, StringComparison.Ordinal);
        Assert.DoesNotContain("blazor.web.js", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/_blazor", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_RejectsPrefetchAndUnknownTickets()
    {
        await using WebApplicationFactory<Program> factory = Create();
        using HttpClient client = factory.CreateClient();
        LogonConsentLink link = Issue(factory);
        using HttpRequestMessage prefetch = new(HttpMethod.Get, new Uri(link.Url).PathAndQuery);
        prefetch.Headers.TryAddWithoutValidation("Sec-Fetch-Purpose", "prefetch");
        using HttpResponseMessage blocked = await client.SendAsync(prefetch);
        using HttpResponseMessage unknown = await client.GetAsync("/system/logon/consent/logon-deadbeef?ticket=nope");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Contains("unknown or has expired", await unknown.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_ApprovesWithoutEchoingThePassword()
    {
        await using WebApplicationFactory<Program> factory = Create();
        using HttpClient client = factory.CreateClient();
        LogonConsentLink link = Issue(factory);
        string path = new Uri(link.Url).PathAndQuery;
        using HttpResponseMessage form = await client.GetAsync(path);
        string html = await form.Content.ReadAsStringAsync();
        string token = Antiforgery(html);
        string field = AntiforgeryField(html);
        using HttpResponseMessage posted = await client.PostAsync(
            path.Split('?')[0],
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["ticket"] = Ticket(link),
                ["decision"] = "approve",
                ["password"] = "secret-password",
                [field] = token
            }));

        string body = await posted.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.Contains("AutoLogon is enabled", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-password", body, StringComparison.Ordinal);
        FakePlatform platform = (FakePlatform)factory.Services.GetRequiredService<IAutoLogonPlatform>();
        Assert.Equal(1, platform.EnableCalls);
        Assert.Equal("approved", factory.Services.GetRequiredService<LogonConsentStore>().Peek(link.RequestId)!.Status);
    }

    [Fact]
    public async Task Post_ApprovesUnlockWorkstationConsent()
    {
        await using WebApplicationFactory<Program> factory = CreateUnlock();
        using HttpClient client = factory.CreateClient();
        LogonConsentLink link = IssueUnlock(factory);
        string path = new Uri(link.Url).PathAndQuery;
        using HttpResponseMessage form = await client.GetAsync(path);
        string html = await form.Content.ReadAsStringAsync();
        Assert.Contains("Unlock workstation", html, StringComparison.Ordinal);
        string token = Antiforgery(html);
        string field = AntiforgeryField(html);
        using HttpResponseMessage posted = await client.PostAsync(
            path.Split('?')[0],
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["ticket"] = Ticket(link),
                ["decision"] = "approve",
                ["password"] = "secret-password",
                [field] = token
            }));

        string body = await posted.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.Contains("Unlock workstation", body, StringComparison.Ordinal);
        Assert.Contains("workstation should unlock shortly", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-password", body, StringComparison.Ordinal);
        Assert.Equal("approved", factory.Services.GetRequiredService<LogonConsentStore>().Peek(link.RequestId)!.Status);
        FakeDispatcher dispatcher = (FakeDispatcher)factory.Services.GetRequiredService<IInteractiveLogonDispatcher>();
        Assert.Equal(1, dispatcher.DispatchCalls);
    }

    [Fact]
    public async Task MudBlazorCssIsReachableWhileConfigStaysLoopbackOnly()
    {
        DefaultHttpContext remote = new();
        remote.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.4");
        remote.Connection.LocalPort = 5098;
        remote.Request.Host = new HostString("192.0.2.4:5098");
        remote.Request.Scheme = "http";
        remote.Request.Method = "GET";
        remote.Request.Path = "/_content/MudBlazor/MudBlazor.min.css";
        bool cssReached = false;
        await new Vestigo.Hosting.LocalConfigurationAccess(_ =>
        {
            cssReached = true;
            return Task.CompletedTask;
        }).InvokeAsync(remote);
        Assert.True(cssReached);

        DefaultHttpContext post = new();
        post.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.4");
        post.Connection.LocalPort = 5098;
        post.Request.Host = new HostString("192.0.2.4:5098");
        post.Request.Scheme = "http";
        post.Request.Method = "POST";
        post.Request.Path = "/_content/MudBlazor/MudBlazor.min.css";
        await new Vestigo.Hosting.LocalConfigurationAccess(_ => Task.CompletedTask).InvokeAsync(post);
        Assert.Equal(405, post.Response.StatusCode);
    }

    private static LogonConsentLink Issue(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<LogonConsentStore>()
            .Issue(LogonConsentKind.AutoLogonEnable, "lab", null, null, false, "http://127.0.0.1")
            .Value!;

    private static LogonConsentLink IssueUnlock(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<LogonConsentStore>()
            .Issue(LogonConsentKind.UnlockWorkstation, "lab", null, null, false, "http://127.0.0.1")
            .Value!;

    private static string Ticket(LogonConsentLink link)
    {
        string query = new Uri(link.Url).Query.TrimStart('?');
        return Uri.UnescapeDataString(query.Split('&').Single(part => part.StartsWith("ticket=", StringComparison.Ordinal))["ticket=".Length..]);
    }

    private static string Antiforgery(string html)
    {
        Match match = Regex.Match(html, @"name=""__RequestVerificationToken"" value=""([^""]+)""");
        if (!match.Success)
        {
            match = Regex.Match(html, @"name=""([^""]+)"" value=""([^""]+)""");
            foreach (Match item in Regex.Matches(html, @"<input type=""hidden"" name=""([^""]+)"" value=""([^""]+)"""))
            {
                if (!string.Equals(item.Groups[1].Value, "ticket", StringComparison.Ordinal))
                {
                    return item.Groups[2].Value;
                }
            }
        }

        return match.Success ? match.Groups[1].Value : throw new InvalidOperationException(html);
    }

    private static string AntiforgeryField(string html)
    {
        foreach (Match item in Regex.Matches(html, @"<input type=""hidden"" name=""([^""]+)"" value=""([^""]+)"""))
        {
            if (!string.Equals(item.Groups[1].Value, "ticket", StringComparison.Ordinal))
            {
                return item.Groups[1].Value;
            }
        }

        return "__RequestVerificationToken";
    }

    private WebApplicationFactory<Program> Create() => Create(unlock: false);

    private WebApplicationFactory<Program> CreateUnlock() => Create(unlock: true);

    private WebApplicationFactory<Program> Create(bool unlock)
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
                System = new { AllowAutoLogonChanges = true, AllowInteractiveLogon = unlock }
            }
        });
        File.WriteAllText(Path.Combine(_settings, "appsettings.json"), json);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAutoLogonPlatform>();
                services.AddSingleton<IAutoLogonPlatform, FakePlatform>();
                if (unlock)
                {
                    services.RemoveAll<IInteractiveLogonDispatcher>();
                    services.AddSingleton<IInteractiveLogonDispatcher, FakeDispatcher>();
                    services.RemoveAll<ISessionPlatform>();
                    services.AddSingleton<ISessionPlatform, LockedConsole>();
                }
            });
        });
    }

    private sealed class FakePlatform : IAutoLogonPlatform
    {
        public int EnableCalls { get; private set; }

        public AutoLogonState Read() =>
            new(false, null, null, null, false, false, false, null, ["The password is never returned."]);

        public OperationResult<AutoLogonChange> Enable(
            string userName,
            string? domain,
            ReadOnlySpan<char> password,
            int? logonCount,
            bool forceAutoLogon)
        {
            EnableCalls++;
            return OperationResult<AutoLogonChange>.Ok(
                new AutoLogonChange(true, userName, domain, logonCount, forceAutoLogon, ["The password is never returned."]));
        }

        public OperationResult<AutoLogonChange> Disable(bool clearDefaultUser) =>
            OperationResult<AutoLogonChange>.Ok(new AutoLogonChange(false, null, null, null, false, []));

        public OperationResult<bool> VerifyCredentials(string userName, string? domain, ReadOnlySpan<char> password) =>
            OperationResult<bool>.Ok(true);
    }

    private sealed class LockedConsole : ISessionPlatform
    {
        public SessionSnapshot Read() => throw new NotSupportedException();

        public SessionTokenRead ReadToken(uint sessionId) => throw new NotSupportedException();

        public ConsoleLockRead ReadConsoleLock() => ConsoleLockRead.Ok("locked", 1, true, "lab");
    }

    private sealed class FakeDispatcher : IInteractiveLogonDispatcher
    {
        public int DispatchCalls { get; private set; }

        public bool LogonUiConnected => true;

        public bool ProviderInstalled => true;

        public bool ProviderRegistered => true;

        public Task<OperationResult<string>> DispatchAsync(
            string userName,
            string? domain,
            char[] password,
            string? expectedSid,
            bool requireUnlock,
            CancellationToken cancellationToken)
        {
            DispatchCalls++;
            return Task.FromResult(OperationResult<string>.Ok("submitted"));
        }
    }
}
