using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading.Channels;

using Microsoft.Extensions.Logging.Abstractions;

using Vestigo.Build;
using Vestigo.Configuration;
using Vestigo.DebugOutput;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

public class DebugViewLineParserTests
{
    [Fact]
    public void TryParse_SkipsBannerAndSplitsTabsInsideTheMessage()
    {
        Assert.True(DebugViewLineParser.IsBanner("DebugViewConsole 1.30.0.0"));
        Assert.True(DebugViewLineParser.IsBanner("Listening for OutputDebugString messages..."));
        Assert.False(DebugViewLineParser.TryParse("Listening for OutputDebugString messages...", out _));

        Assert.True(DebugViewLineParser.TryParse("12:00:00.000\t4242\tapp.exe\t\thello\tworld", out DebugViewMessage? message));
        Assert.Equal("12:00:00.000", message!.Time);
        Assert.Equal(4242, message.Pid);
        Assert.Equal("app.exe", message.ProcessName);
        Assert.Equal("hello\tworld", message.Message);
    }

    [Fact]
    public void TryParse_AllowsAnEmptyProcessName()
    {
        Assert.True(DebugViewLineParser.TryParse("12:00:00.000\t8\t\t\tkernel-ish", out DebugViewMessage? message));
        Assert.Equal(8, message!.Pid);
        Assert.Equal("", message.ProcessName);
        Assert.Equal("kernel-ish", message.Message);
    }

    [Fact]
    public void ClassifiesStderr()
    {
        Assert.True(DebugViewLineParser.IsDbWinInUse("Unexpected error occurred: CreateDBWinBufferMapping failed"));
        Assert.True(DebugViewLineParser.IsDbWinInUse("Another DebugView++ (or similar application) might be running."));
        Assert.True(DebugViewLineParser.IsGlobalCaptureWarning("Unable to capture Global Win32 messages: access denied"));
        Assert.False(DebugViewLineParser.IsDbWinInUse("Listening for OutputDebugString messages..."));
    }
}

public class DebugViewArgumentsTests
{
    [Fact]
    public void Build_EmitsTheFixedFlagSetAndMappedFilters()
    {
        OperationResult<IReadOnlyList<string>> result = DebugViewArguments.Build(
            new DebugOutputOptions(),
            ["alpha"],
            ["beta"],
            ["app.exe"],
            ["noise.exe"]);

        Assert.True(result.Success);
        Assert.Equal(
            ["-c", "-a", "-f", "-s", "-p", "-n", "-q", "-i", "alpha", "-e", "beta", "--include-process", "app.exe", "--exclude-process", "noise.exe"],
            result.Value);
    }

    [Fact]
    public void Build_RejectsInjectionAndOverLimitValues()
    {
        DebugOutputOptions options = new() { MaxFilterPatterns = 1, MaxPatternLength = 4 };
        Assert.Equal("invalid_request", DebugViewArguments.Build(options, ["ab"], ["cd"], [], []).ErrorCode);
        Assert.Equal("invalid_request", DebugViewArguments.Build(new DebugOutputOptions(), ["-evil"], [], [], []).ErrorCode);
        Assert.Equal("invalid_request", DebugViewArguments.Build(new DebugOutputOptions(), ["a\nb"], [], [], []).ErrorCode);
        Assert.Equal("invalid_request", DebugViewArguments.Build(options, ["abcde"], [], [], []).ErrorCode);
        Assert.Equal("invalid_request", DebugViewArguments.Build(new DebugOutputOptions(), [""], [], [], []).ErrorCode);
    }
}

public class DebugViewPinTests
{
    [Fact]
    public void Pin_MatchesServerDefaultAndIsWellFormed()
    {
        Assert.Equal(DebugOutputOptions.DefaultExpectedSha256, DebugViewPin.Sha256);
        Assert.Equal(DebugViewPin.Sha256, new DebugOutputOptions().ExpectedSha256);
        Assert.Matches("^[0-9a-f]{64}$", DebugViewPin.Sha256);
        Assert.True(Uri.TryCreate(DebugViewPin.AssetUrl, UriKind.Absolute, out Uri? url));
        Assert.Equal(Uri.UriSchemeHttps, url.Scheme);
        Assert.Equal("github.com", url.Host);
        Assert.Contains(DebugViewPin.Version, DebugViewPin.AssetUrl, StringComparison.Ordinal);
        Assert.Equal("DebugViewConsole.exe", DebugViewPin.Executable);
    }
}

[Collection("VestigoHost")]
public class DebugOutputManagerTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-debugview").FullName;

    public DebugOutputManagerTests()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);
        Directory.CreateDirectory(VestigoPaths.SpoolDirectory);
    }

    [Fact]
    public async Task Start_RejectsASecondCaptureWhileOneIsRunning()
    {
        FakeDebugViewProcess process = new() { WaitUntilCancelled = true };
        DebugOutputManager manager = CreateManager(process, out _, out _);
        OperationResult<DebugCaptureStatus> first = await manager.StartCaptureAsync(EmptyRequest(), CancellationToken.None);
        OperationResult<DebugCaptureStatus> second = await manager.StartCaptureAsync(EmptyRequest(), CancellationToken.None);

        Assert.True(first.Success);
        Assert.Equal("capture_busy", second.ErrorCode);
        await manager.StopAsync(first.Value!.Id);
    }

    [Fact]
    public async Task Start_RejectsAMissingExecutableAndAHashMismatch()
    {
        FakeDebugViewProcess process = new();
        DebugOutputManager manager = CreateManager(process, out VestigoOptions options, out string exe);
        options.DebugOutput.ExecutablePath = Path.Combine(_settings, "missing.exe");
        OperationResult<DebugCaptureStatus> missing = await manager.StartCaptureAsync(EmptyRequest(), CancellationToken.None);

        options.DebugOutput.ExecutablePath = exe;
        options.DebugOutput.ExpectedSha256 = DebugOutputOptions.DefaultExpectedSha256;
        OperationResult<DebugCaptureStatus> mismatch = await manager.StartCaptureAsync(EmptyRequest(), CancellationToken.None);

        Assert.Equal("executable_missing", missing.ErrorCode);
        Assert.Equal("hash_mismatch", mismatch.ErrorCode);
        Assert.Empty(process.Started);
    }

    [Fact]
    public async Task Start_MapsDbWinInUseFromStderr()
    {
        FakeDebugViewProcess process = new()
        {
            Lines =
            [
                new DebugViewLine("Unexpected error occurred: CreateDBWinBufferMapping failed", true)
            ]
        };
        DebugOutputManager manager = CreateManager(process, out _, out _);
        OperationResult<DebugCaptureStatus> started = await manager.StartCaptureAsync(EmptyRequest(), CancellationToken.None);
        DebugCaptureStatus completed = await WaitForTerminalAsync(manager, started.Value!.Id);

        Assert.Equal("Failed", completed.State);
        Assert.Equal("dbwin_in_use", completed.StopReason);
    }

    [Fact]
    public async Task ReadLoop_RecordsTheMessageThatReachesTheLimitAndStopsOnDuration()
    {
        FakeDebugViewProcess process = new()
        {
            Lines =
            [
                Line("12:00:00.000", 1, "a.exe", "one"),
                Line("12:00:00.001", 1, "a.exe", "two")
            ]
        };
        DebugOutputManager manager = CreateManager(process, out VestigoOptions options, out _);
        options.DebugOutput.MaxMessages = 1;
        OperationResult<DebugCaptureStatus> limited = await manager.StartCaptureAsync(EmptyRequest(), CancellationToken.None);
        DebugCaptureStatus limitStatus = await WaitForTerminalAsync(manager, limited.Value!.Id);

        process.Lines =
        [
            Line("12:00:00.000", 1, "a.exe", "late")
        ];
        process.WaitUntilCancelled = true;
        options.DebugOutput.MaxMessages = 1_000_000;
        OperationResult<DebugCaptureStatus> timed = await manager.StartCaptureAsync(
            EmptyRequest() with { Duration = TimeSpan.FromMilliseconds(50) },
            CancellationToken.None);
        DebugCaptureStatus durationStatus = await WaitForTerminalAsync(manager, timed.Value!.Id);

        Assert.Equal("limit_reached", limitStatus.StopReason);
        Assert.Equal(1, limitStatus.MessageCount);
        Assert.Equal("duration_elapsed", durationStatus.StopReason);
    }

    [Fact]
    public async Task Stop_CancelsARunningCaptureAndCleansTheOwnedProcessFile()
    {
        FakeDebugViewProcess process = new() { WaitUntilCancelled = true };
        DebugOutputManager manager = CreateManager(process, out _, out _);
        OperationResult<DebugCaptureStatus> started = await manager.StartCaptureAsync(EmptyRequest(), CancellationToken.None);
        await WaitForProcessIdAsync(manager, started.Value!.Id);

        OperationResult<DebugCaptureStatus> stopped = await manager.StopAsync(started.Value.Id);

        Assert.Equal("Stopped", stopped.Value!.State);
        Assert.Equal("stopped", stopped.Value.StopReason);
        Assert.False(File.Exists(VestigoPaths.OwnedDebugViewPath));
    }

    [Fact]
    public async Task ReadAsync_ReturnsAsSoonAsAMessageArrives()
    {
        FakeDebugViewProcess process = new() { Feed = Channel.CreateUnbounded<DebugViewLine>() };
        DebugOutputManager manager = CreateManager(process, out VestigoOptions options, out _);
        options.MaxReadWait = TimeSpan.FromSeconds(10);
        OperationResult<DebugCaptureStatus> started = await manager.StartCaptureAsync(EmptyRequest(), CancellationToken.None);
        string id = started.Value!.Id;

        Task<OperationResult<DebugCapturePage>> pending = manager.ReadAsync(
            id, 0, null, TimeSpan.FromSeconds(10), null, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);
        await process.Feed.Writer.WriteAsync(Line("12:00:00.000", 9, "feed.exe", "hello"));

        DebugCapturePage page = (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Value!;
        Assert.Single(page.Events);
        Assert.Contains("hello", page.Events[0], StringComparison.Ordinal);
        Assert.False(page.Completed);
        await manager.StopAsync(id);
    }

    [Fact]
    public async Task ReadAsync_AppliesReadTimeFiltersAndAdvancesPastSkippedLines()
    {
        FakeDebugViewProcess process = new()
        {
            Lines =
            [
                Line("12:00:00.000", 1, "keep.exe", "alpha"),
                Line("12:00:00.001", 2, "skip.exe", "alpha"),
                Line("12:00:00.002", 1, "keep.exe", "beta"),
                Line("12:00:00.003", 1, "keep.exe", "gamma")
            ]
        };
        DebugOutputManager manager = CreateManager(process, out VestigoOptions options, out _);
        options.MaxReadBatch = 2;
        OperationResult<DebugCaptureStatus> started = await manager.StartCaptureAsync(EmptyRequest(), CancellationToken.None);
        await WaitForTerminalAsync(manager, started.Value!.Id);

        DebugCapturePage page = (await manager.ReadAsync(
            started.Value.Id,
            0,
            2,
            TimeSpan.Zero,
            new DebugCaptureReadFilter("alpha", "gamma", [1], ["keep.exe"]),
            CancellationToken.None)).Value!;

        Assert.Single(page.Events);
        Assert.Contains("alpha", page.Events[0], StringComparison.Ordinal);
        Assert.True(page.NextSequence >= 3);
        Assert.True(page.Completed);
    }

    [Fact]
    public void Options_ValidateExecutablePathAndHash()
    {
        Assert.Throws<InvalidOperationException>(() => new DebugOutputOptions { ExecutablePath = "relative.exe" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new DebugOutputOptions { ExpectedSha256 = "zz" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new DebugOutputOptions { MaxDuration = TimeSpan.Zero }.Validate());
        new DebugOutputOptions { ExpectedSha256 = "" }.Validate();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", null);
    }

    private DebugOutputManager CreateManager(FakeDebugViewProcess process, out VestigoOptions options, out string exe)
    {
        exe = Path.Combine(_settings, "DebugViewConsole.exe");
        File.WriteAllText(exe, "placeholder");
        options = new VestigoOptions
        {
            DebugOutput =
            {
                ExecutablePath = exe,
                ExpectedSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant()
            },
            SpoolRetention = TimeSpan.FromHours(1)
        };
        return new DebugOutputManager(options, process, NullLogger<DebugOutputManager>.Instance);
    }

    private static StartDebugCaptureRequest EmptyRequest() => new([], [], [], [], null);

    private static DebugViewLine Line(string time, int pid, string name, string message) =>
        new($"{time}\t{pid}\t{name}\t\t{message}", false);

    private static async Task<DebugCaptureStatus> WaitForTerminalAsync(DebugOutputManager manager, string id, int attempts = 50)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            DebugCaptureStatus status = manager.Get(id).Value!;
            if (status.State is "Stopped" or "Failed")
            {
                return (await manager.StopAsync(id)).Value!;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Debug-output capture did not finish.");
    }

    private static async Task WaitForProcessIdAsync(DebugOutputManager manager, string id, int attempts = 50)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (manager.Get(id).Value!.ProcessId is > 0)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("DebugViewConsole process id was not recorded.");
    }

    private sealed class FakeDebugViewProcess : IDebugViewProcess
    {
        public List<DebugViewLine> Lines { get; set; } = [];

        public Channel<DebugViewLine>? Feed { get; set; }

        public bool WaitUntilCancelled { get; set; }

        public int ProcessId { get; set; } = 4242;

        public List<IReadOnlyList<string>> Started { get; } = [];

        public async IAsyncEnumerable<DebugViewLine> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            Action<int>? onStarted,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.Add(arguments);
            onStarted?.Invoke(ProcessId);
            foreach (DebugViewLine line in Lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return line;
                await Task.Yield();
            }

            if (Feed is not null)
            {
                await foreach (DebugViewLine line in Feed.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return line;
                }
            }

            if (WaitUntilCancelled)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
    }
}

public class DebugOutputElevatedTests
{
    [WindowsElevatedFact]
    [Trait("Category", "WindowsElevated")]
    public async Task RealDebugView_ReceivesOutputDebugString()
    {
        string? exe = FindExecutable();
        if (exe is null)
        {
            Assert.Skip("DebugViewConsole.exe was not found. Run PrepareDebugView or set VESTIGO_DEBUGVIEW_PATH.");
        }

        string settings = Directory.CreateTempSubdirectory("vestigo-debugview-elevated").FullName;
        string? previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", settings);
        try
        {
            Directory.CreateDirectory(VestigoPaths.SpoolDirectory);
            VestigoOptions options = new()
            {
                DebugOutput =
                {
                    ExecutablePath = exe,
                    ExpectedSha256 = DebugOutputOptions.DefaultExpectedSha256,
                    MaxDuration = TimeSpan.FromMinutes(1)
                }
            };
            using DebugOutputManager manager = new(
                options,
                new DebugViewProcess(),
                NullLogger<DebugOutputManager>.Instance);
            await manager.StartAsync(CancellationToken.None);
            OperationResult<DebugCaptureStatus> started = await manager.StartCaptureAsync(
                new StartDebugCaptureRequest([], [], [], [], TimeSpan.FromSeconds(20)),
                CancellationToken.None);
            if (!started.Success)
            {
                Assert.Skip($"Could not start DebugViewConsole: {started.ErrorCode} {started.Error}");
            }

            await Task.Delay(500);
            string marker = "Vestigo-debug-output-test-" + Guid.NewGuid().ToString("N");
            OutputDebugStringW(marker);
            DebugCapturePage page = (await manager.ReadAsync(
                started.Value!.Id,
                0,
                50,
                TimeSpan.FromSeconds(10),
                new DebugCaptureReadFilter(marker, null, null, null),
                CancellationToken.None)).Value!;
            await manager.StopAsync(started.Value.Id);
            await manager.StopAsync(CancellationToken.None);

            Assert.Contains(page.Events, line => line.Contains(marker, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", previous);
        }
    }

    private static string? FindExecutable()
    {
        string? configured = Environment.GetEnvironmentVariable("VESTIGO_DEBUGVIEW_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        string nextToTests = Path.Combine(AppContext.BaseDirectory, "tools", "DebugViewConsole.exe");
        if (File.Exists(nextToTests))
        {
            return nextToTests;
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "publish-x64", "tools", "DebugViewConsole.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern void OutputDebugStringW(string message);

    private sealed class WindowsElevatedFactAttribute : FactAttribute
    {
        public WindowsElevatedFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Requires an elevated Windows session.";
                return;
            }

            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Skip = "Requires an elevated Windows session.";
            }
        }
    }
}
