using System.Text.Json;

using ModelContextProtocol.Protocol;

using Vestigo.Configuration;
using Vestigo.Input;
using Vestigo.Jobs;
using Vestigo.Processes;
using Vestigo.Vision;

namespace Vestigo.Server.Tests;

public sealed class InputHelperRoutingTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-input-helper").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");

    public InputHelperRoutingTests()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);
    }

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
    public void InputHelper_SharesTheSessionHelperCommand()
    {
        Assert.Equal(SessionHelper.Flag, InputHelperJson.Flag);
        Assert.Equal(SessionHelper.SendInputCommand, InputHelperJson.Command);
    }

    [Fact]
    public void InputHelper_RoundTripsTheBatchWithSourceGeneratedJson()
    {
        InputBatch batch = new(
            [
                new InputStep("key", null, null, null, null, null, 1, 0, 0, null, [new KeyStroke(0x11, false), new KeyStroke(0x2E, true)], 0, 50, 15),
                new InputStep("type", null, null, null, null, null, 1, 0, 0, "héllo", null, 0, InputMode: "keys")
            ],
            "0xABC",
            true);

        Assert.True(InputHelperJson.TryParse(InputHelperJson.BuildArguments(batch).ToArray(), out InputBatch parsed, out string? error), error);
        Assert.True(parsed.RequireFocus);
        Assert.Equal("0xABC", parsed.FocusWindow);
        Assert.Equal(50, parsed.Steps[0].HoldMilliseconds);
        Assert.Equal(15, parsed.Steps[0].DelayAfterMilliseconds);
        Assert.True(parsed.Steps[0].Keys![1].Extended);
        Assert.Equal("héllo", parsed.Steps[1].Text);
        Assert.Equal("keys", parsed.Steps[1].InputMode);
    }

    [Fact]
    public void InputHelper_ResultKeepsTickStringsAndTheClientParserAcceptsIt()
    {
        InputResult result = new(
            1,
            4,
            null,
            2,
            "0xA1",
            "0xA1",
            true,
            Status: InputInjectionStatus.Submitted,
            Timing: new InputTiming(
                InputTiming.QueryPerformanceCounter,
                "10000000",
                false,
                new InputTimestamp(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), "9007199254740997"),
                new InputTimestamp(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(40), "9007199254740999")));
        string json = JsonSerializer.Serialize(result, InputHelperJsonContext.Default.InputResult);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement ticks = document.RootElement.GetProperty("timing").GetProperty("firstSubmitted").GetProperty("qpcTicks");
        Assert.Equal(JsonValueKind.String, ticks.ValueKind);
        Assert.Equal("9007199254740997", ticks.GetString());

        OperationResult<InputResult> parsed = SessionHelperClient.Parse<InputResult>(json, 0);
        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal("9007199254740999", parsed.Value!.Timing!.LastSubmitted.QpcTicks);
        Assert.Equal(InputInjectionStatus.Submitted, parsed.Value.Status);
    }

    [Fact]
    public void InputHelper_ErrorEnvelopeMatchesTheClientParser()
    {
        string withoutReason = JsonSerializer.Serialize(
            new InputHelperError("The batch is empty.", "invalid_request"),
            InputHelperJsonContext.Default.InputHelperError);
        Assert.DoesNotContain("reason", withoutReason, StringComparison.Ordinal);
        OperationResult<InputResult> missing = SessionHelperClient.Parse<InputResult>(withoutReason, 2);
        Assert.Equal("invalid_request", missing.ErrorCode);
        Assert.Null(missing.Reason);

        string withReason = JsonSerializer.Serialize(
            new InputHelperError("The helper is not running on the input desktop.", "unsupported_on_inactive_desktop", "desktop_inactive"),
            InputHelperJsonContext.Default.InputHelperError);
        OperationResult<InputResult> inactive = SessionHelperClient.Parse<InputResult>(withReason, 2);
        Assert.Equal("unsupported_on_inactive_desktop", inactive.ErrorCode);
        Assert.Equal("desktop_inactive", inactive.Reason);

        int code = Capture(() => InputHelperJson.Run([InputHelperJson.Flag, InputHelperJson.Command, "--batch", "!!!"]));
        Assert.Equal(2, code);
    }

    [Fact]
    public void ResolveInputHelper_SelectsOnlyADispatchableTrustedFile()
    {
        string directory = Directory.CreateTempSubdirectory("vestigo-input-helper-bin").FullName;
        try
        {
            string path = Path.Combine(directory, SessionHelperClient.InputHelperExecutableName);
            Assert.Null(SessionHelperClient.ResolveInputHelper(directory));
            File.WriteAllBytes(path, [0x4D, 0x5A]);
            Assert.Equal(Path.GetFullPath(path), SessionHelperClient.ResolveInputHelper(directory, _ => true, _ => null));
            Assert.Null(SessionHelperClient.ResolveInputHelper(directory, _ => false, _ => null));
            Assert.Null(SessionHelperClient.ResolveInputHelper(directory, _ => true, _ => "the file is writable by Users"));
            if (OperatingSystem.IsWindows())
            {
                Assert.Null(SessionHelperClient.ResolveInputHelper(directory));
            }
            else
            {
                Assert.Equal(Path.GetFullPath(path), SessionHelperClient.ResolveInputHelper(directory));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunInputAsync_UsesTheDedicatedHelperAndDoesNotReplay()
    {
        string dedicated = Path.Combine(_settings, SessionHelperClient.InputHelperExecutableName);
        CountingLauncher launcher = new(SuccessJson());
        SessionHelperClient helper = new(launcher)
        {
            InputExecutableOverride = () => dedicated
        };

        OperationResult<InputResult> sent = await helper.RunInputAsync<InputResult>(
            SessionHelper.BuildSendInputArguments(SampleBatch()),
            elevated: false,
            TimeSpan.FromSeconds(5),
            64 * 1024,
            CancellationToken.None);
        Assert.True(sent.Success, sent.Error);
        Assert.Equal(dedicated, launcher.Executable);
        Assert.Equal(1, launcher.Calls);
        Assert.Contains(SessionHelper.SendInputCommand, launcher.Arguments);

        launcher.Mode = LaunchMode.LaunchFailure;
        OperationResult<InputResult> failed = await helper.RunInputAsync<InputResult>(
            SessionHelper.BuildSendInputArguments(SampleBatch()),
            elevated: false,
            TimeSpan.FromSeconds(5),
            64 * 1024,
            CancellationToken.None);
        Assert.Equal("start_failed", failed.ErrorCode);
        Assert.Equal(2, launcher.Calls);

        launcher.Mode = LaunchMode.Timeout;
        OperationResult<InputResult> timedOut = await helper.RunInputAsync<InputResult>(
            SessionHelper.BuildSendInputArguments(SampleBatch()),
            elevated: false,
            TimeSpan.FromMilliseconds(50),
            64 * 1024,
            CancellationToken.None);
        Assert.Equal("helper_timeout", timedOut.ErrorCode);
        Assert.Equal(3, launcher.Calls);

        launcher.Mode = LaunchMode.Malformed;
        OperationResult<InputResult> malformed = await helper.RunInputAsync<InputResult>(
            SessionHelper.BuildSendInputArguments(SampleBatch()),
            elevated: false,
            TimeSpan.FromSeconds(5),
            64 * 1024,
            CancellationToken.None);
        Assert.Equal("helper_failed", malformed.ErrorCode);
        Assert.Equal(4, launcher.Calls);

        launcher.Mode = LaunchMode.Success;
        OperationResult<InputResult> other = await helper.RunAsync<InputResult>(
            [SessionHelper.Flag, SessionHelper.ListWindowsCommand],
            elevated: false,
            TimeSpan.FromSeconds(5),
            64 * 1024,
            CancellationToken.None);
        Assert.True(other.Success, other.Error);
        Assert.NotEqual(dedicated, launcher.Executable);
        Assert.Equal(5, launcher.Calls);
    }

    [Fact]
    public async Task RunInputAsync_FallsBackBeforeDispatchWhenTheHelperIsAbsent()
    {
        CountingLauncher launcher = new(SuccessJson());
        SessionHelperClient helper = new(launcher)
        {
            InputExecutableOverride = () => null
        };

        OperationResult<InputResult> sent = await helper.RunInputAsync<InputResult>(
            SessionHelper.BuildSendInputArguments(SampleBatch()),
            elevated: false,
            TimeSpan.FromSeconds(5),
            64 * 1024,
            CancellationToken.None);
        Assert.True(sent.Success, sent.Error);
        Assert.Equal(1, launcher.Calls);
        Assert.NotEqual(SessionHelperClient.InputHelperExecutableName, Path.GetFileName(launcher.Executable));
    }

    [Fact]
    public async Task SendInput_RoutesThroughTheDedicatedHelper()
    {
        string dedicated = Path.Combine(_settings, SessionHelperClient.InputHelperExecutableName);
        CountingLauncher launcher = new(SuccessJson());
        SessionHelperClient helper = new(launcher)
        {
            InputExecutableOverride = () => dedicated
        };
        VestigoOptions options = new() { Input = { AllowInput = true } };
        InputTools tools = new(
            helper,
            launcher,
            new PerceptionService(launcher, helper, new CaptureCache(), options),
            options);

        CallToolResult result = await tools.SendInput([new InputActionSpec { Type = "key", Keys = "a" }]);
        Assert.True(result.IsError is not true);
        Assert.Equal(dedicated, launcher.Executable);
        Assert.Equal(1, launcher.Calls);
        Assert.Contains(SessionHelper.SendInputCommand, launcher.Arguments);
        Assert.DoesNotContain(SessionHelper.ListWindowsCommand, launcher.Arguments);
    }

    private static InputBatch SampleBatch() =>
        new([new InputStep("key", null, null, null, null, null, 1, 0, 0, null, [new KeyStroke(0x41, false)], 0)], "0xA1", true);

    private static string SuccessJson()
    {
        InputResult result = new(1, 7, null, 2, "0xA1", "0xA1", true, Timing: new InputTiming(
            InputTiming.QueryPerformanceCounter,
            "10000000",
            false,
            new InputTimestamp(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), "1000"),
            new InputTimestamp(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(50), "1500000")));
        return JsonSerializer.Serialize(result, InputHelperJsonContext.Default.InputResult);
    }

    private static int Capture(Func<int> action)
    {
        TextWriter previous = Console.Out;
        using StringWriter writer = new();
        Console.SetOut(writer);
        try
        {
            return action();
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    private enum LaunchMode
    {
        Success,
        LaunchFailure,
        Timeout,
        Malformed
    }

    private sealed class CountingLauncher(string successJson) : IProcessLauncher
    {
        public int Calls { get; private set; }

        public string? Executable { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public LaunchMode Mode { get; set; } = LaunchMode.Success;

        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) => OperationResult<bool>.Ok(true);

        public async Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken)
        {
            Calls++;
            Executable = request.Executable;
            Arguments = request.Arguments;
            switch (Mode)
            {
                case LaunchMode.LaunchFailure:
                    throw new ProcessLaunchException("start_failed", "The helper could not be started.");
                case LaunchMode.Timeout:
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    return -1;
                case LaunchMode.Malformed:
                    await File.WriteAllTextAsync(output.OutputPath, "not-json", cancellationToken).ConfigureAwait(false);
                    return 2;
                default:
                    await File.WriteAllTextAsync(output.OutputPath, successJson, cancellationToken).ConfigureAwait(false);
                    return 0;
            }
        }
    }
}
