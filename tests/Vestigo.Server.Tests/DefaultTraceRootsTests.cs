using Microsoft.Extensions.Diagnostics.HealthChecks;

using Vestigo.Configuration;
using Vestigo.Etws;
using Vestigo.Hosting;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

[Collection("VestigoHost")]
public sealed class DefaultTraceRootsTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-default-traces").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");

    public DefaultTraceRootsTests()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);
        Directory.CreateDirectory(VestigoPaths.TraceDirectory);
        Directory.CreateDirectory(VestigoPaths.DefaultSymbolCache);
    }

    [Fact]
    public void PathPolicy_EmptyListsAcceptTheBuiltInTraceFolder()
    {
        string file = Path.Combine(VestigoPaths.TraceDirectory, "trace.etl");
        File.WriteAllText(file, "etl");
        PathPolicy policy = new(new VestigoOptions { MaxInputBytes = 100 });

        PathCheck result = policy.RequireExistingFile(file, [".etl"]);

        Assert.True(result.Success);
        Assert.Equal(Path.GetFullPath(file), result.FullPath);
    }

    [Fact]
    public void PathPolicy_ConfiguredListsReplaceTheDefault()
    {
        string custom = Directory.CreateTempSubdirectory("vestigo-custom-in").FullName;
        string insideDefault = Path.Combine(VestigoPaths.TraceDirectory, "trace.etl");
        File.WriteAllText(insideDefault, "etl");
        PathPolicy policy = new(new VestigoOptions { AllowedInputRoots = [custom] });

        PathCheck result = policy.RequireExistingFile(insideDefault, [".etl"]);

        Assert.Equal("outside_root", result.ErrorCode);
    }

    [Fact]
    public void PathPolicy_AcceptsTracesAliasAndSymbolCachePdbs()
    {
        string tracesFile = Path.Combine(VestigoPaths.TraceDirectory, "nested", "driver.pdb");
        Directory.CreateDirectory(Path.GetDirectoryName(tracesFile)!);
        File.WriteAllText(tracesFile, "pdb");
        string cacheFile = Path.Combine(VestigoPaths.DefaultSymbolCache, "driver.pdb");
        File.WriteAllText(cacheFile, "pdb");
        PathPolicy policy = new(new VestigoOptions { MaxInputBytes = 100 });

        PathCheck alias = policy.RequireExistingFile("traces:nested\\driver.pdb", [".pdb"]);
        PathCheck cache = policy.RequireExistingFile(cacheFile, [".pdb"]);
        PathCheck cacheEtl = policy.RequireExistingFile(
            Path.ChangeExtension(cacheFile, ".etl"),
            [".etl"]);

        Assert.True(alias.Success);
        Assert.Equal(Path.GetFullPath(tracesFile), alias.FullPath);
        Assert.True(cache.Success);
        Assert.Equal("outside_root", cacheEtl.ErrorCode);
    }

    [Fact]
    public void RootPolicy_AddsBuiltInTracesRootUntilOverridden()
    {
        RootPolicy generated = new(new VestigoOptions());
        Assert.Contains(generated.Roots, root =>
            root.Name == "traces" &&
            root.Access.HasFlag(RootAccess.Read) &&
            root.Access.HasFlag(RootAccess.Write) &&
            string.Equals(root.Path, VestigoPaths.TraceDirectory, StringComparison.OrdinalIgnoreCase));

        VestigoOptions custom = new()
        {
            Roots = [new RootDefinition { Name = "traces", Path = @"D:\other", Access = RootAccess.Read }]
        };
        RootPolicy overridden = new(custom);
        Assert.DoesNotContain(
            overridden.Roots,
            root => root.Name == "traces" &&
                    string.Equals(root.Path, VestigoPaths.TraceDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(overridden.Roots, root => root.Name == "traces" && root.Path == @"D:\other");
        Assert.NotNull(custom.GetTracesRootOverrideWarning());

        RootPolicy disabled = new(new VestigoOptions
        {
            Modules = new Dictionary<string, ModuleSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["Trace"] = new() { Enabled = false }
            }
        });
        Assert.DoesNotContain(disabled.Roots, root => root.Name == "traces");
    }

    [Fact]
    public async Task HealthCheck_IsHealthyWithDefaultRoots()
    {
        VestigoHealthCheck check = new(new VestigoOptions());

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void GetTraceSettings_ReportsDefaultsAndRedactsUserInfo()
    {
        VestigoOptions options = new()
        {
            SymbolServer = "https://user:pass@symbols.example/download/symbols",
            AttachableSessions = ["Kernel*"]
        };
        options.Validate();
        SymbolCatalogService symbols = new(options, new PathPolicy(options, new RootPolicy(options)), new UnusedHttp());

        TraceSettingsDescription settings = symbols.GetSettings();

        Assert.True(settings.UsesDefaultRoots);
        Assert.Equal("traces", settings.TracesRootName);
        Assert.Equal(VestigoPaths.TraceDirectory, settings.TracesRootPath);
        Assert.Equal("https://symbols.example/download/symbols", settings.SymbolServer);
        Assert.Equal("setting", settings.SymbolServerSource);
        Assert.Equal("Basic", settings.Auth.Scheme);
        Assert.True(settings.Auth.Configured);
        Assert.DoesNotContain("pass", settings.SymbolServer, StringComparison.Ordinal);
        Assert.Equal(10_000, settings.MaxArchiveEntries);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _previous);
    }

    private sealed class UnusedHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
