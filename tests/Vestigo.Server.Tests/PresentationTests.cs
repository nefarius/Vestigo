using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Vestigo.Configuration;
using Vestigo.Etws;

namespace Vestigo.Server.Tests;

public class PresentationTests : IDisposable
{
    private readonly string _settings = Path.Combine(Path.GetTempPath(), "vestigo-presentation-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");

    public PresentationTests()
    {
        Directory.CreateDirectory(_settings);
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _previous);
        if (Directory.Exists(_settings))
        {
            Directory.Delete(_settings, recursive: true);
        }
    }

    [Fact]
    public void IndependentFlip_CorrelatesByProcess()
    {
        PresentationObservation observation = Classify(42, null, Event(266, "IndependentFlip", 42));

        Assert.Equal("independent-flip", observation.Mode);
        Assert.Null(observation.Reason);
        Assert.Equal("etw", observation.EvidenceSource);
    }

    [Fact]
    public void IndependentFlip_ForAnotherProcess_DoesNotCorrelate()
    {
        PresentationObservation observation = Classify(42, null, Event(266, "IndependentFlip", 7));

        Assert.Equal("unknown", observation.Mode);
        Assert.Equal("no_correlation", observation.Reason);
    }

    [Fact]
    public void SchedulePresent_UsesTheAttributedProcess()
    {
        PresentationObservation observation = Classify(42, null, """
            {"Event":{"Provider":"Microsoft-Windows-Dwm-Core","TaskName":"SCHEDULE_PRESENT","ProcessId":4,"Properties":[{"Name":"AttributedProcessId","Value":42}]}}
            """);

        Assert.Equal("composed", observation.Mode);
        Assert.Equal(1, observation.CorrelatedEvents);
    }

    [Fact]
    public void ConflictingModes_AreUnknown()
    {
        PresentationObservation observation = Classify(
            42,
            null,
            Event(266, "IndependentFlip", 42),
            """
            {"Event":{"Provider":"Microsoft-Windows-Dwm-Core","TaskName":"SCHEDULE_PRESENT","ProcessId":42}}
            """);

        Assert.Equal("unknown", observation.Mode);
        Assert.Equal("ambiguous", observation.Reason);
    }

    [Fact]
    public void DxgiPresent_IsNotAMode()
    {
        PresentationObservation observation = Classify(42, null, """
            {"Event":{"Provider":"Microsoft-Windows-DXGI","Id":42,"ProcessId":42}}
            """);

        Assert.Equal("insufficient_evidence", observation.Reason);
    }

    [Fact]
    public void AdapterLockExclusive_IsNotExclusiveFullscreen()
    {
        PresentationObservation observation = Classify(42, null, """
            {"Event":{"Provider":"Microsoft-Windows-DxgKrnl","Id":1100,"ProcessId":42,"TaskName":"TraceAdapterLock","Properties":[{"Name":"Exclusive","Value":true}]}}
            """);

        Assert.Equal("insufficient_evidence", observation.Reason);
    }

    [Fact]
    public void ExplicitPresentMode_CanBeExclusive()
    {
        PresentationObservation observation = Classify(42, null, """
            {"Event":{"Provider":"Microsoft-Windows-DXGI","Id":42,"ProcessId":42,"Properties":[{"Name":"PresentMode","Value":"hardware_legacy_flip"}]}}
            """);

        Assert.Equal("exclusive", observation.Mode);
    }

    [Fact]
    public void DroppedEvents_HideAMatchingMode()
    {
        PresentationObservation observation = Classify(42, null, """
            {"Event":{"Provider":"Microsoft-Windows-DxgKrnl","Id":266,"ProcessId":42,"TaskName":"IndependentFlip","EventsLost":2}}
            """);

        Assert.Equal("events_dropped", observation.Reason);
    }

    [Fact]
    public void PartialProviderFailure_HidesAMatchingMode()
    {
        PresentationObservation observation = PresentationClassifier.Classify(
            [Parsed(Event(266, "IndependentFlip", 42))],
            42,
            null,
            ["dxgi"],
            ["dxgkrnl"],
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            "Vestigo-Ptest",
            false,
            false,
            0);

        Assert.Equal("unknown", observation.Mode);
        Assert.Equal("providers_unavailable", observation.Reason);
    }

    [Fact]
    public void OneUndecodedEvent_HidesAMatchingMode()
    {
        PresentationObservation observation = PresentationClassifier.Classify(
            [Parsed(Event(266, "IndependentFlip", 42))],
            42,
            null,
            ["provider"],
            [],
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            "Vestigo-Ptest",
            false,
            false,
            1);

        Assert.Equal("decode_failed", observation.Reason);
    }

    [Fact]
    public void LostEventName_HidesAMatchingMode()
    {
        string json = """
            {"Event":{"Name":"MSNT_SystemTrace/EventTrace/RTLostEvent","Provider":"Microsoft-Windows-DxgKrnl","Id":266,"TaskName":"IndependentFlip","ProcessId":42}}
            """;

        Assert.True(PresentationEventParser.TryParse(Encoding.UTF8.GetBytes(json), out PresentationSample sample));
        Assert.True(sample.Dropped);
        Assert.Equal("events_dropped", Classify(42, null, json).Reason);
        Assert.True(PresentationEventParser.TryParse(
            """{"Event":{"TaskName":"RTLostBuffer","ProcessId":1}}"""u8,
            out PresentationSample buffer));
        Assert.True(buffer.Dropped);
    }

    [Fact]
    public async Task SessionLoss_HidesAMatchingMode()
    {
        ScriptedFactory factory = new() { Events = [Event(266, "IndependentFlip", 42)], EventsLost = true };

        OperationResult<PresentationObservation> result = await Observer(factory).ObserveAsync(42, null, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("events_dropped", result.Value!.Reason);
    }

    [Fact]
    public async Task UnreadableLossCounters_HideAMatchingMode()
    {
        ScriptedFactory factory = new() { Events = [Event(266, "IndependentFlip", 42)], FailLossQuery = true };

        OperationResult<PresentationObservation> result = await Observer(factory).ObserveAsync(42, null, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("events_dropped", result.Value!.Reason);
    }

    [Fact]
    public async Task Startup_KeepsASessionThatFailsToStop()
    {
        File.WriteAllText(VestigoPaths.OwnedPresentationPath, JsonSerializer.Serialize(new[] { "Vestigo-Pstuck", "Vestigo-Pok", "Other Session" }));
        ScriptedFactory factory = new();
        factory.FailStop.Add("Vestigo-Pstuck");

        await Observer(factory).StartAsync(CancellationToken.None);

        string saved = File.ReadAllText(VestigoPaths.OwnedPresentationPath);
        Assert.Contains("Vestigo-Pstuck", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("Vestigo-Pok", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("Other Session", saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeFailure_KeepsTheCheckpointAndReleasesTheGate()
    {
        ScriptedFactory factory = new() { Events = [Event(266, "IndependentFlip", 42)], FailDispose = true };
        PresentationObserver observer = Observer(factory);

        OperationResult<PresentationObservation> first = await observer.ObserveAsync(42, null, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("independent-flip", first.Value!.Mode);
        Assert.Contains(first.Value.SessionName!, File.ReadAllText(VestigoPaths.OwnedPresentationPath));
        factory.FailDispose = false;
        OperationResult<PresentationObservation> second = await observer.ObserveAsync(42, null, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.True(second.Success, second.Error);
        Assert.Contains(first.Value.SessionName!, File.ReadAllText(VestigoPaths.OwnedPresentationPath));
    }

    [Fact]
    public void WindowHandle_MustMatchWhenRequested()
    {
        string matched = """
            {"Event":{"Provider":"Microsoft-Windows-DxgKrnl","Id":266,"ProcessId":42,"TaskName":"IndependentFlip","Properties":[{"Name":"hWnd","Value":"0x63"}]}}
            """;

        Assert.Equal("no_correlation", Classify(42, 99, Event(266, "IndependentFlip", 42)).Reason);
        Assert.Equal("independent-flip", Classify(42, 99, matched).Mode);
    }

    [Fact]
    public async Task Observe_StopsTheSessionAndClearsTheCheckpoint()
    {
        ScriptedFactory factory = new() { Events = [Event(266, "IndependentFlip", 42)] };
        PresentationObserver observer = Observer(factory);

        OperationResult<PresentationObservation> result = await observer.ObserveAsync(42, null, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("independent-flip", result.Value!.Mode);
        Assert.Contains(result.Value.SessionName!, factory.Disposed);
        Assert.Equal("[]", File.ReadAllText(VestigoPaths.OwnedPresentationPath));
    }

    [Fact]
    public async Task Cancellation_StopsTheSessionWithoutAMode()
    {
        ScriptedFactory factory = new() { Hold = true };
        PresentationObserver observer = Observer(factory);
        using CancellationTokenSource cancellation = new();
        Task<OperationResult<PresentationObservation>> running = observer.ObserveAsync(42, null, TimeSpan.FromSeconds(5), cancellation.Token);
        await WaitForSession(factory);
        await cancellation.CancelAsync();
        factory.Release();

        OperationResult<PresentationObservation> result = await running;

        Assert.Equal("cancelled", result.Value!.Reason);
        Assert.Contains(result.Value.SessionName!, factory.Disposed);
    }

    [Fact]
    public async Task Restart_StopsOnlyTheRecordedVestigoSession()
    {
        ScriptedFactory factory = new();
        File.WriteAllText(VestigoPaths.OwnedPresentationPath, JsonSerializer.Serialize(new[] { "Vestigo-P0123456789a", "Other Session" }));

        await Observer(factory).StartAsync(CancellationToken.None);

        Assert.Equal(["Vestigo-P0123456789a"], factory.Stopped);
        Assert.Equal("[]", File.ReadAllText(VestigoPaths.OwnedPresentationPath));
    }

    [Fact]
    public async Task UnavailableSession_DoesNotLeaveACheckpoint()
    {
        ScriptedFactory factory = new() { CreateError = new InvalidOperationException("access denied") };

        OperationResult<PresentationObservation> result = await Observer(factory).ObserveAsync(42, null, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("session_unavailable", result.Value!.Reason);
        Assert.Empty(factory.Disposed);
        Assert.Equal("[]", File.ReadAllText(VestigoPaths.OwnedPresentationPath));
    }

    [Fact]
    public async Task WindowsObservation_IsOptIn()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("VESTIGO_PRESENTATION_TESTS") != "1")
        {
            Assert.Skip("Set VESTIGO_PRESENTATION_TESTS=1 in an elevated Windows session.");
        }

        PresentationObserver observer = new(
            new EtwRealtimeTraceSessionFactory(),
            new VestigoOptions(),
            TimeProvider.System,
            NullLogger<PresentationObserver>.Instance);
        OperationResult<PresentationObservation> result = await observer.ObserveAsync(
            Environment.ProcessId,
            null,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Contains(result.Value!.Mode, new[] { "composed", "independent-flip", "exclusive", "unknown" });
        if (result.Value.Mode == "unknown")
        {
            Assert.Contains(result.Value.Reason, new[]
            {
                "providers_unavailable", "no_events", "no_correlation", "insufficient_evidence",
                "ambiguous", "events_dropped", "events_truncated", "decode_failed", "session_unavailable", "cancelled"
            });
        }

        Assert.DoesNotContain(result.Value.SessionName!, new EtwRealtimeTraceSessionFactory().ListSessions());
    }

    private PresentationObserver Observer(ScriptedFactory factory) =>
        new(factory, new VestigoOptions(), TimeProvider.System, NullLogger<PresentationObserver>.Instance);

    private static PresentationSample Parsed(string json)
    {
        Assert.True(PresentationEventParser.TryParse(Encoding.UTF8.GetBytes(json), out PresentationSample sample));
        return sample;
    }

    private static PresentationObservation Classify(int processId, long? window, params string[] json)
    {
        List<PresentationSample> samples = json.Select(item =>
        {
            Assert.True(PresentationEventParser.TryParse(Encoding.UTF8.GetBytes(item), out PresentationSample sample));
            return sample;
        }).ToList();
        return PresentationClassifier.Classify(
            samples,
            processId,
            window,
            ["provider"],
            [],
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            "Vestigo-Ptest",
            cancelled: false,
            truncated: false,
            undecodedEvents: 0);
    }

    private static string Event(int id, string task, int processId) =>
        "{\"Event\":{\"Provider\":\"Microsoft-Windows-DxgKrnl\",\"Id\":" + id + ",\"TaskName\":\"" + task + "\",\"ProcessId\":" + processId + "}}";

    private static async Task WaitForSession(ScriptedFactory factory)
    {
        for (int attempt = 0; attempt < 50 && factory.Created.Count == 0; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.NotEmpty(factory.Created);
    }

    private sealed class ScriptedFactory : IRealtimeTraceSessionFactory
    {
        public string[] Events { get; init; } = [];

        public bool Hold { get; init; }

        public bool EventsLost { get; init; }

        public bool FailDispose { get; set; }

        public bool FailLossQuery { get; init; }

        public Exception? CreateError { get; init; }

        public HashSet<string> FailStop { get; } = new(StringComparer.Ordinal);

        public List<string> Created { get; } = [];

        public List<string> Disposed { get; } = [];

        public List<string> Stopped { get; } = [];

        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public IRealtimeTraceSession Create(string sessionName, VestigoOptions options)
        {
            if (CreateError is not null)
            {
                throw CreateError;
            }

            Created.Add(sessionName);
            return new ScriptedSession(sessionName, this);
        }

        public IRealtimeTraceSession Attach(string sessionName) => throw new NotSupportedException();

        public void StopOrphan(string sessionName)
        {
            Stopped.Add(sessionName);
            if (FailStop.Contains(sessionName))
            {
                throw new IOException("The presentation session could not be stopped.");
            }
        }

        public IReadOnlyList<string> ListSessions() => [];

        private sealed class ScriptedSession(string sessionName, ScriptedFactory factory) : IRealtimeTraceSession
        {
            public string SessionName => sessionName;

            public bool EventsWereLost => factory.FailLossQuery
                ? throw new IOException("The loss counters could not be read.")
                : factory.EventsLost;

            public void EnableProvider(Guid providerId, TraceLevelSelection level, ulong matchAnyKeyword, ulong matchAllKeyword)
            {
            }

            public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadEventsAsync(
                Nefarius.Utilities.ETW.Deserializer.WPP.DecodingContext? context,
                bool rewriteProviderName,
                Action onMissingFormat,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
            {
                if (factory.Hold)
                {
                    await factory._gate.Task.WaitAsync(cancellationToken);
                }

                foreach (string item in factory.Events)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return Encoding.UTF8.GetBytes(item);
                }
            }

            public ValueTask DisposeAsync()
            {
                factory.Disposed.Add(sessionName);
                if (factory.FailDispose)
                {
                    throw new IOException("The presentation session could not be stopped.");
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
