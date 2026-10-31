using Microsoft.Extensions.Logging.Abstractions;

using Vestigo.Configuration;
using Vestigo.Etws;
using Vestigo.Security;
using Vestigo.Tools;

namespace Vestigo.Server.Tests;

[Collection("VestigoHost")] // creates a CaptureManager, which uses the machine-wide owned-sessions.json
public class ToolAndSymbolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vestigo-tools").FullName;
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-tool-settings").FullName;

    public ToolAndSymbolTests()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);
        Directory.CreateDirectory(VestigoPaths.SpoolDirectory);
    }

    [Fact]
    public void SymbolStore_UsesExplicitServerAndCache()
    {
        string cache = Path.Combine(_root, "cache");
        SymbolCatalogService symbols = CreateSymbols(new VestigoOptions
        {
            AllowedInputRoots = [_root],
            SymbolServer = "https://symbols.example/download/symbols",
            SymbolCache = cache
        });

        Nefarius.Utilities.ETW.Symbols.SymbolStoreConfiguration store = symbols.CreateStore();

        Assert.Equal("https://symbols.example/download/symbols", store.ServerUrl);
        Assert.Equal(cache, store.CacheDirectory);
        Assert.Contains(_root, store.SearchPaths);
    }

    [Fact]
    public void ValidateDecodingSources_LoadsTmfFixture()
    {
        string tmf = CopyFixture("sample.tmf");
        SymbolCatalogService symbols = CreateSymbols(new VestigoOptions { AllowedInputRoots = [_root] });

        OperationResult<SymbolLoadDescription> loaded = symbols.ValidateSources([tmf]);

        Assert.True(loaded.Success);
        Assert.True(loaded.Value!.SourceCount >= 1);
    }

    [Fact]
    public async Task ConvertEtl_RejectsBadFormatAndRequiresWindowsForFixtures()
    {
        string etl = CopyFixture("BthPS3_0.etl");
        string output = Path.Combine(_root, "events.ndjson");
        OfflineConversionService offline = new(
            new VestigoOptions { AllowedInputRoots = [_root], AllowedOutputRoots = [_root] },
            new PathPolicy(new VestigoOptions { AllowedInputRoots = [_root], AllowedOutputRoots = [_root] }),
            CreateSymbols(new VestigoOptions { AllowedInputRoots = [_root] }));

        OperationResult<ConversionSummary> format = await offline.ConvertAsync(
            [etl], output, "yaml", [], false, false, CancellationToken.None);
        OperationResult<ConversionSummary> converted = await offline.ConvertAsync(
            [etl], output, "ndjson", [CopyFixture("sample.tmf")], false, false, CancellationToken.None);

        Assert.Equal("invalid_request", format.ErrorCode);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(converted.Success);
            Assert.True(converted.Value!.EventCount > 0);
            Assert.True(File.Exists(output));
        }
        else
        {
            Assert.Equal("windows_required", converted.ErrorCode);
        }
    }

    [Fact]
    public void ScanEtl_ChecksPathsBeforeWindows()
    {
        SymbolCatalogService symbols = CreateSymbols(new VestigoOptions { AllowedInputRoots = [_root] });

        OperationResult<IReadOnlyList<PdbReferenceDescription>> missing = symbols.ScanEtl([Path.Combine(_root, "missing.etl")]);
        OperationResult<IReadOnlyList<PdbReferenceDescription>> present = symbols.ScanEtl([CopyFixture("BthPS3_0.etl")]);

        Assert.Equal("not_found", missing.ErrorCode);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("windows_required", present.ErrorCode);
        }
        else
        {
            Assert.True(present.Success);
            Assert.NotEmpty(present.Value!);
        }
    }

    [Fact]
    public async Task StartCapture_RejectsMalformedArguments()
    {
        TraceTools tools = new(
            CreateSymbols(new VestigoOptions { AllowedInputRoots = [_root] }),
            new OfflineConversionService(
                new VestigoOptions(),
                new PathPolicy(new VestigoOptions()),
                CreateSymbols(new VestigoOptions())),
            new CaptureManager(
                new VestigoOptions(),
                CreateSymbols(new VestigoOptions()),
                new EtwRealtimeTraceSessionFactory(),
                NullLogger<CaptureManager>.Instance),
            new EtwRealtimeTraceSessionFactory());

        OperationResult<CaptureStatus> guid = await tools.StartCapture(providerIds: ["not-a-guid"]);
        OperationResult<CaptureStatus> duration = await tools.StartCapture(
            providerIds: ["11111111-1111-1111-1111-111111111111"],
            duration: "later");
        OperationResult<IReadOnlyList<string>> sessions = tools.ListEtwSessions();

        Assert.Equal("invalid_request", guid.ErrorCode);
        Assert.Equal("invalid_request", duration.ErrorCode);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("windows_required", sessions.ErrorCode);
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", null);
    }

    private SymbolCatalogService CreateSymbols(VestigoOptions options) =>
        new(options, new PathPolicy(options), new UnusedHttpClientFactory());

    private string CopyFixture(string name)
    {
        string source = Path.Combine(RepositoryRoot(), "tests", "fixtures", name);
        string destination = Path.Combine(_root, name);
        File.Copy(source, destination, overwrite: true);
        return destination;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vestigo.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }
}
