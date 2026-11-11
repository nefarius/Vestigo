using System.Runtime.CompilerServices;
using System.Text.Json;

using Vestigo.Configuration;
using Vestigo.Desktop;

namespace Vestigo.Server.Tests;

public class WindowInspectionContractTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void FailedField_DoesNotSerializeAZeroRectangle()
    {
        string json = JsonSerializer.Serialize(
            FieldResult<DesktopRect>.Fail("dwm_unavailable", "DWM did not return the extended frame bounds.", -1),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("dwm_unavailable", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"value\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"x\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void GeometryMath_ExplainsTheGameFrameAndAdjacentDisplays()
    {
        DesktopRect raw = new(-8, -31, 1936, 1239);
        DesktopRect client = new(0, 0, 1920, 1200);
        WindowInsets insets = WindowGeometryMath.Insets(raw, client);

        Assert.Equal(new WindowInsets(8, 31, 8, 8), insets);
        Assert.Equal(raw, WindowGeometryMath.Expand(client, insets));

        DisplayInfo left = Display(@"\\.\DISPLAY1", 0, 0, 1920, 1080, primary: true);
        DisplayInfo right = Display(@"\\.\DISPLAY2", 1920, 0, 1920, 1080, primary: false);
        IReadOnlyList<DisplayIntersection> adjacent = WindowGeometryMath.Intersections(raw, client, @"\\.\DISPLAY1", [left, right]);
        Assert.Equal(2, adjacent.Count);
        Assert.Contains(adjacent, item => item.GdiDeviceName == @"\\.\DISPLAY1" && item.Nearest && item.RawOverlap is not null);
        DisplayIntersection overhang = Assert.Single(adjacent, item => item.GdiDeviceName == @"\\.\DISPLAY2");
        Assert.Equal(new DesktopRect(1920, 0, 8, 1080), overhang.RawOverlap);
        Assert.Null(overhang.ClientOverlap);

        IReadOnlyList<DisplayIntersection> crossing = WindowGeometryMath.Intersections(
            new DesktopRect(1910, 0, 20, 100),
            null,
            @"\\.\DISPLAY1",
            [left, right]);
        Assert.Equal(2, crossing.Count);
        Assert.Contains(crossing, item => item.GdiDeviceName == @"\\.\DISPLAY2" && item.RawOverlap is not null);
    }

    [Fact]
    public void Inspect_RejectsAStalePidOrStartTime()
    {
        FakeInspection windows = new();
        WindowInspector inspector = new(windows);

        Assert.Equal("window_identity_mismatch", inspector.Inspect("0x11", 99, null).ErrorCode);
        Assert.Equal("process_identity_mismatch", inspector.Inspect("0x11", 10, Start.AddMinutes(1)).ErrorCode);
        windows.Before = windows.Before with { StartTimeReadable = false, StartTime = null };
        Assert.Equal("process_identity_unreadable", inspector.Inspect("0x11", 10, Start).ErrorCode);
        Assert.Equal(0, windows.Reads);
    }

    [Fact]
    public void Inspect_OmitsGeometryWhenDpiContextCannotBeEntered()
    {
        FakeInspection windows = new() { Dpi = false };
        OperationResult<WindowInspectionReport> result = new WindowInspector(windows).Inspect("0x11", 10, Start);

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Value!.Geometry);
        Assert.Equal("dpi_context_unavailable", result.Value.Frame.ErrorCode);
        Assert.Equal("dpi_context_unavailable", result.Value.Monitor.ErrorCode);
        Assert.True(windows.Restored);
        string json = JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("\"rawBounds\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_KeepsNegativeCoordinatesAndPartialFailures()
    {
        FakeInspection windows = new();
        OperationResult<WindowInspectionReport> result = new WindowInspector(windows).Inspect("0x11", 10, Start);

        Assert.True(result.Success, result.Error);
        Assert.Equal(-8, result.Value!.Geometry!.RawBounds.Value!.X);
        Assert.False(result.Value.Geometry.DwmVisibleBounds.Success);
        Assert.Null(result.Value.Geometry.DwmVisibleBounds.Value);
        Assert.Equal("access_denied", result.Value.Process.ImagePath.ErrorCode);
        Assert.True(windows.Restored);
    }

    [Fact]
    public void Inspect_ReturnsNoReportWhenTheWindowIsReusedDuringTheRead()
    {
        FakeInspection windows = new() { After = new WindowProcessIdentity(11, 2, true, Start.AddSeconds(1), null) };

        OperationResult<WindowInspectionReport> result = new WindowInspector(windows).Inspect("0x11", 10, Start);

        Assert.False(result.Success);
        Assert.Equal("window_identity_mismatch", result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Inspect_ReportsAccessDeniedWithoutARectangle()
    {
        FakeInspection windows = new()
        {
            Identity = OperationResult<WindowProcessIdentity>.Fail("access_denied", "OpenProcess failed.")
        };

        OperationResult<WindowInspectionReport> result = new WindowInspector(windows).Inspect("0x11", null, null);

        Assert.Equal("access_denied", result.ErrorCode);
        Assert.Null(result.Value);
    }

    private static DisplayInfo Display(string name, int x, int y, int width, int height, bool primary) =>
        new(name, primary, new DesktopRect(x, y, width, height), new DesktopRect(x, y, width, height - 40), 96, null, []);

    private sealed class FakeInspection : IWindowInspectionSource
    {
        public WindowProcessIdentity Before = new(10, 1, true, Start, null);
        public WindowProcessIdentity? After;
        public OperationResult<WindowProcessIdentity>? Identity;
        public bool Dpi = true;
        public bool Restored;
        public int Reads;
        private int _identityReads;

        public bool TryParseHandle(string? text, out nint handle)
        {
            handle = 0x11;
            return string.Equals(text, "0x11", StringComparison.OrdinalIgnoreCase);
        }

        public bool IsWindow(nint handle) => handle == 0x11;

        public OperationResult<WindowProcessIdentity> ReadIdentity(nint handle)
        {
            if (Identity is not null)
            {
                return Identity;
            }

            _identityReads++;
            return OperationResult<WindowProcessIdentity>.Ok(_identityReads == 1 ? Before : After ?? Before);
        }

        public PhysicalDpiLease EnterPhysicalCoordinates() => new()
        {
            Established = Dpi,
            Actual = Dpi ? DesktopCoordinates.PerMonitorV2 : null,
            ErrorCode = Dpi ? null : "dpi_context_unavailable",
            Error = Dpi ? null : "The physical DPI context could not be established.",
            Restore = () => Restored = true
        };

        public WindowInspectionReport Read(nint handle, WindowProcessIdentity identity, PhysicalDpiLease dpi, string requestedHandle)
        {
            Reads++;
            FieldResult<DesktopRect> raw = FieldResult<DesktopRect>.Ok(new DesktopRect(-8, -31, 1936, 1239));
            FieldResult<DesktopRect> dwm = FieldResult<DesktopRect>.Fail("dwm_unavailable", "DWM did not return the extended frame bounds.", -1);
            FieldResult<DesktopRect> client = FieldResult<DesktopRect>.Ok(new DesktopRect(0, 0, 1920, 1200));
            return new WindowInspectionReport(
                requestedHandle,
                identity.ProcessId,
                identity.ThreadId,
                FieldResult<TimestampFact>.Ok(new TimestampFact(identity.StartTime ?? Start)),
                FieldResult<DpiContextInfo>.Ok(new DpiContextInfo(DesktopCoordinates.PerMonitorV2, dpi.Actual, dpi.Established)),
                new WindowGeometryReport(DesktopCoordinates.PhysicalScreenPixels, raw, dwm, client, FieldResult<WindowInsets>.Ok(new WindowInsets(8, 31, 8, 8))),
                FieldResult<FrameMetrics>.Ok(new FrameMetrics(8, 8, 4, 23)),
                FieldResult<WindowStyleInfo>.Ok(new WindowStyleInfo("0xCF0000", "0x0", ["WS_OVERLAPPEDWINDOW"], [])),
                new WindowVisibility(
                    FieldResult<FlagFact>.Ok(new FlagFact(true)),
                    FieldResult<FlagFact>.Ok(new FlagFact(false)),
                    FieldResult<FlagFact>.Ok(new FlagFact(false)),
                    FieldResult<FlagFact>.Ok(new FlagFact(false))),
                FieldResult<WindowRelations>.Ok(new WindowRelations(null, null, null)),
                FieldResult<WindowMonitorInfo>.Ok(new WindowMonitorInfo(@"\\.\DISPLAY1", [])),
                new ProcessInspection(
                    FieldResult<CountFact>.Ok(new CountFact(1)),
                    FieldResult<TextFact>.Ok(new TextFact("game")),
                    FieldResult<TextFact>.Fail("access_denied", "The process image path could not be read.", 5),
                    FieldResult<TextFact>.Ok(new TextFact("medium")),
                    FieldResult<TextFact>.Ok(new TextFact("S-1-16-8192")),
                    FieldResult<FlagFact>.Ok(new FlagFact(false)),
                    FieldResult<TextFact>.Ok(new TextFact("default")),
                    FieldResult<FlagFact>.Ok(new FlagFact(false))));
        }
    }
}

public class WindowEventContractTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-window-events").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");
    private static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public WindowEventContractTests() => Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);

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
    public void Pipeline_FiltersChildrenKeepsLaterWindowsAndLabelsDpiAndExit()
    {
        WindowEventPipeline process = new(new WindowEventStart(null, 10, Start, TimeSpan.FromSeconds(5), 20));
        DateTimeOffset now = Start.AddMinutes(1);
        Assert.Equal("ready", process.Accept(Line(Ready(includeNew: true)), now)[0].Kind);
        Assert.Empty(process.Accept(Line(Observation("created", "0x22", 11, Start)), now));
        WindowEventRecord created = Assert.Single(process.Accept(Line(Observation("created", "0x22", 10, Start, dpi: 96)), now));
        Assert.Equal(2, created.Sequence);
        Assert.Equal("observed", created.Origin);
        WindowEventRecord[] sampled = process.Accept(Line(Observation("location-changed", "0x22", 10, Start, dpi: 144)), now).ToArray();
        Assert.Equal("location-changed", sampled[0].Event);
        Assert.Equal("dpi-sampled", sampled[1].Event);
        Assert.Equal("sampled", sampled[1].Origin);
        Assert.Contains("WM_DPICHANGED", sampled[1].Explanation, StringComparison.Ordinal);

        WindowEventRecord exited = Assert.Single(process.Accept(Line(new WindowEventMessage(
            "observation", now, "process-exited", null, 10, Start, true, null, null, null, null, null, null, null, 1,
            null, null, null, null, null, null, null, null)), now));
        Assert.Equal("observed", exited.Origin);
        Assert.Equal(1, exited.ExitCode);
        Assert.Contains("No exit cause", exited.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("cause", exited.Event, StringComparison.Ordinal);
    }

    [Fact]
    public void Pipeline_ReportsIdentityReuseAndDropsOtherWindows()
    {
        WindowEventPipeline window = new(new WindowEventStart("0x11", 10, Start, TimeSpan.FromSeconds(5), 20));
        window.Accept(Line(Ready(includeNew: false, handle: "0x11")), Start);
        Assert.Empty(window.Accept(Line(Observation("shown", "0x99", 10, Start)), Start));
        WindowEventRecord mismatch = Assert.Single(window.Accept(Line(Observation("shown", "0x11", 10, Start.AddSeconds(1))), Start));
        Assert.Equal("identity-mismatch", mismatch.Event);
        Assert.Equal("observed", mismatch.Origin);
        Assert.Contains("process_identity_mismatch", mismatch.Errors!);
        Assert.Null(mismatch.RawBounds);
    }

    [Fact]
    public void Pipeline_RejectsUntrustedLinesWithoutKeepingThem()
    {
        WindowEventPipeline pipeline = new(new WindowEventStart("0x11", 10, Start, TimeSpan.FromSeconds(5), 20));
        string secret = "secret-title-should-not-be-stored";
        WindowEventRecord rejected = Assert.Single(pipeline.Accept(secret, Start));

        Assert.Equal("collection-error", rejected.Kind);
        Assert.Contains("untrusted_event", rejected.Errors!);
        Assert.DoesNotContain(secret, rejected.Explanation, StringComparison.Ordinal);
        Assert.Null(rejected.Title);
        Assert.False(pipeline.Accept(Line(new WindowEventMessage(
            "mystery", null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null, null)), Start)[0].Explanation!.Contains("mystery", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Manager_StopsAtTheEventCapAndOnCancel()
    {
        string directory = Path.Combine(_settings, "spool");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(directory + "-live");
        VestigoOptions options = new() { Desktop = { MaxWindowEvents = 2, MaxWindowEventCaptures = 1 } };
        ScriptFactory scripts = new(_ => new ScriptSession(
        [
            Line(Ready(includeNew: true)),
            Line(Observation("created", "0x22", 10, Start)),
            Line(Observation("location-changed", "0x22", 10, Start))
        ], hold: false));
        WindowEventCaptureManager manager = new(options, scripts, TimeProvider.System, directory);
        OperationResult<WindowEventCaptureStatus> started = await manager.StartAsync(
            new WindowEventStart(null, 10, Start, TimeSpan.FromSeconds(5), 2),
            CancellationToken.None);
        await WaitStopped(manager, started.Value!.Id);

        OperationResult<WindowEventPage> page = await manager.ReadAsync(started.Value.Id, 0, 10, TimeSpan.Zero, CancellationToken.None);
        Assert.True(page.Value!.Truncated);
        Assert.Equal("limit_reached", manager.Get(started.Value.Id).Value!.StopReason);
        Assert.DoesNotContain(page.Value.Events, record => record.Event == "location-changed");

        ScriptFactory running = new(_ => new ScriptSession([Line(Ready(includeNew: false, handle: "0x11"))], hold: true));
        WindowEventCaptureManager live = new(options, running, TimeProvider.System, directory + "-live");
        OperationResult<WindowEventCaptureStatus> first = await live.StartAsync(
            new WindowEventStart("0x11", 10, Start, TimeSpan.FromSeconds(5), 20),
            CancellationToken.None);
        Assert.True(first.Success, first.Error);
        Assert.Equal("too_many_captures", (await live.StartAsync(new WindowEventStart("0x11", 10, Start, TimeSpan.FromSeconds(5), 20), CancellationToken.None)).ErrorCode);
        OperationResult<WindowEventCaptureStatus> stopped = await live.StopAsync(first.Value!.Id);
        Assert.Equal("Stopped", stopped.Value!.State);
        Assert.Equal("stopped", stopped.Value.StopReason);
        Assert.False(stopped.Value.IncludesChildProcesses);
    }

    private static async Task WaitStopped(WindowEventCaptureManager manager, string id)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (manager.Get(id).Value!.State is "Stopped" or "Failed")
            {
                return;
            }

            await Task.Delay(10);
        }
    }

    private static WindowEventMessage Ready(bool includeNew, string? handle = null) => new(
        "ready", Start, null, handle ?? "0x22", 10, Start, true, null, null, null, null, null, null, null, null,
        null, null, null, null, 100, Start, includeNew, false);

    private static WindowEventMessage Observation(string ev, string handle, int pid, DateTimeOffset start, int? dpi = null) => new(
        "observation", Start, ev, handle, pid, start, true, "Game", "Title",
        DesktopCoordinates.PhysicalScreenPixels, new DesktopRect(-8, -31, 100, 80), new DesktopRect(0, 0, 84, 41),
        dpi, null, null, null, null, null, null, null, null, null, false);

    private static string Line(WindowEventMessage message) => WindowEventLines.Observation(message);

    private sealed class ScriptFactory(Func<WindowEventStart, IWindowEventSession> create) : IWindowEventSessionFactory
    {
        public Task<OperationResult<IWindowEventSession>> StartAsync(WindowEventStart start, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult<IWindowEventSession>.Ok(create(start)));
    }

    private sealed class ScriptSession(IReadOnlyList<string> lines, bool hold) : IWindowEventSession
    {
        public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (string line in lines)
            {
                yield return line;
            }

            if (!hold)
            {
                yield break;
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public class WindowPlacementContractTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void Place_AchievesTheClientRectangleAndPreservesForeground()
    {
        FakePlacement windows = new();
        WindowPlacer placer = new(windows, new WindowPlacementStore(TimeProvider.System));
        WindowPlacementRequest request = new("0x11", 10, Start, 100, 200, 1920, 1200, DesktopCoordinates.PhysicalScreenPixels, false);

        OperationResult<WindowPlacementResult> result = placer.Place(request);

        Assert.True(result.Value!.Achieved);
        Assert.Equal(new DesktopRect(100, 200, 1920, 1200), result.Value.MeasuredClient);
        Assert.Equal(new DesktopRect(92, 169, 1936, 1239), result.Value.MeasuredRaw);
        Assert.True(result.Value.ActivationPreserved);
        Assert.Equal("0x1", result.Value.ForegroundAfter);
    }

    [Fact]
    public void Place_ReportsRejectionInsteadOfSuccess()
    {
        FakePlacement windows = new() { Reject = true };
        OperationResult<WindowPlacementResult> result = new WindowPlacer(windows, new WindowPlacementStore(TimeProvider.System))
            .Place(new WindowPlacementRequest("0x11", 10, Start, 100, 200, 1920, 1200, DesktopCoordinates.PhysicalScreenPixels, false));

        Assert.False(result.Value!.Achieved);
        Assert.Equal("placement_not_achieved", result.Value.StopCode);
        Assert.Equal(1, windows.Moves);
    }

    [Fact]
    public void Place_DoesNotMoveWhenIdentityOrDpiFails()
    {
        FakePlacement denied = new() { MeasureResult = OperationResult<PlacementSample>.Fail("window_identity_mismatch", "reused") };
        Assert.Equal("window_identity_mismatch", new WindowPlacer(denied, new WindowPlacementStore(TimeProvider.System))
            .Place(new WindowPlacementRequest("0x11", 10, Start, 1, 1, 10, 10, DesktopCoordinates.PhysicalScreenPixels, false)).ErrorCode);
        Assert.Equal(0, denied.Moves);

        FakePlacement dpi = new() { Sample = new PlacementSample(false, null, null, "dpi_context_unavailable", "no context") };
        Assert.Equal("dpi_context_unavailable", new WindowPlacer(dpi, new WindowPlacementStore(TimeProvider.System))
            .Place(new WindowPlacementRequest("0x11", 10, Start, 1, 1, 10, 10, DesktopCoordinates.PhysicalScreenPixels, false)).ErrorCode);
        Assert.Equal(0, dpi.Moves);
    }

    [Fact]
    public void Restore_ChecksTheTokenAndTheSavedRectangle()
    {
        ManualTime time = new();
        FakePlacement windows = new();
        WindowPlacementStore store = new(time);
        WindowPlacer placer = new(windows, store);
        OperationResult<WindowPlacementResult> saved = placer.Place(
            new WindowPlacementRequest("0x11", 10, Start, 10, 10, 20, 20, DesktopCoordinates.PhysicalScreenPixels, true));
        windows.ForegroundHandle = "0x9";

        OperationResult<WindowPlacementResult> restored = placer.Restore(saved.Value!.SavedPlacementToken);

        Assert.True(restored.Value!.Achieved);
        Assert.False(restored.Value.ActivationPreserved);
        Assert.Equal("placement_not_found", placer.Restore(saved.Value.SavedPlacementToken).ErrorCode);
        time.Now += TimeSpan.FromMinutes(11);
        OperationResult<WindowPlacementResult> expired = placer.Place(
            new WindowPlacementRequest("0x11", 10, Start, 10, 10, 20, 20, DesktopCoordinates.PhysicalScreenPixels, true));
        time.Now += TimeSpan.FromMinutes(11);
        Assert.Equal("placement_not_found", placer.Restore(expired.Value!.SavedPlacementToken).ErrorCode);
    }

    [Fact]
    public void Restore_RequiresTheSavedShowState()
    {
        FakePlacement windows = new();
        WindowPlacer placer = new(windows, new WindowPlacementStore(TimeProvider.System));
        OperationResult<WindowPlacementResult> saved = placer.Place(
            new WindowPlacementRequest("0x11", 10, Start, 10, 10, 20, 20, DesktopCoordinates.PhysicalScreenPixels, true));
        windows.ReportedShowCommand = 3;

        OperationResult<WindowPlacementResult> restored = placer.Restore(saved.Value!.SavedPlacementToken);

        Assert.False(restored.Value!.Achieved);
        Assert.Equal("placement_not_achieved", restored.Value.StopCode);
    }

    [Fact]
    public void TryParseHandle_DropsLeadingZeroes()
    {
        Assert.True(WindowEventLines.TryParseHandle("0x0000000A", out string handle));
        Assert.Equal("0xA", handle);
        Assert.False(WindowEventLines.TryParseHandle("0x0", out _));
    }

    [Fact]
    public async Task Tool_RequiresThePlacementGate()
    {
        WindowPlacementTools tools = new(
            new WindowPlacer(new FakePlacement(), new WindowPlacementStore(TimeProvider.System)),
            new WindowPlacementStore(TimeProvider.System),
            null!,
            null!,
            new VestigoOptions());

        OperationResult<WindowPlacementResult> blocked = await tools.SetWindowClientRect("0x11", 0, 0, 10, 10);

        Assert.Equal("window_placement_disabled", blocked.ErrorCode);
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakePlacement : IWindowPlacementSource
    {
        public PlacementSample Sample = new(true, new DesktopRect(-8, -31, 1936, 1239), new DesktopRect(0, 0, 1920, 1200), null, null);
        public OperationResult<PlacementSample>? MeasureResult;
        public bool Reject;
        public int Moves;
        public string? ForegroundHandle = "0x1";
        public int ReportedShowCommand = 1;
        private SavedWindowPlacement? _saved;

        public OperationResult<PlacementSample> Measure(string handle, int? expectedPid, DateTimeOffset? expectedStart) =>
            MeasureResult ?? OperationResult<PlacementSample>.Ok(Sample);

        public OperationResult<PlacementMove> MoveOuter(string handle, int? expectedPid, DateTimeOffset? expectedStart, DesktopRect outer)
        {
            Moves++;
            if (!Reject)
            {
                WindowInsets insets = WindowGeometryMath.Insets(Sample.Raw!, Sample.Client!);
                Sample = Sample with
                {
                    Raw = outer,
                    Client = new DesktopRect(
                        outer.X + insets.Left,
                        outer.Y + insets.Top,
                        outer.Width - insets.Left - insets.Right,
                        outer.Height - insets.Top - insets.Bottom)
                };
            }

            return OperationResult<PlacementMove>.Ok(new PlacementMove(true, null, null));
        }

        public OperationResult<SavedWindowPlacement> ReadPlacement(string handle, int? expectedPid, DateTimeOffset? expectedStart)
        {
            _saved = new SavedWindowPlacement(handle, 10, Start, true, ReportedShowCommand, -1, -1, -1, -1, -8, -31, 1936, 1239, 0);
            return OperationResult<SavedWindowPlacement>.Ok(_saved);
        }

        public OperationResult<PlacementMove> ApplyPlacement(SavedWindowPlacement placement)
        {
            ForegroundHandle = "0x2";
            Sample = Sample with { Raw = new DesktopRect(placement.NormalX, placement.NormalY, placement.NormalWidth, placement.NormalHeight) };
            return OperationResult<PlacementMove>.Ok(new PlacementMove(true, null, null));
        }

        public string? Foreground() => ForegroundHandle;
    }
}

public class DisplayLayoutContractTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-display-layout").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");

    public DisplayLayoutContractTests() => Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);

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
    public void Validate_RejectsAnUnknownTarget()
    {
        DisplayLayoutValidation validation = DisplayLayoutPlanner.Validate(
            Snap(Path(0, 0, 1920, 1080)),
            [Change(0, 0, 1920, 1080) with { MonitorDevicePath = @"\\?\DISPLAY#OTHER" }]);

        Assert.False(validation.Valid);
        Assert.Contains(validation.Errors, error => error.Contains("not in the active topology", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Transaction_ValidatesBeforeApplyAndRejectsASecondOwner()
    {
        FakeDisplays displays = new(Snap(Path(0, 0, 1920, 1080)));
        DisplayLayoutTransactionManager manager = Manager(displays);
        Assert.Equal("invalid_request", (await manager.StartAsync([Change(0, 0, 1920, 1080) with { AdapterLuid = "FFFFFFFF:FFFFFFFF" }], false, false, CancellationToken.None)).ErrorCode);
        Assert.Empty(displays.Applied);

        OperationResult<DisplayLayoutTransaction> started = await manager.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None);
        Assert.True(started.Success, started.Error);
        Assert.False(started.Value!.Persistent);
        Assert.False(displays.Applied[0].Persistent);
        Assert.Equal("display_transaction_busy", (await manager.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Observe_WaitsForStableGeometryAndReportsTimeout()
    {
        FakeDisplays displays = new(Snap(Path(0, 0, 1920, 1080))) { SnapshotsBeforeChanged = 2 };
        ManualTime time = new();
        DisplayLayoutTransactionManager manager = Manager(displays, time);
        manager.Delay = (span, _) =>
        {
            time.Now += span;
            return Task.CompletedTask;
        };
        OperationResult<DisplayLayoutTransaction> started = await manager.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None);

        OperationResult<DisplayLayoutObservation> stable = await manager.ObserveAsync(
            started.Value!.Id,
            new DisplayWait(TimeSpan.FromSeconds(5), 2, Change(0, 0, 1280, 720)),
            CancellationToken.None);
        Assert.True(stable.Value!.Stable);
        Assert.False(stable.Value.TimedOut);
        Assert.Equal(1280, stable.Value.Target!.Width);

        displays.Current = Snap(Path(10, 10, 800, 600));
        displays.SnapshotsBeforeChanged = 0;
        OperationResult<DisplayLayoutObservation> drifted = await manager.ObserveAsync(
            started.Value.Id,
            new DisplayWait(TimeSpan.FromMilliseconds(200), 2, null),
            CancellationToken.None);
        Assert.True(drifted.Value!.TimedOut);
        Assert.Contains(drifted.Value.Differences, difference => difference.Contains("fingerprint", StringComparison.Ordinal));
        Assert.Equal("active", manager.Get(started.Value.Id).Value!.State);
    }

    [Fact]
    public async Task Restore_KeepsTheTransactionWhenVerificationFailsAndRecoversAfterRestart()
    {
        FakeDisplays displays = new(Snap(Path(0, 0, 1920, 1080)));
        DisplayLayoutTransactionManager manager = Manager(displays);
        OperationResult<DisplayLayoutTransaction> started = await manager.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None);
        displays.FailApply = true;
        OperationResult<DisplayLayoutTransaction> failed = await manager.RestoreAsync(started.Value!.Id, CancellationToken.None);
        Assert.Equal("restore_failed", failed.Value!.State);
        Assert.False(failed.Value.RestorationVerified);
        Assert.Contains("NativeError=87", failed.Value.Error);
        Assert.Contains("rejected", failed.Value.Error);
        Assert.True(File.Exists(VestigoPaths.OwnedDisplayLayoutPath));
        Assert.Equal("display_transaction_busy", (await manager.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None)).ErrorCode);

        displays.FailApply = false;
        displays.Current = Snap(Path(0, 0, 1280, 720));
        DisplayLayoutTransactionManager recovered = Manager(displays);
        await recovered.StartAsync(CancellationToken.None);
        Assert.Equal("restored", recovered.Get(null).Value!.State);
        Assert.True(recovered.Get(null).Value!.RestorationVerified);
        Assert.False(File.Exists(VestigoPaths.OwnedDisplayLayoutPath));
    }

    [Fact]
    public async Task CancelDuringApply_RestoresTheOriginalLayout()
    {
        FakeDisplays displays = new(Snap(Path(0, 0, 1920, 1080))) { CancelFirstApply = true };
        DisplayLayoutTransactionManager manager = Manager(displays);

        OperationResult<DisplayLayoutTransaction> cancelled = await manager.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None);

        Assert.Equal("cancelled", cancelled.ErrorCode);
        Assert.Equal(2, displays.Applied.Count);
        Assert.True(displays.Applied[1].Persistent);
        Assert.Equal("restored", manager.Get(null).Value!.State);
    }

    [Fact]
    public async Task Start_RestoresWhenTheHelperResultIsUncertainAndDropsADefiniteRejection()
    {
        FakeDisplays uncertain = new(Snap(Path(0, 0, 1920, 1080))) { UncertainError = "helper_timeout" };
        DisplayLayoutTransactionManager manager = Manager(uncertain);
        OperationResult<DisplayLayoutTransaction> started = await manager.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None);
        Assert.Equal("helper_timeout", started.ErrorCode);
        Assert.Equal(2, uncertain.Applied.Count);
        Assert.True(uncertain.Applied[1].Persistent);
        Assert.Equal("restored", manager.Get(null).Value!.State);

        FakeDisplays rejected = new(Snap(Path(0, 0, 1920, 1080))) { FailApply = true };
        DisplayLayoutTransactionManager rejection = Manager(rejected);
        OperationResult<DisplayLayoutTransaction> failed = await rejection.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None);
        Assert.Equal("display_config_rejected", failed.ErrorCode);
        Assert.Contains("NativeError=87", failed.Error);
        Assert.Contains("Stage=apply", failed.Error);
        Assert.Single(rejected.Applied);
        Assert.False(File.Exists(VestigoPaths.OwnedDisplayLayoutPath));
        Assert.Equal("not_found", rejection.Get(null).ErrorCode);
    }

    [Fact]
    public async Task Start_KeepsTheCheckpointWhenTheTopologyWasNotApplied()
    {
        FakeDisplays displays = new(Snap(Path(0, 0, 1920, 1080))) { RejectBeforeApply = true };
        DisplayLayoutTransactionManager manager = Manager(displays);

        OperationResult<DisplayLayoutTransaction> failed = await manager.StartAsync([Change(0, 0, 1280, 720)], true, false, CancellationToken.None);

        Assert.Equal("display_mode_unavailable", failed.ErrorCode);
        Assert.Contains("A requested target was not in the active topology", failed.Error);
        Assert.DoesNotContain("Rollback", failed.Error);
        Assert.Contains(manager.Get(null).Value!.Id, failed.Error);
        Assert.Contains("get_display_layout_transaction", failed.Error);
        Assert.Contains("restore_display_layout_transaction", failed.Error);
        Assert.Single(displays.Applied);
        Assert.True(File.Exists(VestigoPaths.OwnedDisplayLayoutPath));
    }

    [Fact]
    public async Task Restore_SkipsInactivePathsFromTheOriginalSnapshot()
    {
        DisplayPathLayout active = Path(0, 0, 1920, 1080);
        DisplayPathLayout idle = active with
        {
            Active = false,
            Width = 0,
            Height = 0,
            Identity = new DisplayTargetIdentity(@"\\?\DISPLAY#IDLE", 1, "00000000:00000001", 9, 0)
        };
        FakeDisplays displays = new(DisplayLayoutPlanner.WithFingerprint(new DisplayLayoutSnapshot(
            DateTimeOffset.UnixEpoch,
            1,
            DesktopCoordinates.PhysicalScreenPixels,
            string.Empty,
            [active, idle])));
        DisplayLayoutTransactionManager manager = Manager(displays);

        OperationResult<DisplayLayoutTransaction> started = await manager.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None);
        OperationResult<DisplayLayoutTransaction> restored = await manager.RestoreAsync(started.Value!.Id, CancellationToken.None);

        Assert.Equal("restored", restored.Value!.State);
        Assert.Equal([@"\\?\DISPLAY#TEST"], displays.Applied[^1].Targets.Select(target => target.MonitorDevicePath));
    }

    [Fact]
    public async Task Restore_LeavesAnAlreadyRestoredTransactionUntouched()
    {
        FakeDisplays displays = new(Snap(Path(0, 0, 1920, 1080)));
        DisplayLayoutTransactionManager manager = Manager(displays);
        OperationResult<DisplayLayoutTransaction> started = await manager.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None);
        OperationResult<DisplayLayoutTransaction> restored = await manager.RestoreAsync(started.Value!.Id, CancellationToken.None);
        Assert.Equal("restored", restored.Value!.State);
        int applies = displays.Applied.Count;

        OperationResult<DisplayLayoutTransaction> again = await manager.RestoreAsync(started.Value.Id, CancellationToken.None);

        Assert.Equal("restored", again.Value!.State);
        Assert.Equal(applies, displays.Applied.Count);
    }

    [Fact]
    public void ModePatch_PreservesSignalAndRefreshForAMove()
    {
        DisplayPathLayout current = Path(0, 1200, 1920, 1200) with { SignalWidth = 1920, SignalHeight = 1200 };
        DisplayModePatch move = DisplayModePatch.For(current, Change(1920, 0, 1920, 1200));
        DisplayModePatch same = DisplayModePatch.For(current, Change(0, 1200, 1920, 1200));
        DisplayModePatch refresh = DisplayModePatch.For(current, Change(0, 1200, 1920, 1200) with { RefreshNumerator = 120, RefreshDenominator = 1 });
        DisplayModePatch scaled = DisplayModePatch.For(current with { SignalWidth = 1280, SignalHeight = 720 }, Change(0, 0, 800, 600));

        Assert.True(move.Move);
        Assert.False(move.ResizeSignal);
        Assert.False(move.ChangeRefresh);
        Assert.True(move.AnyChange);
        Assert.False(same.AnyChange);
        Assert.True(refresh.ChangeRefresh);
        Assert.False(refresh.ResizeSignal);
        Assert.True(scaled.Unsupported);
        Assert.True(WindowsDisplayConfig.SeparatesValidationFromApply(false));
        Assert.True(WindowsDisplayConfig.SeparatesValidationFromApply(true));
    }

    [Fact]
    public async Task UnchangedApply_DoesNotLeaveATransactionOpen()
    {
        FakeDisplays displays = new(Snap(Path(0, 1200, 1920, 1200))) { ReportUnchanged = true };
        DisplayLayoutTransactionManager manager = Manager(displays);

        OperationResult<DisplayLayoutTransaction> started = await manager.StartAsync([Change(0, 1200, 1920, 1200)], false, false, CancellationToken.None);

        Assert.True(started.Success);
        Assert.Equal("unchanged", started.Value!.State);
        Assert.True(started.Value.Unchanged);
        Assert.False(manager.Get(null).Success);
    }

    [Fact]
    public async Task AppliedWithoutASnapshot_LeavesCurrentNull()
    {
        FakeDisplays displays = new(Snap(Path(0, 0, 1920, 1080))) { OmitAfter = true };
        DisplayLayoutTransactionManager manager = Manager(displays);

        OperationResult<DisplayLayoutTransaction> started = await manager.StartAsync([Change(10, 0, 1920, 1080)], false, false, CancellationToken.None);

        Assert.True(started.Success, started.Error);
        Assert.Equal("active", started.Value!.State);
        Assert.Null(started.Value.Current);
    }

    [Fact]
    public async Task Tool_RequiresTheDisplayGate()
    {
        DisplayLayoutTools tools = new(Manager(new FakeDisplays(Snap(Path(0, 0, 1920, 1080)))), new VestigoOptions());

        Assert.Equal("display_layout_disabled", (await tools.StartDisplayLayoutTransaction([Change(0, 0, 1280, 720)])).ErrorCode);
    }

    [Fact]
    public async Task ManagedDesktop_RunsApplyObserveAndRestoreOnTheInputDesktop()
    {
        FakeDisplays displays = new(Snap(Path(0, 0, 1920, 1080))) { RejectWithAccessDeniedOffDesktop = true };
        FakeDesktops desktops = new(input: true);
        DisplayLayoutTransactionManager manager = new(displays, new VestigoOptions(), TimeProvider.System, desktops);

        OperationResult<DisplayLayoutTransaction> started = await manager.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None, "abc");
        Assert.True(started.Success, started.Error);
        Assert.Equal("abc", started.Value!.DesktopSessionId);
        Assert.All(displays.ApplyPaths, path => Assert.Equal(FakeDesktops.DesktopPath, path));
        Assert.All(displays.SnapshotPaths, path => Assert.Equal(FakeDesktops.DesktopPath, path));

        OperationResult<DisplayLayoutObservation> observed = await manager.ObserveAsync(
            started.Value.Id, new DisplayWait(TimeSpan.FromSeconds(5), 1, Change(0, 0, 1280, 720)), CancellationToken.None);
        Assert.True(observed.Value!.Stable);
        Assert.Equal(FakeDesktops.DesktopPath, displays.SnapshotPaths[^1]);

        OperationResult<DisplayLayoutTransaction> restored = await manager.RestoreAsync(started.Value.Id, CancellationToken.None);
        Assert.True(restored.Value!.RestorationVerified);
        Assert.Equal(FakeDesktops.DesktopPath, displays.ApplyPaths[^1]);
        Assert.Equal(0, desktops.Switches);
    }

    [Fact]
    public async Task ManagedDesktop_RefusesAnInactiveDesktopAndHintsWhenTheDefaultDesktopIsDenied()
    {
        FakeDisplays displays = new(Snap(Path(0, 0, 1920, 1080))) { RejectWithAccessDeniedOffDesktop = true };
        DisplayLayoutTransactionManager inactive = new(displays, new VestigoOptions(), TimeProvider.System, new FakeDesktops(input: false));
        OperationResult<DisplayLayoutTransaction> refused = await inactive.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None, "abc");
        Assert.Equal(DesktopHelperTargeting.Unsupported, refused.ErrorCode);
        Assert.Empty(displays.Applied);

        OperationResult<DisplayLayoutTransaction> denied = await inactive.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None);
        Assert.Equal("display_config_rejected", denied.ErrorCode);
        Assert.Contains("NativeError=5", denied.Error);
        Assert.Contains("desktopSessionId", denied.Error);
        Assert.Null(inactive.Get(null).Value);
    }

    [Fact]
    public async Task ManagedDesktop_RestoreFallsBackToTheDefaultDesktopAfterTheSessionEnds()
    {
        FakeDisplays displays = new(Snap(Path(0, 0, 1920, 1080)));
        FakeDesktops desktops = new(input: true);
        DisplayLayoutTransactionManager manager = new(displays, new VestigoOptions(), TimeProvider.System, desktops);
        OperationResult<DisplayLayoutTransaction> started = await manager.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None, "abc");

        desktops.Input = false;
        OperationResult<DisplayLayoutTransaction> restored = await manager.RestoreAsync(started.Value!.Id, CancellationToken.None);

        Assert.True(restored.Value!.RestorationVerified);
        Assert.Null(displays.ApplyPaths[^1]);
    }

    [Fact]
    public async Task ManagedDesktop_RestoreFollowsAnotherManagedDesktopThatIsNowTheInput()
    {
        FakeDisplays displays = new(Snap(Path(0, 0, 1920, 1080)));
        FakeDesktops desktops = new(input: true);
        DisplayLayoutTransactionManager manager = new(displays, new VestigoOptions(), TimeProvider.System, desktops);
        OperationResult<DisplayLayoutTransaction> started = await manager.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None, "abc");

        desktops.Input = false;
        desktops.OtherInput = @"winsta0\Vestigo-ffffffffffff";
        await manager.RestoreAsync(started.Value!.Id, CancellationToken.None);

        Assert.Equal(@"winsta0\Vestigo-ffffffffffff", displays.ApplyPaths[^1]);
    }

    [Fact]
    public async Task ManagedDesktop_ObserveLooksUpOnceAndAgainOnlyAfterAFailedSnapshot()
    {
        FakeDisplays displays = new(Snap(Path(0, 0, 1920, 1080))) { SnapshotsBeforeChanged = 3 };
        FakeDesktops desktops = new(input: true);
        ManualTime time = new();
        DisplayLayoutTransactionManager manager = new(displays, new VestigoOptions(), time, desktops)
        {
            Delay = (span, _) =>
            {
                time.Now += span;
                return Task.CompletedTask;
            }
        };
        OperationResult<DisplayLayoutTransaction> started = await manager.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None, "abc");
        int before = desktops.Lookups;

        await manager.ObserveAsync(started.Value!.Id, new DisplayWait(TimeSpan.FromSeconds(5), 1, Change(0, 0, 1280, 720)), CancellationToken.None);

        Assert.Equal(before + 1, desktops.Lookups);
    }

    [Fact]
    public async Task ManagedDesktop_ObserveRetriesAFailedSnapshotOnceAndStaysWithinTheDeadline()
    {
        FakeDisplays displays = new(Snap(Path(0, 0, 1920, 1080)));
        FakeDesktops desktops = new(input: true);
        DisplayLayoutTransactionManager manager = new(displays, new VestigoOptions(), TimeProvider.System, desktops);
        OperationResult<DisplayLayoutTransaction> started = await manager.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None, "abc");
        displays.FailSnapshots = true;
        displays.SnapshotPaths.Clear();
        desktops.AfterLookup = () => desktops.Input = !desktops.Input;

        OperationResult<DisplayLayoutObservation> observed = await manager.ObserveAsync(
            started.Value!.Id, new DisplayWait(TimeSpan.FromSeconds(5), 1, null), CancellationToken.None);

        Assert.False(observed.Success);
        Assert.Equal([FakeDesktops.DesktopPath, null], displays.SnapshotPaths);
    }

    [Fact]
    public async Task Observe_BoundsAHungSnapshotByTheWaitButStillHonoursCallerCancellation()
    {
        FakeDisplays displays = new(Snap(Path(0, 0, 1920, 1080)));
        DisplayLayoutTransactionManager manager = Manager(displays);
        OperationResult<DisplayLayoutTransaction> started = await manager.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None);
        displays.HangSnapshots = true;

        OperationResult<DisplayLayoutObservation> timedOut = await manager.ObserveAsync(
            started.Value!.Id, new DisplayWait(TimeSpan.FromMilliseconds(100), 1, null), CancellationToken.None);
        Assert.Equal("helper_timeout", timedOut.ErrorCode);

        using CancellationTokenSource caller = new(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ObserveAsync(
            started.Value.Id, new DisplayWait(TimeSpan.FromSeconds(30), 1, null), caller.Token));
    }

    [Fact]
    public async Task ManagedDesktop_FailedLookupIsNotTreatedAsTheDefaultDesktop()
    {
        FakeDisplays displays = new(Snap(Path(0, 0, 1920, 1080)));
        FakeDesktops desktops = new(input: true);
        DisplayLayoutTransactionManager manager = new(displays, new VestigoOptions(), TimeProvider.System, desktops);
        OperationResult<DisplayLayoutTransaction> started = await manager.StartAsync([Change(0, 0, 1280, 720)], false, false, CancellationToken.None, "abc");
        desktops.FailLookup = true;
        int applies = displays.Applied.Count;

        OperationResult<DisplayLayoutObservation> observed = await manager.ObserveAsync(
            started.Value!.Id, new DisplayWait(TimeSpan.FromSeconds(1), 1, null), CancellationToken.None);
        OperationResult<DisplayLayoutTransaction> restored = await manager.RestoreAsync(started.Value.Id, CancellationToken.None);

        Assert.Equal("desktop_unavailable", observed.ErrorCode);
        Assert.Equal("restore_failed", restored.Value!.State);
        Assert.Contains("could not be determined", restored.Value.Error);
        Assert.Equal(applies, displays.Applied.Count);

        desktops.FailLookup = false;
        Assert.True((await manager.RestoreAsync(started.Value.Id, CancellationToken.None)).Value!.RestorationVerified);
    }

    [Fact]
    public void PlacementAndDisplayTools_ExposeDesktopSessionIdInTheirSchemas()
    {
        foreach ((Type type, string method) in new[]
        {
            (typeof(WindowPlacementTools), nameof(WindowPlacementTools.SetWindowClientRect)),
            (typeof(WindowPlacementTools), nameof(WindowPlacementTools.RestoreWindowPlacement)),
            (typeof(DisplayLayoutTools), nameof(DisplayLayoutTools.SnapshotDisplayLayout)),
            (typeof(DisplayLayoutTools), nameof(DisplayLayoutTools.StartDisplayLayoutTransaction))
        })
        {
            ModelContextProtocol.Server.McpServerTool tool = ModelContextProtocol.Server.McpServerTool.Create(
                type.GetMethod(method)!, RuntimeHelpers.GetUninitializedObject(type), new ModelContextProtocol.Server.McpServerToolCreateOptions { Services = null });
            Assert.True(tool.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("desktopSessionId", out _), method);
        }
    }

    private sealed class FakeDesktops(bool input) : IManagedDesktopDirectory
    {
        public const string DesktopPath = @"winsta0\Vestigo-0123456789ab";

        public bool Input { get; set; } = input;

        public int Switches { get; }

        public string? OtherInput { get; set; }

        public int Lookups { get; private set; }

        public bool FailLookup { get; set; }

        public Action? AfterLookup { get; set; }

        public Task<OperationResult<string?>> InputDesktopPathAsync(CancellationToken cancellationToken)
        {
            Lookups++;
            OperationResult<string?> result = FailLookup
                ? OperationResult<string?>.Fail("desktop_unavailable", "The input desktop could not be read.")
                : OperationResult<string?>.Ok(OtherInput ?? (Input ? DesktopPath : null));
            AfterLookup?.Invoke();
            return Task.FromResult(result);
        }

        public Task<OperationResult<ManagedDesktopLease>> LeaseForLaunchAsync(string sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<DesktopHelperTarget>> DescribeAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult<DesktopHelperTarget>.Ok(new DesktopHelperTarget(sessionId, DesktopPath, "Vestigo-0123456789ab", Input)));
    }

    private static DisplayLayoutTransactionManager Manager(FakeDisplays displays, TimeProvider? time = null) =>
        new(displays, new VestigoOptions(), time ?? TimeProvider.System);

    private static DisplayPathLayout Path(int x, int y, int width, int height) => new(
        new DisplayTargetIdentity(@"\\?\DISPLAY#TEST", 1, "00000000:00000001", 2, 0),
        @"\\.\DISPLAY1",
        true,
        x,
        y,
        width,
        height,
        60000,
        1000,
        "IDENTITY",
        "IDENTITY",
        DesktopCoordinates.PhysicalScreenPixels);

    private static DisplayLayoutSnapshot Snap(DisplayPathLayout path) => new(
        DateTimeOffset.UnixEpoch,
        1,
        DesktopCoordinates.PhysicalScreenPixels,
        DisplayLayoutPlanner.Fingerprint([path]),
        [path]);

    private static DisplayTargetChange Change(int x, int y, int width, int height) => new(
        @"\\?\DISPLAY#TEST",
        1,
        "00000000:00000001",
        2,
        x,
        y,
        width,
        height,
        null,
        null,
        null);

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeDisplays(DisplayLayoutSnapshot original) : IDisplayLayoutClient
    {
        private int _snapshots;

        public DisplayLayoutSnapshot Current = original;
        public int SnapshotsBeforeChanged;
        public bool FailApply;
        public bool FailSnapshots;
        public bool HangSnapshots;
        public bool RejectBeforeApply;
        public bool ReportUnchanged;
        public bool OmitAfter;
        public bool CancelFirstApply;
        public string? UncertainError;
        public bool RejectWithAccessDeniedOffDesktop;
        public List<DisplayLayoutRequest> Applied { get; } = [];
        public List<string?> ApplyPaths { get; } = [];
        public List<string?> SnapshotPaths { get; } = [];

        public async Task<OperationResult<DisplayLayoutSnapshot>> SnapshotAsync(bool elevated, CancellationToken cancellationToken, string? desktopPath = null)
        {
            SnapshotPaths.Add(desktopPath);
            if (HangSnapshots)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (FailSnapshots)
            {
                return OperationResult<DisplayLayoutSnapshot>.Fail("helper_failed", "snapshot failed");
            }

            _snapshots++;
            DisplayLayoutSnapshot snapshot = _snapshots <= SnapshotsBeforeChanged ? original : Current;
            return OperationResult<DisplayLayoutSnapshot>.Ok(snapshot);
        }

        public Task<OperationResult<DisplayLayoutApplyResult>> ApplyAsync(
            DisplayLayoutRequest request,
            bool elevated,
            CancellationToken cancellationToken,
            string? desktopPath = null)
        {
            Applied.Add(request);
            ApplyPaths.Add(desktopPath);
            if (RejectWithAccessDeniedOffDesktop && desktopPath is null)
            {
                return Task.FromResult(OperationResult<DisplayLayoutApplyResult>.Ok(
                    new DisplayLayoutApplyResult(false, false, request.Persistent, 5, "display_config_rejected", "SetDisplayConfig validate failed.", Current, null, "validate")));
            }

            if (CancelFirstApply && Applied.Count == 1)
            {
                throw new OperationCanceledException();
            }

            if (UncertainError is not null && Applied.Count == 1)
            {
                return Task.FromResult(OperationResult<DisplayLayoutApplyResult>.Fail(UncertainError, "The helper did not return a result."));
            }

            if (RejectBeforeApply)
            {
                return Task.FromResult(OperationResult<DisplayLayoutApplyResult>.Ok(
                    new DisplayLayoutApplyResult(false, true, request.Persistent, null, "display_mode_unavailable", "A requested target was not in the active topology.", Current, null, "validate")));
            }

            if (FailApply)
            {
                return Task.FromResult(OperationResult<DisplayLayoutApplyResult>.Ok(
                    new DisplayLayoutApplyResult(false, true, request.Persistent, 87, "display_config_rejected", "rejected", Current, Current)));
            }

            Current = Snap(Path(request.Targets[0].X, request.Targets[0].Y, request.Targets[0].Width, request.Targets[0].Height));
            return Task.FromResult(OperationResult<DisplayLayoutApplyResult>.Ok(
                new DisplayLayoutApplyResult(true, true, request.Persistent, null, null, null, null, OmitAfter ? null : Current, Unchanged: ReportUnchanged)));
        }
    }
}
