using System.Text.Json;

using Vestigo.Configuration;
using Vestigo.Desktop;
using Vestigo.Devices;
using Vestigo.Diagnostics;
using Vestigo.Jobs;
using Vestigo.Processes;

namespace Vestigo.Server.Tests;

public class GraphicsDiagnosticsTests
{
    [Fact]
    public void Matcher_PrefersAnExactPresentDeviceAndReportsATie()
    {
        DeviceEntry exact = Device("PCI\\VEN_10DE&DEV_2204&SUBSYS_147A10DE&REV_A1", present: true, "exact");
        DeviceEntry other = Device("PCI\\VEN_10DE&DEV_2204", present: true, "device-only");
        (string? id, string? confidence) = GraphicsAdapterMatcher.Match(0x10DE, 0x2204, 0x147A10DE, 0xA1, [exact, other]);

        Assert.Equal("exact", id);
        Assert.Equal(GraphicsAdapterMatcher.Exact, confidence);

        DeviceEntry left = Device("PCI\\VEN_8086&DEV_1234", present: true, "left");
        DeviceEntry right = Device("PCI\\VEN_8086&DEV_1234", present: true, "right");
        (string? tied, string? tie) = GraphicsAdapterMatcher.Match(0x8086, 0x1234, null, null, [left, right]);
        Assert.Null(tied);
        Assert.Equal(GraphicsAdapterMatcher.Ambiguous, tie);
        Assert.Null(GraphicsAdapterMatcher.DriverVersion(0));
        Assert.Equal("10.0.1.2", GraphicsAdapterMatcher.DriverVersion((10L << 48) | (0L << 32) | (1L << 16) | 2));
    }

    [Fact]
    public void DisplaySignal_LeavesUnknownNumbersNull()
    {
        Assert.Null(DisplaySignalReader.RefreshHz(0, 1000));
        Assert.Null(DisplaySignalReader.RefreshHz(60, 0));
        Assert.Equal(59.94, DisplaySignalReader.RefreshHz(59940, 1000)!.Value, 2);
        Assert.Null(DisplaySignalReader.Count(0));
        Assert.Equal(1920, DisplaySignalReader.Count(1920));
        Assert.Null(DisplaySignalReader.Luid(0, 0));
        Assert.Equal("0000000A:0000000B", DisplaySignalReader.Luid(0xA, 0xB));
    }

    [Fact]
    public void SessionHelper_RoundTripsProbeCaptureArguments()
    {
        string[] args = SessionHelper.BuildProbeCaptureArguments(@"\\.\DISPLAY2", "dxgi", 250).ToArray();

        Assert.True(SessionHelper.TryParseProbeCapture(args, out string target, out string backend, out int duration, out string? error));
        Assert.Equal(@"\\.\DISPLAY2", target);
        Assert.Equal("dxgi", backend);
        Assert.Equal(250, duration);
        Assert.Null(error);
        Assert.False(SessionHelper.TryParseProbeCapture([SessionHelper.Flag, SessionHelper.ProbeCaptureCommand, "--backend", "gdi"], out _, out _, out _, out _));
    }

    [Fact]
    public async Task Probe_RequiresTheGateAndDoesNotReturnPixels()
    {
        RecordingProbe recording = new();
        CaptureProbeTools disabled = new(recording, new VestigoOptions());
        Assert.Equal("probes_disabled", (await disabled.ProbeCaptureSupport()).ErrorCode);
        Assert.False(recording.Called);

        VestigoOptions enabled = new() { Diagnostics = { AllowCaptureProbes = true, MaxProbeDuration = TimeSpan.FromSeconds(1) } };
        CaptureProbeTools tools = new(recording, enabled);
        OperationResult<CaptureProbeResult> result = await tools.ProbeCaptureSupport(durationMs: 5_000);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1000, recording.DurationMs);
        string json = JsonSerializer.Serialize(result.Value, SessionHelper.Json);
        Assert.DoesNotContain("pixel", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("png", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("base64", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_ReportsUnsupportedWgcAndNoConsoleUser()
    {
        UnavailableLauncher launcher = new();
        SessionCaptureProbe probe = new(launcher, new SessionHelperClient(launcher));

        Assert.Equal("unsupported", (await probe.ProbeAsync("primary", "wgc", 100, CancellationToken.None)).ErrorCode);
        Assert.Equal("user_not_available", (await probe.ProbeAsync("primary", "dxgi", 100, CancellationToken.None)).ErrorCode);
        Assert.False(launcher.Started);
    }

    [Fact]
    public void ProbeLoop_DisposesTheSourceWhenCancelled()
    {
        CancelSource source = new();

        Assert.Throws<OperationCanceledException>(() =>
            CaptureProbeLoop.Run(source, TimeSpan.FromSeconds(2), new CancellationToken(canceled: true)));
        Assert.True(source.Disposed);
    }

    private static DeviceEntry Device(string hardwareId, bool present, string instance) =>
        new(instance, "Adapter", GraphicsAdapterMatcher.DisplayClass, "Display", [hardwareId], null, null, present);

    private sealed class RecordingProbe : ICaptureProbe
    {
        public bool Called { get; private set; }

        public int DurationMs { get; private set; }

        public Task<OperationResult<CaptureProbeResult>> ProbeAsync(string target, string backend, int durationMs, CancellationToken cancellationToken)
        {
            Called = true;
            DurationMs = durationMs;
            CaptureProbeResult result = new(1, "Default", backend, target, new CaptureAdvertised(true, true), new CaptureMeasured(0, null, null, null));
            return Task.FromResult(OperationResult<CaptureProbeResult>.Ok(result));
        }
    }

    private sealed class UnavailableLauncher : IProcessLauncher
    {
        public bool Started { get; private set; }

        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) =>
            OperationResult<bool>.Fail("no_interactive_session", "No user is logged on at the console.");

        public Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken)
        {
            Started = true;
            throw new InvalidOperationException("The probe started a process.");
        }
    }

    private sealed class CancelSource : ICaptureFrameSource
    {
        public bool Disposed { get; private set; }

        public bool TryAcquire(int timeoutMs, out int hresult)
        {
            hresult = 0;
            return false;
        }

        public void Dispose() => Disposed = true;
    }
}
