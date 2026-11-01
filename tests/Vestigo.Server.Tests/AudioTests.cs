using System.Text.Json;

using Vestigo.Audio;
using Vestigo.Configuration;
using Vestigo.Jobs;
using Vestigo.Modules;
using Vestigo.Processes;

namespace Vestigo.Server.Tests;

public class AudioTests
{
    [Fact]
    public void SessionHelper_RoundTripsListAudioArguments()
    {
        AudioSelector selector = new(AudioSelector.FlowCapture, AudioSelector.StateAll, "ep-1", false, 500);

        string[] args = SessionHelper.BuildListAudioArguments(selector).ToArray();

        Assert.True(SessionHelper.IsInvocation(args));
        Assert.True(SessionHelper.TryParseListAudio(args, out AudioSelector parsed, out string? error));
        Assert.Null(error);
        Assert.Equal(AudioSelector.FlowCapture, parsed.Flow);
        Assert.Equal(AudioSelector.StateAll, parsed.State);
        Assert.Equal("ep-1", parsed.Id);
        Assert.False(parsed.IncludeSessions);
        Assert.Equal(500, parsed.MeterSampleMilliseconds);
    }

    [Fact]
    public void SessionHelper_RejectsBadListAudioArguments()
    {
        Assert.False(SessionHelper.TryParseListAudio([SessionHelper.Flag, "other"], out _, out string? error));
        Assert.NotNull(error);
        Assert.False(SessionHelper.TryParseListAudio([SessionHelper.Flag, SessionHelper.ListAudioCommand, "--flow", "input"], out _, out _));
        Assert.False(SessionHelper.TryParseListAudio([SessionHelper.Flag, SessionHelper.ListAudioCommand, "--state", "enabled"], out _, out _));
        Assert.False(SessionHelper.TryParseListAudio([SessionHelper.Flag, SessionHelper.ListAudioCommand, "--meter-ms", "-1"], out _, out _));
        Assert.False(SessionHelper.TryParseListAudio([SessionHelper.Flag, SessionHelper.ListAudioCommand, "--include-sessions", "maybe"], out _, out _));
        Assert.False(SessionHelper.TryParseListAudio([SessionHelper.Flag, SessionHelper.ListAudioCommand, "--flow"], out _, out _));
    }

    [Fact]
    public void SessionHelper_RoundTripsSetDefaultAudioArguments()
    {
        string[] args = SessionHelper.BuildSetDefaultAudioArguments("{0.0.0.00000000}.{id}", AudioSelector.RoleCommunications).ToArray();

        Assert.True(SessionHelper.TryParseSetDefaultAudio(args, out string id, out string role, out string? error));
        Assert.Null(error);
        Assert.Equal("{0.0.0.00000000}.{id}", id);
        Assert.Equal(AudioSelector.RoleCommunications, role);
        Assert.False(SessionHelper.TryParseSetDefaultAudio([SessionHelper.Flag, SessionHelper.SetDefaultAudioCommand], out _, out _, out _));
        Assert.False(SessionHelper.TryParseSetDefaultAudio([SessionHelper.Flag, SessionHelper.SetDefaultAudioCommand, "--role", "default"], out _, out _, out _));
        Assert.False(SessionHelper.TryParseSetDefaultAudio([SessionHelper.Flag, SessionHelper.SetDefaultAudioCommand, "--id", "x", "--role", "output"], out _, out _, out _));
    }

    [Fact]
    public void SessionHelper_SetDefaultAudio_RejectsWhenAllowSetDefaultIsFalse()
    {
        TextWriter previous = Console.Out;
        StringWriter output = new();
        Console.SetOut(output);
        try
        {
            int code = SessionHelper.Run(
                [SessionHelper.Flag, SessionHelper.SetDefaultAudioCommand, "--id", "x", "--role", "all"],
                allowSetDefault: false);
            Assert.Equal(2, code);
        }
        finally
        {
            Console.SetOut(previous);
        }

        Assert.Contains("Vestigo:Audio:AllowSetDefault", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("not_found", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AudioAllowSetDefault_ReadsJsonSettingsNotArguments()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vestigo-audio-allow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string? previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "appsettings.json"),
                """{ "Vestigo": { "Audio": { "AllowSetDefault": true } } }""");
            Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", directory);
            Assert.True(AudioAllowSetDefault.Load());

            File.WriteAllText(
                Path.Combine(directory, "appsettings.json"),
                """{ "Vestigo": { "Audio": { "AllowSetDefault": false } } }""");
            Assert.False(AudioAllowSetDefault.Load());
        }
        finally
        {
            Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", previous);
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Theory]
    [InlineData(null, AudioSelector.RoleDefault, new[] { "console", "multimedia" })]
    [InlineData("default", AudioSelector.RoleDefault, new[] { "console", "multimedia" })]
    [InlineData("communications", AudioSelector.RoleCommunications, new[] { "communications" })]
    [InlineData("all", AudioSelector.RoleAll, new[] { "console", "multimedia", "communications" })]
    public void AudioSelector_ParsesRoles(string? value, string expectedRole, string[] expectedApplied)
    {
        Assert.True(AudioSelector.TryParseRole(value, out string role, out IReadOnlyList<string> applied, out string? error));
        Assert.Null(error);
        Assert.Equal(expectedRole, role);
        Assert.Equal(expectedApplied, applied);
        Assert.False(AudioSelector.TryParseRole("output", out _, out _, out string? bad));
        Assert.Equal("role must be 'default', 'communications' or 'all'.", bad);
    }

    [Fact]
    public void AudioOptions_RejectsMeterSampleOutOfRange()
    {
        new AudioOptions().Validate();
        new AudioOptions { MeterSampleMilliseconds = 0 }.Validate();
        new AudioOptions { MeterSampleMilliseconds = AudioOptions.MaxMeterSampleMilliseconds }.Validate();
        Assert.Throws<InvalidOperationException>(() => new AudioOptions { MeterSampleMilliseconds = -1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AudioOptions { MeterSampleMilliseconds = 2001 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { Audio = { MeterSampleMilliseconds = -5 } }.Validate());
    }

    [Fact]
    public void Module_IsRegisteredDisabledByDefaultAndWarnsWithoutTokens()
    {
        VestigoModule module = Assert.Single(ModuleRegistry.All, candidate => candidate.Name == "Audio");

        Assert.Contains(typeof(AudioTools), module.ToolTypes);
        Assert.False(module.EnabledByDefault);
        Assert.Empty(module.Dependencies);
        Assert.DoesNotContain(ModuleRegistry.Enabled(new VestigoOptions()), item => item.Name == "Audio");

        VestigoOptions open = new() { Modules = { ["Audio"] = new ModuleSettings { Enabled = true } } };
        ModuleRegistry.ValidateConfiguration(open);
        string? warning = ModuleRegistry.GetPrivilegedModuleWarning(open);
        Assert.NotNull(warning);
        Assert.Contains("Audio", warning);
        Assert.Contains("list audio devices and the processes using them", warning);
        Assert.DoesNotContain("change the default audio devices", warning);
        Assert.DoesNotContain("administrator-equivalent", warning);

        open.Audio.AllowSetDefault = true;
        string? switching = ModuleRegistry.GetPrivilegedModuleWarning(open);
        Assert.NotNull(switching);
        Assert.Contains("change the default audio devices", switching);

        VestigoOptions tokens = new() { ApiTokens = ["t"], Modules = { ["Audio"] = new ModuleSettings { Enabled = true } } };
        tokens.Audio.AllowSetDefault = true;
        Assert.Null(ModuleRegistry.GetPrivilegedModuleWarning(tokens));
    }

    [Fact]
    public async Task Tools_RejectBadContextAndDisabledSwitch()
    {
        AudioTools tools = CreateTools(new UnavailableLauncher(), new VestigoOptions());

        Assert.Equal("invalid_request", (await tools.ListAudioEndpoints(context: "desktop")).ErrorCode);
        Assert.Equal("invalid_request", (await tools.ListAudioEndpoints(flow: "input")).ErrorCode);
        Assert.Equal("invalid_request", (await tools.ListAudioEndpoints(state: "enabled")).ErrorCode);
        Assert.Equal("no_interactive_session", (await tools.ListAudioEndpoints()).ErrorCode);
        Assert.Equal("set_default_disabled", (await tools.SetDefaultAudioEndpoint("id")).ErrorCode);
        Assert.Equal("set_default_disabled", (await tools.SetDefaultAudioEndpoint("id", context: "desktop")).ErrorCode);
    }

    [Fact]
    public async Task SetDefault_RejectsBadRoleWhenEnabled()
    {
        VestigoOptions options = new() { Audio = { AllowSetDefault = true } };
        AudioTools tools = CreateTools(new UnavailableLauncher(), options);

        Assert.Equal("invalid_request", (await tools.SetDefaultAudioEndpoint("id", role: "output")).ErrorCode);
        Assert.Equal("invalid_request", (await tools.SetDefaultAudioEndpoint("id", context: "desktop")).ErrorCode);
        Assert.Equal("no_interactive_session", (await tools.SetDefaultAudioEndpoint("id")).ErrorCode);
    }

    [Fact]
    public void SessionHelper_SerializesEndpointList()
    {
        AudioEndpointListResult result = new(
            [
                new AudioEndpoint(
                    "id-1",
                    "Speakers",
                    "Speakers",
                    "Realtek",
                    "render",
                    "active",
                    "speakers",
                    ["console", "multimedia"],
                    50.5,
                    false,
                    [50.5, 50.5],
                    1.2,
                    true,
                    [new AudioSession(12, "music", "App", "active", false)])
            ],
            1);

        string json = JsonSerializer.Serialize(result, SessionHelper.Json);
        AudioEndpointListResult? parsed = JsonSerializer.Deserialize<AudioEndpointListResult>(json, SessionHelper.Json);
        Assert.NotNull(parsed);
        AudioEndpoint endpoint = Assert.Single(parsed.Endpoints);
        Assert.Equal("id-1", endpoint.Id);
        Assert.Equal(["console", "multimedia"], endpoint.DefaultFor);
        Assert.Equal(50.5, endpoint.VolumePercent);
        Assert.True(endpoint.Active);
        Assert.Equal("music", Assert.Single(endpoint.Sessions).ProcessName);
    }

    [Fact]
    public async Task SessionHelperClient_PassesServiceSettingsDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vestigo-audio-helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string? previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", directory);
            CapturingLauncher launcher = new();
            AudioInventory inventory = new(launcher, new SessionHelperClient(launcher));

            await inventory.ListAsync(AudioSelector.Defaults, LaunchContext.Interactive, CancellationToken.None);

            Assert.NotNull(launcher.Last);
            Assert.Equal(VestigoPaths.SettingsDirectory, launcher.Last.Environment["VESTIGO_SETTINGS_DIR"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", previous);
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void List_ServiceContext_SucceedsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        OperationResult<AudioEndpointListResult> listed = AudioEndpoints.List(
            new AudioSelector(AudioSelector.FlowAll, AudioSelector.StateAll, null, false, 0));

        Assert.True(listed.Success, listed.Error);
        Assert.NotNull(listed.Value);
        Assert.All(listed.Value.Endpoints, endpoint =>
        {
            Assert.False(string.IsNullOrWhiteSpace(endpoint.Id));
            Assert.False(string.IsNullOrWhiteSpace(endpoint.FriendlyName));
            Assert.True(endpoint.Flow is "render" or "capture");
        });
    }

    private static AudioTools CreateTools(IProcessLauncher launcher, VestigoOptions options) =>
        new(new AudioInventory(launcher, new SessionHelperClient(launcher)), options);

    private sealed class CapturingLauncher : IProcessLauncher
    {
        public LaunchRequest? Last { get; private set; }

        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) =>
            OperationResult<bool>.Ok(true);

        public Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken)
        {
            Last = request;
            File.WriteAllText(output.OutputPath, """{"error":"stop"}""");
            return Task.FromResult(2);
        }
    }

    private sealed class UnavailableLauncher : IProcessLauncher
    {
        public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) =>
            context == LaunchContext.Interactive
                ? OperationResult<bool>.Fail("no_interactive_session", "No user is logged on at the console.")
                : OperationResult<bool>.Ok(true);

        public Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken) =>
            throw new ProcessLaunchException("no_interactive_session", "No user is logged on at the console.");
    }
}
