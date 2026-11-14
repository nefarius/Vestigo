using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
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
    public async Task ConsoleHostCannotRestartTheInstalledService()
    {
        var lifetime = new TestLifetime();
        var restart = new ServiceRestart(lifetime);
        Assert.False(restart.Available);
        await Assert.ThrowsAsync<InvalidOperationException>(restart.RequestAsync);
        Assert.False(lifetime.Stopped);
    }

    [Fact]
    public async Task FailedHelperReadinessLeavesTheHostRunningAndAllowsRetry()
    {
        var lifetime = new TestLifetime();
        var restart = new ServiceRestart(lifetime);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => restart.RequestAsync(true, () =>
            {
                var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
                };
                start.ArgumentList.Add("/d");
                start.ArgumentList.Add("/c");
                start.ArgumentList.Add("exit 1");
                return System.Diagnostics.Process.Start(start)!;
            }));
            Assert.False(lifetime.Stopped);
        }
        // A failed request must not leave the normal delayed shutdown scheduled.
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
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

    [Fact]
    public void DelimitedKeysLoadAndSaveAsSingleNestedPaths()
    {
        File.WriteAllText(VestigoPaths.SettingsFilePath, """
        {
          "Vestigo:ListenPort": 6012,
          "Vestigo": { "Files:MaxUploadBytes": 1234, "ExamplePlugin": { "Limits:Count": 7 } },
          "Other:Nested": { "Enabled": true }
        }
        """);
        ConfigurationSnapshot snapshot = _editor.Load();
        Assert.Equal(6012, snapshot.Values["ListenPort"]!.GetValue<int>());
        Assert.Equal(1234, snapshot.Values["Files"]!["MaxUploadBytes"]!.GetValue<long>());
        Assert.Equal(7, snapshot.Values["ExamplePlugin"]!["Limits"]!["Count"]!.GetValue<int>());
        _editor.Save(snapshot);
        JsonNode saved = JsonNode.Parse(File.ReadAllText(VestigoPaths.SettingsFilePath))!;
        Assert.Null(saved["Vestigo:ListenPort"]);
        Assert.Null(saved["Vestigo"]!["Files:MaxUploadBytes"]);
        Assert.True(saved["Other"]!["Nested"]!["Enabled"]!.GetValue<bool>());
        using var stream = new MemoryStream(File.ReadAllBytes(VestigoPaths.SettingsFilePath));
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddJsonStream(stream).Build();
        Assert.Equal("6012", configuration["Vestigo:ListenPort"]);
    }

    [Fact]
    public void SaveRejectsConflictingUnknownPluginPathsWithoutWriting()
    {
        File.WriteAllText(VestigoPaths.SettingsFilePath, "{}");
        ConfigurationSnapshot snapshot = _editor.Load();
        snapshot.Values["ExamplePlugin"] = new JsonObject
        {
            ["Limits:Count"] = 1, ["Limits"] = new JsonObject { ["Count"] = 2 }
        };
        Assert.Throws<InvalidOperationException>(() => _editor.Save(snapshot));
        Assert.Equal("{}", File.ReadAllText(VestigoPaths.SettingsFilePath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DelimitedArrayElementFieldsPreserveUnknownValuesInEitherOrder(bool delimitedFirst)
    {
        JsonObject nested = new()
        {
            ["Roots"] = new JsonArray(new JsonObject { ["Name"] = "work", ["Path"] = _directory, ["Access"] = "Read" })
        };
        JsonObject document = new();
        if (delimitedFirst) document["Vestigo:Roots:0:PluginHint"] = "preserved";
        document["Vestigo"] = nested;
        if (!delimitedFirst) document["Vestigo:Roots:0:PluginHint"] = "preserved";
        File.WriteAllText(VestigoPaths.SettingsFilePath, document.ToJsonString());
        ConfigurationSnapshot snapshot = _editor.Load();
        Assert.Equal("preserved", snapshot.Values["Roots"]![0]!["PluginHint"]!.GetValue<string>());
        _editor.Save(snapshot);
        Assert.Equal("preserved", _editor.Load().Values["Roots"]![0]!["PluginHint"]!.GetValue<string>());
    }

    [Fact]
    public void SaveNormalizesNewPluginPathsInTheReturnedSnapshotAndFile()
    {
        ConfigurationSnapshot snapshot = _editor.Load();
        snapshot.Values["ExamplePlugin:Limits:Count"] = 7;
        ConfigurationSnapshot saved = _editor.Save(snapshot);
        Assert.False(saved.Values.ContainsKey("ExamplePlugin:Limits:Count"));
        Assert.Equal(7, saved.Values["ExamplePlugin"]!["Limits"]!["Count"]!.GetValue<int>());
        Assert.Equal(7, _editor.Load().Values["ExamplePlugin"]!["Limits"]!["Count"]!.GetValue<int>());
    }

    [Fact]
    public void LoadRejectsConflictingPathsOutsideVestigo()
    {
        File.WriteAllText(VestigoPaths.SettingsFilePath, """
        { "Other:Count": 1, "other": { "Count": 2 } }
        """);
        Assert.Throws<InvalidOperationException>(() => _editor.Load());
    }

    [Fact]
    public void DelimitedKeysCanExtendAnExistingRootsArray()
    {
        JsonObject document = new()
        {
            ["Vestigo"] = new JsonObject { ["Roots"] = new JsonArray(new JsonObject
                { ["Name"] = "first", ["Path"] = _directory, ["Access"] = "Read" }) },
            ["Vestigo:Roots:1"] = new JsonObject { ["Name"] = "second", ["Path"] = _directory, ["Access"] = "Read" }
        };
        File.WriteAllText(VestigoPaths.SettingsFilePath, document.ToJsonString());
        _editor.Save(_editor.Load());
        var roots = _editor.Load().Values["Roots"]!.AsArray();
        Assert.Equal(2, roots.Count);
        Assert.Equal("first", roots[0]!["Name"]!.GetValue<string>());
        Assert.Equal("second", roots[1]!["Name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScalarAndDescendantValuesRoundTripInEitherOrder(bool scalarFirst)
    {
        JsonObject values = new();
        if (scalarFirst) values["ExamplePlugin"] = "scalar";
        values["ExamplePlugin:Limits:Count"] = 7;
        if (!scalarFirst) values["ExamplePlugin"] = "scalar";
        File.WriteAllText(VestigoPaths.SettingsFilePath, new JsonObject { ["Vestigo"] = values }.ToJsonString());
        _editor.Save(_editor.Load());
        using var stream = new MemoryStream(File.ReadAllBytes(VestigoPaths.SettingsFilePath));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        Assert.Equal("scalar", configuration["Vestigo:ExamplePlugin"]);
        Assert.Equal("7", configuration["Vestigo:ExamplePlugin:Limits:Count"]);
    }

    [Fact]
    public void UserinfoCredentialsRoundTripWithNormalizedRunningOptions()
    {
        VestigoOptions running = new() { SymbolServer = "https://user:private-password@example.test/symbols" };
        running.Validate();
        File.WriteAllText(VestigoPaths.SettingsFilePath, """
        { "Vestigo": { "SymbolServer": "https://user:private-password@example.test/symbols" } }
        """);
        var editor = new ConfigurationEditor(running, ModuleRegistry.All);
        ConfigurationSnapshot snapshot = editor.Load();
        Assert.Equal("https://example.test/symbols", snapshot.Values["SymbolServer"]!.GetValue<string>());
        Assert.Equal("Basic", snapshot.Values["SymbolServerAuth"]!["Scheme"]!.GetValue<string>());
        Assert.Equal("user", snapshot.Values["SymbolServerAuth"]!["Username"]!.GetValue<string>());
        Assert.Equal("private-password", snapshot.Values["SymbolServerAuth"]!["Secret"]!.GetValue<string>());
        editor.Save(snapshot);
        Assert.Equal("private-password", editor.Load().Values["SymbolServerAuth"]!["Secret"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("\"InvalidAccess\"")]
    [InlineData("null")]
    [InlineData("{}")]
    public void BadCollectionElementsAreRejectedWithoutDroppingThem(string access)
    {
        File.WriteAllText(VestigoPaths.SettingsFilePath, "{}");
        ConfigurationSnapshot snapshot = _editor.Load();
        snapshot.Values["Roots"] = new JsonArray(new JsonObject
        {
            ["Name"] = "work", ["Path"] = _directory, ["Access"] = JsonNode.Parse(access)
        });
        Assert.Throws<InvalidOperationException>(() => _editor.Save(snapshot));
        Assert.Equal("{}", File.ReadAllText(VestigoPaths.SettingsFilePath));
    }

    [Fact]
    public void StrictCollectionBindingPreservesUnknownFieldsInKnownElements()
    {
        ConfigurationSnapshot snapshot = _editor.Load();
        snapshot.Values["Roots"] = new JsonArray(new JsonObject
        {
            ["Name"] = "work", ["Path"] = _directory, ["Access"] = "Read", ["PluginHint"] = "preserved"
        });
        _editor.Save(snapshot);
        var saved = JsonNode.Parse(File.ReadAllText(VestigoPaths.SettingsFilePath))!;
        Assert.Equal("preserved", saved["Vestigo"]!["Roots"]![0]!["PluginHint"]!.GetValue<string>());
        _editor.Save(_editor.Load());
        saved = JsonNode.Parse(File.ReadAllText(VestigoPaths.SettingsFilePath))!;
        Assert.Equal("preserved", saved["Vestigo"]!["Roots"]![0]!["PluginHint"]!.GetValue<string>());
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
