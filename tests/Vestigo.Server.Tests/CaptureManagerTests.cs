using Microsoft.Extensions.Logging.Abstractions;

using Nefarius.Utilities.ETW.Deserializer.WPP;

using Vestigo.Configuration;
using Vestigo.Etws;
using Vestigo.Security;
using Vestigo.Tools;

namespace Vestigo.Server.Tests;

public class CaptureManagerTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-settings").FullName;
    private readonly string _input = Directory.CreateTempSubdirectory("vestigo-capture-in").FullName;

    public CaptureManagerTests()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);
        Directory.CreateDirectory(VestigoPaths.SpoolDirectory);
    }

    [Fact]
    public async Task StartReadStop_PagesSpooledEventsAndStopsSession()
    {
        FakeSessionFactory factory = new()
        {
            Events = ["{\"Event\":1}", "{\"Event\":2}"]
        };
        CaptureManager manager = CreateManager(factory, out VestigoOptions options);
        options.MaxCaptureDuration = TimeSpan.FromMinutes(5);

        OperationResult<CaptureStatus> started = await manager.StartAsync(
            new StartCaptureRequest([], [], [Guid.Parse("11111111-1111-1111-1111-111111111111")], "Verbose", ulong.MaxValue, 0, null),
            CancellationToken.None);

        Assert.True(started.Success);
        CaptureStatus completed = await WaitForTerminalAsync(manager, started.Value!.Id);
        OperationResult<CapturePage> page = manager.Read(started.Value.Id, 0, 10);
        OperationResult<CaptureStatus> stopped = await manager.StopAsync(started.Value.Id);

        Assert.Equal("Stopped", completed.State);
        Assert.Equal(2, page.Value!.Events.Count);
        Assert.Equal(2, page.Value.NextSequence);
        Assert.Equal("Stopped", stopped.Value!.State);
        Assert.StartsWith(CaptureManager.SessionPrefix, started.Value.SessionName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_StopsOnlyVestigoPrefixedOrphans()
    {
        File.WriteAllText(
            VestigoPaths.OwnedSessionsPath,
            """["Vestigo-orphan","NT Kernel Logger"]""");
        FakeSessionFactory factory = new();
        CaptureManager manager = CreateManager(factory, out _);

        await manager.StartAsync(CancellationToken.None);

        Assert.Equal(["Vestigo-orphan"], factory.Stopped);
    }

    [Fact]
    public async Task Cleanup_RemovesExpiredStoppedCapture()
    {
        FakeSessionFactory factory = new() { Events = ["{\"Event\":1}"] };
        CaptureManager manager = CreateManager(factory, out VestigoOptions options);
        options.SpoolRetention = TimeSpan.FromMilliseconds(1);
        OperationResult<CaptureStatus> started = await manager.StartAsync(
            new StartCaptureRequest([], [], [Guid.NewGuid()], "Information", ulong.MaxValue, 0, TimeSpan.FromSeconds(1)),
            CancellationToken.None);
        await WaitForTerminalAsync(manager, started.Value!.Id);
        await Task.Delay(30);

        manager.CleanupExpired();

        Assert.False(manager.Get(started.Value.Id).Success);
    }

    [Fact]
    public async Task Read_PagesBySequence()
    {
        FakeSessionFactory factory = new()
        {
            Events = ["{\"Event\":1}", "{\"Event\":2}", "{\"Event\":3}"]
        };
        CaptureManager manager = CreateManager(factory, out VestigoOptions options);
        options.MaxReadBatch = 2;
        OperationResult<CaptureStatus> started = await manager.StartAsync(
            new StartCaptureRequest([], [], [Guid.NewGuid()], "Verbose", ulong.MaxValue, 0, null),
            CancellationToken.None);
        await WaitForTerminalAsync(manager, started.Value!.Id);

        OperationResult<CapturePage> first = manager.Read(started.Value.Id, 0, null);
        OperationResult<CapturePage> second = manager.Read(started.Value.Id, first.Value!.NextSequence, 10);

        Assert.Equal(2, first.Value.Events.Count);
        Assert.Equal(2, first.Value.NextSequence);
        Assert.Single(second.Value!.Events);
        Assert.Equal(3, second.Value.NextSequence);
    }

    [Fact]
    public async Task Stop_CancelsARunningCapture()
    {
        FakeSessionFactory factory = new() { WaitUntilCancelled = true, Events = ["{\"Event\":1}"] };
        CaptureManager manager = CreateManager(factory, out _);
        OperationResult<CaptureStatus> started = await manager.StartAsync(
            new StartCaptureRequest([], [], [Guid.NewGuid()], "Verbose", ulong.MaxValue, 0, null),
            CancellationToken.None);

        Assert.Equal("Running", started.Value!.State);
        OperationResult<CaptureStatus> stopped = await manager.StopAsync(started.Value.Id);

        Assert.True(stopped.Success);
        Assert.Equal("Stopped", stopped.Value!.State);
        Assert.Equal("stopped", stopped.Value.StopReason);
    }

    [Fact]
    public async Task Start_RejectsCaptureWithoutProviders()
    {
        CaptureManager manager = CreateManager(new FakeSessionFactory(), out _);

        OperationResult<CaptureStatus> result = await manager.StartAsync(
            new StartCaptureRequest([], [], [], "Verbose", ulong.MaxValue, 0, null),
            CancellationToken.None);

        Assert.Equal("providers_required", result.ErrorCode);
    }

    [Fact]
    public async Task Start_RejectsUndefinedTraceLevel()
    {
        CaptureManager manager = CreateManager(new FakeSessionFactory(), out _);

        OperationResult<CaptureStatus> result = await manager.StartAsync(
            new StartCaptureRequest([], [], [Guid.NewGuid()], "6", ulong.MaxValue, 0, null),
            CancellationToken.None);

        Assert.Equal("invalid_request", result.ErrorCode);
    }

    [Fact]
    public async Task ReadLoop_RecordsTheEventThatReachesTheLimit()
    {
        FakeSessionFactory factory = new() { Events = ["{\"Event\":1}", "{\"Event\":2}"] };
        CaptureManager manager = CreateManager(factory, out VestigoOptions options);
        options.MaxEvents = 1;

        OperationResult<CaptureStatus> started = await manager.StartAsync(
            new StartCaptureRequest([], [], [Guid.NewGuid()], "Verbose", ulong.MaxValue, 0, null),
            CancellationToken.None);
        CaptureStatus completed = await WaitForTerminalAsync(manager, started.Value!.Id);

        Assert.Equal("limit_reached", completed.StopReason);
        Assert.Equal(1, completed.EventCount);
        Assert.Single(manager.Read(started.Value.Id, 0, 10).Value!.Events);
    }

    [Fact]
    public async Task Startup_RetainsOrphansThatFailToStop()
    {
        File.WriteAllText(VestigoPaths.OwnedSessionsPath, """["Vestigo-stuck","Vestigo-ok"]""");
        FakeSessionFactory factory = new();
        factory.FailStop.Add("Vestigo-stuck");
        CaptureManager manager = CreateManager(factory, out _);

        await manager.StartAsync(CancellationToken.None);

        string saved = File.ReadAllText(VestigoPaths.OwnedSessionsPath);
        Assert.Contains("Vestigo-stuck", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("Vestigo-ok", saved, StringComparison.Ordinal);

        OperationResult<CaptureStatus> started = await manager.StartAsync(
            new StartCaptureRequest([], [], [Guid.NewGuid()], "Verbose", ulong.MaxValue, 0, TimeSpan.FromSeconds(1)),
            CancellationToken.None);
        await WaitForTerminalAsync(manager, started.Value!.Id);

        Assert.Contains("Vestigo-stuck", File.ReadAllText(VestigoPaths.OwnedSessionsPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartCapture_AcceptsHexAndDecimalKeywords()
    {
        FakeSessionFactory factory = new() { Events = [] };
        CaptureManager manager = CreateManager(factory, out VestigoOptions options);
        SymbolCatalogService symbols = new(options, new PathPolicy(options), new UnusedHttpClientFactory());
        TraceTools tools = new(symbols, new OfflineConversionService(options, new PathPolicy(options), symbols), manager, factory);

        OperationResult<CaptureStatus> hex = await tools.StartCapture(
            providerIds: ["11111111-1111-1111-1111-111111111111"],
            matchAnyKeyword: "0x10",
            matchAllKeyword: "16");
        OperationResult<CaptureStatus> bad = await tools.StartCapture(
            providerIds: ["11111111-1111-1111-1111-111111111111"],
            matchAnyKeyword: "0xZZ");

        Assert.True(hex.Success);
        Assert.Equal(0x10UL, factory.LastMatchAny);
        Assert.Equal(16UL, factory.LastMatchAll);
        Assert.Equal("invalid_request", bad.ErrorCode);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", null);
    }

    private CaptureManager CreateManager(FakeSessionFactory factory, out VestigoOptions options)
    {
        options = new VestigoOptions
        {
            AllowedInputRoots = [_input],
            AllowedOutputRoots = [_input],
            MaxConcurrentCaptures = 2,
            SpoolRetention = TimeSpan.FromHours(1)
        };
        SymbolCatalogService symbols = new(options, new PathPolicy(options), new UnusedHttpClientFactory());
        return new CaptureManager(options, symbols, factory, NullLogger<CaptureManager>.Instance);
    }

    private static async Task<CaptureStatus> WaitForTerminalAsync(CaptureManager manager, string id)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            CaptureStatus status = manager.Get(id).Value!;
            if (status.State is "Stopped" or "Failed")
            {
                return status;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Capture did not finish.");
    }

    private sealed class FakeSessionFactory : IRealtimeTraceSessionFactory
    {
        public string[] Events { get; init; } = [];

        public bool WaitUntilCancelled { get; init; }

        public List<string> Stopped { get; } = [];

        public HashSet<string> FailStop { get; } = new(StringComparer.Ordinal);

        public ulong LastMatchAny { get; set; }

        public ulong LastMatchAll { get; set; }

        public IRealtimeTraceSession Create(string sessionName, VestigoOptions options) =>
            new FakeSession(sessionName, Events, WaitUntilCancelled, this);

        public void StopOrphan(string sessionName)
        {
            if (FailStop.Contains(sessionName))
            {
                throw new InvalidOperationException(sessionName);
            }

            if (!sessionName.StartsWith(CaptureManager.SessionPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(sessionName);
            }

            Stopped.Add(sessionName);
        }

        public IReadOnlyList<string> ListSessions() => ["Vestigo-live", "Other Session"];
    }

    private sealed class FakeSession(
        string sessionName,
        string[] events,
        bool waitUntilCancelled,
        FakeSessionFactory factory) : IRealtimeTraceSession
    {
        public string SessionName { get; } = sessionName;

        public void EnableProvider(Guid providerId, TraceLevelSelection level, ulong matchAnyKeyword, ulong matchAllKeyword)
        {
            factory.LastMatchAny = matchAnyKeyword;
            factory.LastMatchAll = matchAllKeyword;
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadEventsAsync(
            DecodingContext? context,
            bool rewriteProviderName,
            Action onMissingFormat,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (string item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return System.Text.Encoding.UTF8.GetBytes(item);
                await Task.Yield();
            }

            if (waitUntilCancelled)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }
}
