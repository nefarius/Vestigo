using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Vestigo.Configuration;
using Vestigo.Hosting;
using Vestigo.Modules;

namespace Vestigo.Server.Tests;

public class LocalConfigurationAccessTests
{
    private static DefaultHttpContext Context(string remote = "127.0.0.1", string host = "localhost:5098")
    {
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Connection.LocalPort = 5098;
        context.Request.Host = new HostString(host);
        context.Request.Scheme = "http";
        context.Request.Path = "/config";
        return context;
    }

    [Theory]
    [InlineData("127.0.0.1", "localhost:5098")]
    [InlineData("::1", "[::1]:5098")]
    [InlineData("::ffff:127.0.0.1", "127.0.0.1:5098")]
    public void LoopbackPeersAndLocalHostsAreAllowed(string remote, string host) =>
        Assert.True(LocalConfigurationAccess.IsAllowed(Context(remote, host)));

    [Theory]
    [InlineData("192.0.2.4", "localhost:5098")]
    [InlineData("127.0.0.1", "attacker.example:5098")]
    [InlineData("127.0.0.1", "localhost:8098")]
    [InlineData("127.0.0.1", "localhost")]
    public void RemotePeersRebindingAndWrongPortsAreDenied(string remote, string host) =>
        Assert.False(LocalConfigurationAccess.IsAllowed(Context(remote, host)));

    [Theory]
    [InlineData("Origin", "https://attacker.example")]
    [InlineData("Origin", "null")]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("X-Forwarded-For", "127.0.0.1")]
    [InlineData("X-Forwarded-Host", "localhost:5098")]
    [InlineData("Forwarded", "for=127.0.0.1")]
    public void CrossOriginAndProxyRequestsAreDenied(string header, string value)
    {
        DefaultHttpContext context = Context();
        context.Request.Headers[header] = value;
        Assert.False(LocalConfigurationAccess.IsAllowed(context));
    }

    [Fact]
    public void SameOriginIsAllowedButMissingPeerIsDenied()
    {
        DefaultHttpContext context = Context();
        context.Request.Headers.Origin = "http://localhost:5098";
        Assert.True(LocalConfigurationAccess.IsAllowed(context));
        context.Connection.RemoteIpAddress = null;
        Assert.False(LocalConfigurationAccess.IsAllowed(context));
    }

    [Theory]
    [InlineData("/config")]
    [InlineData("/CONFIG/")]
    [InlineData("/_blazor")]
    [InlineData("/_blazor/negotiate")]
    [InlineData("/_content/MudBlazor/MudBlazor.min.js")]
    [InlineData("/_framework/blazor.web.js")]
    public async Task GateProtectsThePageAndAllInteractiveTransports(string path)
    {
        DefaultHttpContext context = Context("192.0.2.4");
        context.Request.Path = path;
        bool reached = false;
        await new LocalConfigurationAccess(_ => { reached = true; return Task.CompletedTask; }).InvokeAsync(context);
        Assert.False(reached);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task OtherRoutesAreUnaffected()
    {
        DefaultHttpContext context = Context("192.0.2.4");
        context.Request.Path = "/mcp";
        bool reached = false;
        await new LocalConfigurationAccess(_ => { reached = true; return Task.CompletedTask; }).InvokeAsync(context);
        Assert.True(reached);
    }
}

public class ServiceRestartTests
{
    [Fact]
    public void ConsoleHostCannotRestartTheInstalledService()
    {
        var lifetime = new TestLifetime();
        var restart = new ServiceRestart(lifetime);
        Assert.False(restart.Available);
        Assert.Throws<InvalidOperationException>(restart.Request);
        Assert.False(lifetime.Stopped);
    }

    [Fact]
    public void MalformedHelperArgumentsFailWithoutStartingTheHost()
    {
        Assert.False(ServiceRestart.IsInvocation([]));
        Assert.True(ServiceRestart.IsInvocation(["--restart-vestigo-service"]));
        Assert.Equal(2, ServiceRestart.Run(["--restart-vestigo-service"]));
        Assert.Equal(2, ServiceRestart.Run(["--restart-vestigo-service", "-1"]));
        Assert.Equal(2, ServiceRestart.Run(["--restart-vestigo-service", "invalid"]));
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public bool Stopped { get; private set; }
        public void StopApplication() => Stopped = true;
    }
}

[Collection("VestigoHost")]
public class ConfigurationEditorTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("vestigo-config-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");
    private readonly ConfigurationEditor _editor;
    public ConfigurationEditorTests()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _directory);
        _editor = new ConfigurationEditor(new VestigoOptions { ListenPort = 6000 }, ModuleRegistry.All);
    }

    [Fact]
    public void DefaultsIncludeEveryWritableOptionAndModule()
    {
        ConfigurationSnapshot snapshot = _editor.Load();
        foreach (var property in typeof(VestigoOptions).GetProperties().Where(property => property.CanWrite))
            Assert.True(snapshot.Values.ContainsKey(property.Name), property.Name);
        Assert.False(snapshot.Values.ContainsKey("AuthenticationEnabled"));
        Assert.False(snapshot.Values.ContainsKey("EffectiveInputRoots"));
        Assert.False(snapshot.Values.ContainsKey("EffectiveOutputRoots"));
        Assert.Equal(6000, snapshot.Values["ListenPort"]!.GetValue<int>());
        foreach (VestigoModule module in ModuleRegistry.All)
            Assert.Equal(module.EnabledByDefault, snapshot.Values["Modules"]![module.Name]!["Enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void SavePreservesOtherSectionsAndPluginKeysAndRoundTripsListsAndFlags()
    {
        File.WriteAllText(VestigoPaths.SettingsFilePath, """
        { // installer-owned machine settings
          "Serilog": { "MinimumLevel": "Debug" },
          "vestigo": { "ListenPort": 5098, "ExamplePlugin": { "Custom": "kept" }, },
        }
        """);
        ConfigurationSnapshot snapshot = _editor.Load();
        snapshot.Values["ListenPort"] = 6001;
        snapshot.Values["ApiTokens"] = new JsonArray("private-token");
        snapshot.Values["VolumeRoots"] = "FixedVolumes";
        snapshot.Values["VolumeRootAccess"] = "Read, Write";
        snapshot.Values["Roots"] = new JsonArray(new JsonObject { ["Name"] = "work", ["Path"] = _directory, ["Access"] = "Read, Write" });
        ConfigurationSnapshot saved = _editor.Save(snapshot);
        Assert.NotEqual(snapshot.Revision, saved.Revision);
        JsonObject document = JsonNode.Parse(File.ReadAllText(VestigoPaths.SettingsFilePath))!.AsObject();
        Assert.Equal("Debug", document["Serilog"]!["MinimumLevel"]!.GetValue<string>());
        Assert.False(document.ContainsKey("Vestigo"));
        Assert.Equal("kept", document["vestigo"]!["ExamplePlugin"]!["Custom"]!.GetValue<string>());
        Assert.Equal(6001, _editor.Load().Values["ListenPort"]!.GetValue<int>());
        Assert.Equal("private-token", _editor.Load().Values["ApiTokens"]![0]!.GetValue<string>());
        Assert.False(File.ReadAllBytes(VestigoPaths.SettingsFilePath).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void SaveRejectsConflictingEdits()
    {
        ConfigurationSnapshot snapshot = _editor.Load();
        File.WriteAllText(VestigoPaths.SettingsFilePath, "{\"Vestigo\":{\"ListenPort\":6002}}");
        Assert.Throws<InvalidOperationException>(() => _editor.Save(snapshot));
        Assert.Equal(6002, _editor.Load().Values["ListenPort"]!.GetValue<int>());
    }

    [Fact]
    public void LoadNormalizesBinderSupportedNumericEnumsAndStringNumbersForTheForm()
    {
        File.WriteAllText(VestigoPaths.SettingsFilePath, """
        { "Vestigo": { "ListenPort": "6010", "VolumeRoots": 0, "Plugins": { "Enabled": "false" } } }
        """);
        ConfigurationSnapshot snapshot = _editor.Load();
        Assert.Equal(6010, snapshot.Values["ListenPort"]!.GetValue<int>());
        Assert.Equal("None", snapshot.Values["VolumeRoots"]!.GetValue<string>());
        Assert.False(snapshot.Values["Plugins"]!["Enabled"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("ListenPort", "0")]
    [InlineData("ApiTokens", "[\"\"]")]
    [InlineData("Files", "null")]
    [InlineData("MaxConcurrentJobs", "-1")]
    [InlineData("Modules", "{\"UnknownModule\":{\"Enabled\":true}}")]
    [InlineData("Modules", "{\"RemoteDebugger\":{\"Enabled\":true},\"Processes\":{\"Enabled\":false}}")]
    public void InvalidSettingsDoNotOverwriteTheFile(string field, string json)
    {
        File.WriteAllText(VestigoPaths.SettingsFilePath, "{}");
        ConfigurationSnapshot snapshot = _editor.Load();
        snapshot.Values[field] = JsonNode.Parse(json);
        Assert.Throws<InvalidOperationException>(() => _editor.Save(snapshot));
        Assert.Equal("{}", File.ReadAllText(VestigoPaths.SettingsFilePath));
    }

    [Fact]
    public void ValidationErrorsNeverEchoSuppliedSecrets()
    {
        ConfigurationSnapshot snapshot = _editor.Load();
        snapshot.Values["ListenPort"] = "private-token";
        var exception = Assert.Throws<InvalidOperationException>(() => _editor.Save(snapshot));
        Assert.DoesNotContain("private-token", exception.ToString());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _previous);
        Directory.Delete(_directory, true);
    }
}
