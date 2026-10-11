using System.Net;
using System.Net.Sockets;

using Microsoft.AspNetCore.Mvc.Testing;

using Vestigo.Configuration;
using Vestigo.Tools;

namespace Vestigo.Server.Tests;

[Collection("VestigoHost")]
public class HostTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-host").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");

    public HostTests()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);
    }

    [Fact]
    public async Task Health_IsAnonymousWhenTokensAreConfigured()
    {
        await using VestigoApplication application = CreateApplication(["secret-token"]);
        using HttpClient client = application.CreateClient();

        HttpResponseMessage health = await client.GetAsync("/health");
        HttpResponseMessage mcp = await client.PostAsync("/mcp", new StringContent("{}"));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret-token");
        HttpResponseMessage authorized = await client.PostAsync("/mcp", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, authorized.StatusCode);
    }

    [Fact]
    public async Task Mcp_AllowsAnonymousPostWhenNoTokensAreConfigured()
    {
        await using VestigoApplication application = CreateApplication([]);
        using HttpClient client = application.CreateClient();

        HttpResponseMessage mcp = await client.PostAsync("/mcp", new StringContent("{}"));

        Assert.NotEqual(HttpStatusCode.Unauthorized, mcp.StatusCode);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _previous);
    }

    private VestigoApplication CreateApplication(string[] tokens)
    {
        int port = ReservePort();
        string json = $$"""
        {
          "Vestigo": {
            "ListenAddress": "127.0.0.1",
            "ListenPort": {{port}},
            "ApiTokens": [{{string.Join(",", tokens.Select(token => "\"" + token + "\""))}}],
            "AllowedInputRoots": [],
            "AllowedOutputRoots": []
          }
        }
        """;
        File.WriteAllText(Path.Combine(_settings, "appsettings.json"), json);
        return new VestigoApplication();
    }

    private static int ReservePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class VestigoApplication : WebApplicationFactory<Program>;
}

[CollectionDefinition("VestigoHost", DisableParallelization = true)]
public sealed class VestigoHostCollection;

public class RepositoryBoundaryTests
{
    [Fact]
    public void Projects_ConsumeReleasedPackagesInsteadOfSiblingSource()
    {
        string root = FindRepositoryRoot();
        string[] projects = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        Assert.Contains(projects, path => File.ReadAllText(path).Contains("Nefarius.Utilities.ETW", StringComparison.Ordinal));
        foreach (string project in projects)
        {
            string text = File.ReadAllText(project);
            Assert.DoesNotContain("HintPath", text, StringComparison.Ordinal);
            Assert.DoesNotContain("WinDbgSymbolsCachingProxy", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Nefarius.Utilities.ETW.csproj", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Nefarius.Shared.PdbUtils.csproj", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GeneratedToolCatalog_MatchesAttributes()
    {
        string root = FindRepositoryRoot();
        string catalog = File.ReadAllText(Path.Combine(root, "docs", "generated", "mcp-tools.md"));

        Assert.Equal(McpToolCatalog.Markdown(), catalog.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void GeneratedConfiguration_MatchesOptionDefaults()
    {
        string root = FindRepositoryRoot();
        string catalog = File.ReadAllText(Path.Combine(root, "docs", "generated", "configuration.md"));
        string reference = File.ReadAllText(Path.Combine(root, "docs", "configuration.md"));
        string generated = ConfigurationCatalog.Markdown();

        Assert.Equal(generated, catalog.Replace("\r\n", "\n", StringComparison.Ordinal));
        foreach (string name in new[]
                 {
                     "ListenAddress", "ListenPort", "ApiTokens", "AllowedInputRoots", "AllowedOutputRoots",
                     "SymbolServer", "SymbolCache", "MaxInputBytes", "MaxEvents", "MaxOutputBytes",
                     "MaxCaptureDuration", "SpoolRetention", "MaxConcurrentCaptures", "MaxReadBatch",
                     "BufferSizeKb", "FlushTimerSeconds", "ClockResolution"
                 })
        {
            Assert.Contains(name, generated, StringComparison.Ordinal);
            Assert.Contains(name, reference, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MarkdownLinks_ResolveInsideTheRepository()
    {
        string root = FindRepositoryRoot();
        string[] documents =
        [
            Path.Combine(root, "README.md"),
            ..Directory.GetFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
        ];

        foreach (string document in documents)
        {
            string text = File.ReadAllText(document);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"\[[^\]]+\]\(([^)]+)\)"))
            {
                string target = match.Groups[1].Value;
                if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) || target.StartsWith('#'))
                {
                    continue;
                }

                string path = target.Split('#')[0];
                if (path.Length == 0)
                {
                    continue;
                }

                string resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(document)!, path));
                Assert.True(File.Exists(resolved) || Directory.Exists(resolved), $"{document} links to missing {target}");
            }
        }
    }

    [Fact]
    public void InstallerLicense_MatchesRepositoryLicense()
    {
        string root = FindRepositoryRoot();
        string license = File.ReadAllText(Path.Combine(root, "LICENSE"));
        string rtf = File.ReadAllText(Path.Combine(root, "installer", "License.rtf"));

        Assert.Contains("MIT License", rtf, StringComparison.Ordinal);
        Assert.Contains("Benjamin H\\u246?glinger-Stelzer", rtf, StringComparison.Ordinal);
        Assert.Contains("THE SOFTWARE IS PROVIDED \"AS IS\"", license, StringComparison.Ordinal);
        Assert.Contains("THE SOFTWARE IS PROVIDED \"AS IS\"", rtf, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vestigo.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
