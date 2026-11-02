using System.Net;
using System.Net.Sockets;

using Microsoft.AspNetCore.Mvc.Testing;

using Vestigo.Configuration;
using Vestigo.Modules;
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
    public async Task Root_ServesAnonymousLandingPageWhenTokensAreConfigured()
    {
        await using VestigoApplication application = CreateApplication(["secret-token"]);
        using HttpClient client = application.CreateClient();

        HttpResponseMessage root = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Equal("text/html", root.Content.Headers.ContentType?.MediaType);
        string body = await root.Content.ReadAsStringAsync();
        Assert.Contains("/mcp", body, StringComparison.Ordinal);
        Assert.Contains("location.pathname", body, StringComparison.Ordinal);
        Assert.Contains("TLS-terminating proxy", body, StringComparison.Ordinal);
        Assert.Contains("https://github.com/nefarius/Vestigo", body, StringComparison.Ordinal);
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

    /// <summary>Set VESTIGO_REGENERATE_DOCS=1 when running tests to rewrite docs/generated.</summary>
    private static void RegenerateIfRequested(string path, string content)
    {
        if (Environment.GetEnvironmentVariable("VESTIGO_REGENERATE_DOCS") == "1")
        {
            File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
        }
    }

    [Fact]
    public void GeneratedToolCatalog_MatchesAttributes()
    {
        string root = FindRepositoryRoot();
        RegenerateIfRequested(Path.Combine(root, "docs", "generated", "mcp-tools.md"), McpToolCatalog.Markdown());
        string catalog = File.ReadAllText(Path.Combine(root, "docs", "generated", "mcp-tools.md"));

        Assert.Equal(McpToolCatalog.Markdown(), catalog.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void GeneratedConfiguration_MatchesOptionDefaults()
    {
        string root = FindRepositoryRoot();
        RegenerateIfRequested(Path.Combine(root, "docs", "generated", "configuration.md"), ConfigurationCatalog.Markdown());
        string catalog = File.ReadAllText(Path.Combine(root, "docs", "generated", "configuration.md"));
        string reference = File.ReadAllText(Path.Combine(root, "docs", "configuration.md"));
        string generated = ConfigurationCatalog.Markdown();

        Assert.Equal(generated, catalog.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.DoesNotContain("SoftwareOptions", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("defaults (see ", generated, StringComparison.Ordinal);
        foreach (string name in new[]
                 {
                     "ListenAddress", "ListenPort", "ApiTokens", "AllowedInputRoots", "AllowedOutputRoots",
                     "Roots", "VolumeRoots", "VolumeRootAccess", "Modules", "SymbolServer", "SymbolCache",
                     "SymbolServerAuth:Scheme", "SymbolServerAuth:HeaderName", "SymbolServerAuth:Username",
                     "SymbolServerAuth:Secret", "SymbolServerAuth:SecretFile", "MaxArchiveEntries",
                     "MaxInputBytes", "MaxEvents",
                     "MaxOutputBytes", "MaxCaptureDuration", "SpoolRetention", "MaxConcurrentCaptures",
                     "MaxReadBatch", "MaxReadWait", "AttachableSessions", "BufferSizeKb", "FlushTimerSeconds",
                     "ClockResolution", "Software:AllowUninstall", "Software:ProtectedNames",
                     "System:AllowBootConfigurationChanges",
                     "Updates:AllowSelfUpdate",
                     "Software:MaxUninstallDuration", "Devices:NefconPath", "Devices:NefconTimeout",
                     "Services:ProtectedNames", "ScheduledTasks:AllowChanges", "ScheduledTasks:ProtectedPaths", "Processes:AllowPathLookup", "Files:MaxUploadBytes",
                     "DebugOutput:ExecutablePath", "DebugOutput:ExpectedSha256",
                     "DebugOutput:MaxDuration", "DebugOutput:MaxMessages", "DebugOutput:MaxOutputBytes",
                     "DebugOutput:MaxFilterPatterns", "DebugOutput:MaxPatternLength",
                     "Audio:AllowSetDefault", "Audio:MeterSampleMilliseconds",
                     "Desktop:AllowScreenshots", "Desktop:MaxScreenshotBytes"
                 })
        {
            Assert.Contains(name, generated, StringComparison.Ordinal);
            Assert.Contains(name, reference, StringComparison.Ordinal);
        }

        foreach (VestigoModule module in ModuleRegistry.All)
        {
            Assert.Contains(module.Name, reference, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GeneratedToolCatalog_FormatsTypesAndDefaults()
    {
        string generated = McpToolCatalog.Markdown();

        Assert.Contains("| Parameter | Type | Required | Default | Description |", generated, StringComparison.Ordinal);
        Assert.Contains("| `env` | `Dictionary<string, string>` |", generated, StringComparison.Ordinal);
        Assert.Contains("| `waitSeconds` | `double` |", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("Dictionary`2", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("| `Double` |", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownLinks_ResolveInsideTheRepository()
    {
        string root = FindRepositoryRoot();
        string[] documents = Directory.GetFiles(root, "*.md", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        Assert.Contains(documents, path => path.EndsWith($"{Path.DirectorySeparatorChar}server{Path.DirectorySeparatorChar}README.md", StringComparison.OrdinalIgnoreCase));

        foreach (string document in documents)
        {
            string text = File.ReadAllText(document);
            HashSet<string> localSlugs = HeadingSlugs(text);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"\[[^\]]+\]\(([^)]+)\)"))
            {
                string target = match.Groups[1].Value;
                if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string[] parts = target.Split('#', 2);
                string path = parts[0];
                string? fragment = parts.Length > 1 ? parts[1] : null;
                if (path.Length == 0)
                {
                    Assert.True(fragment is not null && localSlugs.Contains(fragment),
                        $"{document} links to missing fragment #{fragment}");
                    continue;
                }

                string resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(document)!, path));
                Assert.True(File.Exists(resolved) || Directory.Exists(resolved), $"{document} links to missing {target}");
                if (fragment is not null && File.Exists(resolved))
                {
                    Assert.True(HeadingSlugs(File.ReadAllText(resolved)).Contains(fragment),
                        $"{document} links to missing fragment {target}");
                }
            }
        }
    }

    private static HashSet<string> HeadingSlugs(string markdown)
    {
        HashSet<string> slugs = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (!line.StartsWith('#'))
            {
                continue;
            }

            string heading = line.TrimStart('#').Trim().Replace("`", "", StringComparison.Ordinal).Replace("*", "", StringComparison.Ordinal);
            string slug = System.Text.RegularExpressions.Regex.Replace(heading.ToLowerInvariant(), @"[^\p{L}\p{N}\s-]", "");
            slug = System.Text.RegularExpressions.Regex.Replace(slug, @"\s+", "-");
            if (slug.Length == 0)
            {
                continue;
            }

            int suffix = seen.GetValueOrDefault(slug);
            string candidate = suffix == 0 ? slug : $"{slug}-{suffix}";
            while (!slugs.Add(candidate))
            {
                suffix++;
                candidate = $"{slug}-{suffix}";
            }

            seen[slug] = suffix == 0 ? 1 : suffix + 1;
        }

        return slugs;
    }

    [Fact]
    public void HeadingSlugs_SuffixesRepeatedHeadings()
    {
        HashSet<string> slugs = HeadingSlugs("## Example\n\n## Example\n\n## Example 1\n\n## Other\n");

        Assert.Contains("example", slugs);
        Assert.Contains("example-1", slugs);
        Assert.Contains("example-1-1", slugs);
        Assert.Contains("other", slugs);
        Assert.DoesNotContain("example-2", slugs);
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
