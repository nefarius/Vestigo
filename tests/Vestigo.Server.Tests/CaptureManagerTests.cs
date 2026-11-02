using Microsoft.Extensions.Logging.Abstractions;

using Nefarius.Utilities.ETW.Deserializer.WPP;

using Vestigo.Configuration;
using Vestigo.Etws;
using Vestigo.Security;
using Vestigo.Spooling;
using Vestigo.Tools;

namespace Vestigo.Server.Tests;

[Collection("VestigoHost")] // shares the machine-wide owned-sessions.json
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
        Assert.Equal("[]", File.ReadAllText(VestigoPaths.OwnedSessionsPath));
        Assert.Empty(Directory.GetFiles(VestigoPaths.SettingsDirectory, ".owned-sessions.*.tmp"));
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
        TraceTools tools = new(
            symbols,
            new OfflineConversionService(options, new PathPolicy(options), symbols),
            manager,
            factory,
            new TraceArchiveExtractor(options, new PathPolicy(options)));

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

    private static StartCaptureRequest AnyRequest() =>
        new([], [], [Guid.NewGuid()], "Verbose", ulong.MaxValue, 0, null);

    [Fact]
    public async Task ReadAsync_ReturnsAsSoonAsAnEventArrives()
    {
        FakeSessionFactory factory = new() { Feed = System.Threading.Channels.Channel.CreateUnbounded<string>() };
        CaptureManager manager = CreateManager(factory, out VestigoOptions options);
        options.MaxReadWait = TimeSpan.FromSeconds(10);
        OperationResult<CaptureStatus> started = await manager.StartAsync(AnyRequest(), CancellationToken.None);
        string id = started.Value!.Id;

        Task<OperationResult<CapturePage>> pending = manager.ReadAsync(id, 0, null, TimeSpan.FromSeconds(10), CancellationToken.None);
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);
        await factory.Feed.Writer.WriteAsync("{\"Event\":1}");

        CapturePage page = (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Value!;
        Assert.Single(page.Events);
        Assert.False(page.Completed);
        Assert.Equal("Running", page.State);
        await manager.StopAsync(id);
    }

    [Fact]
    public async Task ReadAsync_ReturnsEmptyAtTimeout()
    {
        FakeSessionFactory factory = new() { Feed = System.Threading.Channels.Channel.CreateUnbounded<string>() };
        CaptureManager manager = CreateManager(factory, out _);
        OperationResult<CaptureStatus> started = await manager.StartAsync(AnyRequest(), CancellationToken.None);
        string id = started.Value!.Id;

        CapturePage page = (await manager.ReadAsync(id, 0, null, TimeSpan.FromMilliseconds(100), CancellationToken.None)).Value!;

        Assert.Empty(page.Events);
        Assert.False(page.Completed);
        await manager.StopAsync(id);
    }

    [Fact]
    public async Task ReadAsync_CompletesWaitersWhenStopped()
    {
        FakeSessionFactory factory = new() { Feed = System.Threading.Channels.Channel.CreateUnbounded<string>() };
        CaptureManager manager = CreateManager(factory, out _);
        OperationResult<CaptureStatus> started = await manager.StartAsync(AnyRequest(), CancellationToken.None);
        string id = started.Value!.Id;

        Task<OperationResult<CapturePage>> pending = manager.ReadAsync(id, 0, null, TimeSpan.FromSeconds(10), CancellationToken.None);
        await manager.StopAsync(id);

        CapturePage page = (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Value!;
        Assert.True(page.Completed);
        Assert.Equal("Stopped", page.State);
    }

    [Fact]
    public async Task ReadAsync_HonorsCancellation()
    {
        FakeSessionFactory factory = new() { Feed = System.Threading.Channels.Channel.CreateUnbounded<string>() };
        CaptureManager manager = CreateManager(factory, out _);
        OperationResult<CaptureStatus> started = await manager.StartAsync(AnyRequest(), CancellationToken.None);
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ReadAsync(started.Value!.Id, 0, null, TimeSpan.FromSeconds(10), cts.Token));
        await manager.StopAsync(started.Value!.Id);
    }

    [Fact]
    public async Task Read_PagesAcrossCheckpointBoundary()
    {
        const int count = 2100;
        FakeSessionFactory factory = new() { Events = Enumerable.Range(1, count).Select(i => "{\"Event\":" + i + "}").ToArray() };
        CaptureManager manager = CreateManager(factory, out VestigoOptions options);
        options.MaxReadBatch = 500;
        OperationResult<CaptureStatus> started = await manager.StartAsync(AnyRequest(), CancellationToken.None);
        await WaitForTerminalAsync(manager, started.Value!.Id, 500);

        long sequence = 0;
        int seen = 0;
        while (true)
        {
            CapturePage page = manager.Read(started.Value.Id, sequence, null).Value!;
            foreach (string line in page.Events)
            {
                seen++;
                Assert.Equal("{\"Event\":" + seen + "}", line);
            }

            sequence = page.NextSequence;
            if (page.Completed)
            {
                break;
            }
        }

        Assert.Equal(count, seen);
        CapturePage tail = manager.Read(started.Value.Id, 1500, 3).Value!;
        Assert.Equal("{\"Event\":1501}", tail.Events[0]);
    }

    [Fact]
    public async Task Attach_DecodesEventsAndNeverStopsOrPersistsTheSession()
    {
        FakeSessionFactory factory = new()
        {
            Events = ["{\"Event\":1}"],
            Feed = System.Threading.Channels.Channel.CreateUnbounded<string>()
        };
        CaptureManager manager = CreateManager(factory, out VestigoOptions options);
        options.AttachableSessions = ["External*"];

        OperationResult<CaptureStatus> attached = await manager.AttachAsync(
            new AttachSessionRequest("external session", [], null),
            CancellationToken.None);
        Assert.True(attached.Success);
        Assert.Equal("Attached", attached.Value!.Mode);
        Assert.Equal(["External Session"], factory.Attached);

        CapturePage page = (await manager.ReadAsync(attached.Value.Id, 0, null, TimeSpan.FromSeconds(5), CancellationToken.None)).Value!;
        Assert.Single(page.Events);
        string owned = File.Exists(VestigoPaths.OwnedSessionsPath) ? File.ReadAllText(VestigoPaths.OwnedSessionsPath) : "[]";
        Assert.DoesNotContain("External", owned, StringComparison.Ordinal);

        OperationResult<CaptureStatus> stopped = await manager.StopAsync(attached.Value.Id);
        Assert.Equal("Stopped", stopped.Value!.State);
        Assert.Empty(factory.Stopped);
    }

    [Fact]
    public async Task Attach_EnforcesAllowlistPrefixAndExistence()
    {
        FakeSessionFactory factory = new();
        CaptureManager manager = CreateManager(factory, out VestigoOptions options);

        OperationResult<CaptureStatus> disabled = await manager.AttachAsync(
            new AttachSessionRequest("External Session", [], null), CancellationToken.None);
        options.AttachableSessions = ["*"];
        OperationResult<CaptureStatus> prefixed = await manager.AttachAsync(
            new AttachSessionRequest("Vestigo-live", [], null), CancellationToken.None);
        OperationResult<CaptureStatus> missing = await manager.AttachAsync(
            new AttachSessionRequest("Nope", [], null), CancellationToken.None);

        Assert.Equal("attach_not_allowed", disabled.ErrorCode);
        Assert.Equal("invalid_request", prefixed.ErrorCode);
        Assert.Equal("session_not_found", missing.ErrorCode);
        Assert.Empty(factory.Attached);
    }

    [Fact]
    public void Options_ValidateReadWaitAndAttachableSessions()
    {
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { MaxReadWait = TimeSpan.FromMinutes(3) }.Validate());
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { AttachableSessions = [" "] }.Validate());
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

    private static async Task<CaptureStatus> WaitForTerminalAsync(CaptureManager manager, string id, int attempts = 50)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            CaptureStatus status = manager.Get(id).Value!;
            if (status.State is "Stopped" or "Failed")
            {
                // State flips before the reader's finally block saves owned sessions; wait for the reader to finish.
                return (await manager.StopAsync(id)).Value!;
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

        public IReadOnlyList<string> ListSessions() => ["Vestigo-live", "Other Session", "External Session"];

        public System.Threading.Channels.Channel<string>? Feed { get; set; }

        public List<string> Attached { get; } = [];

        public int AttachedDisposed { get; set; }

        public IRealtimeTraceSession Attach(string sessionName)
        {
            Attached.Add(sessionName);
            return new FakeSession(sessionName, Events, WaitUntilCancelled, this, attached: true);
        }
    }

    private sealed class FakeSession(
        string sessionName,
        string[] events,
        bool waitUntilCancelled,
        FakeSessionFactory factory,
        bool attached = false) : IRealtimeTraceSession
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

            if (factory.Feed is not null)
            {
                await foreach (string item in factory.Feed.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return System.Text.Encoding.UTF8.GetBytes(item);
                }
            }

            if (waitUntilCancelled)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (attached)
            {
                factory.AttachedDisposed++;
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }
}

public class NdjsonSpoolTests
{
    [Fact]
    public async Task ReadAsync_WaitsOnAnEmptyLivePageUntilAConcurrentAppendSignals()
    {
        string path = Path.Combine(Path.GetTempPath(), "vestigo-spool-" + Guid.NewGuid().ToString("N") + ".ndjson");
        await using NdjsonSpool spool = new(path);
        spool.OpenWrite();

        Task<NdjsonPage> pending = spool.ReadAsync(
            0,
            10,
            () => "Running",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);
        await spool.AppendAsync("{}"u8.ToArray());

        NdjsonPage page = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(page.Events);
        Assert.Equal("{}", page.Events[0]);
        TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
