using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Vestigo.Configuration;
using Vestigo.Hosting;
using Vestigo.Installer.Actions.Logic;
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
    public async Task MudBlazorStaticCssIsAllowedOffLoopbackWhileScriptsStayGated()
    {
        DefaultHttpContext css = Context("192.0.2.4");
        css.Request.Method = "GET";
        css.Request.Path = "/_content/MudBlazor/MudBlazor.min.css";
        bool reached = false;
        await new LocalConfigurationAccess(_ => { reached = true; return Task.CompletedTask; }).InvokeAsync(css);
        Assert.True(reached);

        DefaultHttpContext fonts = Context("192.0.2.4");
        fonts.Request.Method = "HEAD";
        fonts.Request.Path = "/_content/MudBlazor/fonts/Roboto-Regular.woff2";
        bool fontReached = false;
        await new LocalConfigurationAccess(_ => { fontReached = true; return Task.CompletedTask; }).InvokeAsync(fonts);
        Assert.True(fontReached);

        DefaultHttpContext script = Context("192.0.2.4");
        script.Request.Method = "GET";
        script.Request.Path = "/_content/MudBlazor/MudBlazor.min.js";
        bool scriptReached = false;
        await new LocalConfigurationAccess(_ => { scriptReached = true; return Task.CompletedTask; }).InvokeAsync(script);
        Assert.False(scriptReached);
        Assert.Equal(403, script.Response.StatusCode);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void KnownContainerScalarAndDescendantBindAndRoundTrip(bool scalarFirst)
    {
        JsonObject values = new();
        if (scalarFirst) values["Files"] = "scalar";
        values["Files:MaxUploadBytes"] = "1234";
        if (!scalarFirst) values["Files"] = "scalar";
        File.WriteAllText(VestigoPaths.SettingsFilePath, new JsonObject { ["Vestigo"] = values }.ToJsonString());
        ConfigurationSnapshot snapshot = _editor.Load();
        Assert.Equal("scalar", snapshot.Values["Files"]!.GetValue<string>());
        Assert.Equal(1234, snapshot.Values["Files:MaxUploadBytes"]!.GetValue<long>());
        _editor.Save(snapshot);
        using var stream = new MemoryStream(File.ReadAllBytes(VestigoPaths.SettingsFilePath));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        Assert.Equal("scalar", configuration["Vestigo:Files"]);
        Assert.Equal("1234", configuration["Vestigo:Files:MaxUploadBytes"]);
        ConfigurationSnapshot reloaded = _editor.Load();
        Assert.Equal("scalar", reloaded.Values["Files"]!.GetValue<string>());
        Assert.Equal(1234, reloaded.Values["Files:MaxUploadBytes"]!.GetValue<long>());
    }

    [Fact]
    public void KnownFlatDescendantConversionFailuresAreNotFilteredOut()
    {
        File.WriteAllText(VestigoPaths.SettingsFilePath, """
        { "Vestigo": { "Files": "scalar", "Files:MaxUploadBytes": "invalid" } }
        """);
        var exception = Assert.Throws<InvalidOperationException>(() => _editor.Load());
        Assert.Contains("Files:MaxUploadBytes", exception.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScalarCollectionKeepsItsSparseSavedIndex(bool scalarFirst)
    {
        JsonObject values = new();
        if (scalarFirst) values["Roots"] = "scalar";
        values["Roots:5"] = new JsonObject
        {
            ["Name"] = "work", ["Path"] = _directory, ["Access"] = "Read", ["PluginHint"] = "preserved"
        };
        if (!scalarFirst) values["Roots"] = "scalar";
        File.WriteAllText(VestigoPaths.SettingsFilePath, new JsonObject { ["Vestigo"] = values }.ToJsonString());
        ConfigurationSnapshot snapshot = _editor.Load();
        Assert.False(snapshot.Values.ContainsKey("Roots:0"));
        Assert.Equal("work", snapshot.Values["Roots:5"]!["Name"]!.GetValue<string>());
        Assert.Equal("preserved", snapshot.Values["Roots:5"]!["PluginHint"]!.GetValue<string>());
        _editor.Save(snapshot);
        using var stream = new MemoryStream(File.ReadAllBytes(VestigoPaths.SettingsFilePath));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        Assert.Equal("scalar", configuration["Vestigo:Roots"]);
        Assert.Equal("5", Assert.Single(configuration.GetSection("Vestigo:Roots").GetChildren()).Key);
        ConfigurationSnapshot reloaded = _editor.Load();
        Assert.False(reloaded.Values.ContainsKey("Roots:0"));
        Assert.Equal("preserved", reloaded.Values["Roots:5"]!["PluginHint"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ModulesScalarAndDescendantKeepDefaultsAndRoundTrip(bool scalarFirst)
    {
        JsonObject values = new();
        if (scalarFirst) values["Modules"] = "scalar";
        values["Modules:Trace:Enabled"] = true;
        if (!scalarFirst) values["Modules"] = "scalar";
        File.WriteAllText(VestigoPaths.SettingsFilePath, new JsonObject { ["Vestigo"] = values }.ToJsonString());
        ConfigurationSnapshot snapshot = _editor.Load();
        Assert.Equal("scalar", snapshot.Values["Modules"]!.GetValue<string>());
        foreach (VestigoModule module in ModuleRegistry.All)
            Assert.NotNull(snapshot.Values["Modules:" + module.Name]!["Enabled"]);
        _editor.Save(snapshot);
        ConfigurationSnapshot reloaded = _editor.Load();
        Assert.Equal("scalar", reloaded.Values["Modules"]!.GetValue<string>());
        Assert.True(reloaded.Values["Modules:Trace"]!["Enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void SystemAllowRebootRoundTripsThroughTheEditor()
    {
        ConfigurationSnapshot snapshot = _editor.Load();
        Assert.False(snapshot.Values["System"]!["AllowReboot"]!.GetValue<bool>());
        snapshot.Values["System"]!["AllowReboot"] = true;
        _editor.Save(snapshot);
        ConfigurationSnapshot loaded = _editor.Load();
        Assert.True(loaded.Values["System"]!["AllowReboot"]!.GetValue<bool>());
        using var stream = new MemoryStream(File.ReadAllBytes(VestigoPaths.SettingsFilePath));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        VestigoOptions options = new();
        configuration.GetSection("Vestigo").Bind(options);
        Assert.True(options.System.AllowReboot);
    }

    [Fact]
    public void SystemAllowRebootLoadsFromMsiWrittenSettings()
    {
        File.WriteAllText(VestigoPaths.SettingsFilePath, """
        { "Vestigo": { "System": { "AllowBootConfigurationChanges": false, "AllowUacChanges": false, "AllowReboot": true } } }
        """);
        ConfigurationSnapshot snapshot = _editor.Load();
        Assert.True(snapshot.Values["System"]!["AllowReboot"]!.GetValue<bool>());
        Assert.False(snapshot.Values["System"]!["AllowUacChanges"]!.GetValue<bool>());
    }

    [Fact]
    public void SystemAutoLogonSettingsRoundTripThroughTheEditor()
    {
        ConfigurationSnapshot snapshot = _editor.Load();
        Assert.False(snapshot.Values["System"]!["AllowAutoLogonChanges"]!.GetValue<bool>());
        Assert.False(snapshot.Values["System"]!["AllowInteractiveLogon"]!.GetValue<bool>());
        Assert.Equal("00:05:00", snapshot.Values["System"]!["LogonConsentLifetime"]!.GetValue<string>());
        snapshot.Values["System"]!["AllowAutoLogonChanges"] = true;
        snapshot.Values["System"]!["AllowInteractiveLogon"] = true;
        snapshot.Values["System"]!["LogonConsentLifetime"] = "00:10:00";
        _editor.Save(snapshot);
        ConfigurationSnapshot loaded = _editor.Load();
        Assert.True(loaded.Values["System"]!["AllowAutoLogonChanges"]!.GetValue<bool>());
        Assert.True(loaded.Values["System"]!["AllowInteractiveLogon"]!.GetValue<bool>());
        Assert.Equal("00:10:00", loaded.Values["System"]!["LogonConsentLifetime"]!.GetValue<string>());
        using var stream = new MemoryStream(File.ReadAllBytes(VestigoPaths.SettingsFilePath));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        VestigoOptions options = new();
        configuration.GetSection("Vestigo").Bind(options);
        Assert.True(options.System.AllowAutoLogonChanges);
        Assert.True(options.System.AllowInteractiveLogon);
        Assert.Equal(TimeSpan.FromMinutes(10), options.System.LogonConsentLifetime);
    }

    [Fact]
    public void SystemAutoLogonUiSaveSurvivesUnselectedUpgradeMerge()
    {
        ConfigurationSnapshot snapshot = _editor.Load();
        snapshot.Values["System"]!["AllowAutoLogonChanges"] = true;
        snapshot.Values["System"]!["AllowInteractiveLogon"] = true;
        _editor.Save(snapshot);
        string existing = File.ReadAllText(VestigoPaths.SettingsFilePath);
        string merged = ListenSettings.Merge(
            existing,
            new ResolvedSettings(ListenSettings.ListenAny, ListenSettings.AuthNone, 5098, string.Empty, null),
            new Dictionary<string, bool> { ["System"] = true });
        Assert.True(ModuleSelection.ReadExisting(merged)["SystemAutoLogon"]);
        Assert.True(ModuleSelection.ReadExisting(merged)["SystemInteractiveLogon"]);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(merged));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        VestigoOptions options = new();
        configuration.GetSection("Vestigo").Bind(options);
        Assert.True(options.System.AllowAutoLogonChanges);
        Assert.True(options.System.AllowInteractiveLogon);
    }

    [Fact]
    public void SystemAllowRebootUiSaveSurvivesUnselectedUpgradeMerge()
    {
        ConfigurationSnapshot snapshot = _editor.Load();
        snapshot.Values["System"]!["AllowReboot"] = true;
        _editor.Save(snapshot);
        string existing = File.ReadAllText(VestigoPaths.SettingsFilePath);
        string merged = ListenSettings.Merge(
            existing,
            new ResolvedSettings(ListenSettings.ListenAny, ListenSettings.AuthNone, 5098, string.Empty, null),
            new Dictionary<string, bool> { ["System"] = true });
        Assert.True(ModuleSelection.ReadExisting(merged)["SystemReboot"]);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(merged));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        VestigoOptions options = new();
        configuration.GetSection("Vestigo").Bind(options);
        Assert.True(options.System.AllowReboot);
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
    public void TypedEditorValuesRoundTripAndPreserveUnknownFields()
    {
        ConfigurationSnapshot snapshot = _editor.Load();
        Assert.True(ConfigurationQuantity.TryConvertByteCount(64, "KiB", typeof(int), out JsonValue? readBytes, out string? error), error);
        Assert.True(ConfigurationQuantity.TryConvertByteCount(1.5m, "MiB", typeof(long), out JsonValue? outputBytes, out error), error);
        Assert.True(ConfigurationQuantity.TryConvertDuration(1.5m, "hours", out string? retention, out error), error);
        Assert.True(ConfigurationQuantity.TryConvertDuration(2.5m, "days", out string? spool, out error), error);
        snapshot.Values["MaxJobReadBytes"] = readBytes;
        snapshot.Values["MaxOutputBytes"] = outputBytes;
        snapshot.Values["JobRetention"] = retention;
        snapshot.Values["SpoolRetention"] = spool;
        snapshot.Values["VolumeRootAccess"] = ConfigurationQuantity.FormatFlags(typeof(RootAccess), ["Execute", "Read"]);
        NamedRootDraft root = new()
        {
            Name = "work",
            Path = _directory,
            Access = ["Execute", "Read"],
            Prototype = new JsonObject { ["PluginHint"] = "kept", ["Name"] = "old", ["Path"] = "old", ["Access"] = "Read" }
        };
        Assert.True(ConfigurationQuantity.TryCreateNamedRoots([root], out JsonArray? roots, out _), "root");
        snapshot.Values["Roots"] = roots;
        string hash = new('a', 64);
        Assert.True(ConfigurationQuantity.TryCreateStringDictionary(
            [new StringEntryDraft { Key = "Playwright", Value = hash }], true, out JsonObject? hashes, out _), "hash");
        snapshot.Values["Plugins"]!["PackageHashes"] = hashes;

        _editor.Save(snapshot);
        ConfigurationSnapshot loaded = _editor.Load();
        Assert.Equal(64 * 1024, loaded.Values["MaxJobReadBytes"]!.GetValue<int>());
        Assert.Equal(1_572_864L, loaded.Values["MaxOutputBytes"]!.GetValue<long>());
        Assert.Equal(54_000_000_000L, TimeSpan.Parse(loaded.Values["JobRetention"]!.GetValue<string>(), CultureInfo.InvariantCulture).Ticks);
        Assert.Equal(ConfigurationQuantity.ToCanonical(TimeSpan.FromTicks((long)(2.5m * TimeSpan.TicksPerDay))), loaded.Values["SpoolRetention"]!.GetValue<string>());
        Assert.Equal(RootAccess.Read | RootAccess.Execute, Enum.Parse<RootAccess>(loaded.Values["VolumeRootAccess"]!.GetValue<string>()));
        Assert.Equal("kept", loaded.Values["Roots"]![0]!["PluginHint"]!.GetValue<string>());
        Assert.Equal("Read, Execute", loaded.Values["Roots"]![0]!["Access"]!.GetValue<string>());
        Assert.Equal(hash, loaded.Values["Plugins"]!["PackageHashes"]!["Playwright"]!.GetValue<string>());
        _editor.Save(loaded);
        Assert.Equal("kept", _editor.Load().Values["Roots"]![0]!["PluginHint"]!.GetValue<string>());
    }

    [Fact]
    public void MalformedQuantitiesRootsAndHashesDoNotOverwriteTheFile()
    {
        File.WriteAllText(VestigoPaths.SettingsFilePath, "{}");
        Reject("JobRetention", JsonValue.Create("forever"));
        Reject("MaxJobReadBytes", JsonValue.Create(-1));
        ConfigurationSnapshot snapshot = _editor.Load();
        snapshot.Values["Plugins"]!["PackageHashes"] = new JsonObject { ["plugin"] = "zz" };
        Assert.Throws<InvalidOperationException>(() => _editor.Save(snapshot));
        Assert.Equal("{}", File.ReadAllText(VestigoPaths.SettingsFilePath));
        snapshot = _editor.Load();
        snapshot.Values["Roots"] = new JsonArray(new JsonObject { ["Name"] = "work", ["Path"] = "relative", ["Access"] = "Read" });
        Assert.Throws<InvalidOperationException>(() => _editor.Save(snapshot));
        Assert.Equal("{}", File.ReadAllText(VestigoPaths.SettingsFilePath));
    }

    private void Reject(string field, JsonNode? value)
    {
        ConfigurationSnapshot snapshot = _editor.Load();
        snapshot.Values[field] = value;
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

    [Fact]
    public void ListenAddressIpv6WildcardRoundTripsAndSurvivesInstallerMerge()
    {
        ConfigurationSnapshot snapshot = _editor.Load();
        snapshot.Values["ListenAddress"] = "::";
        _editor.Save(snapshot);

        ConfigurationSnapshot loaded = _editor.Load();
        Assert.Equal("::", loaded.Values["ListenAddress"]!.GetValue<string>());

        string existing = File.ReadAllText(VestigoPaths.SettingsFilePath);
        ExistingSettings? parsed = ListenSettings.ReadExisting(existing);
        Assert.NotNull(parsed);
        Assert.Equal("::", parsed.Address);

        ResolvedSettings resolved = ListenSettings.Resolve(null, null, null, null, parsed, isUpgrade: true);
        string merged = ListenSettings.Merge(existing, resolved);
        Assert.Equal("::", ListenSettings.ReadExisting(merged)!.Address);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _previous);
        Directory.Delete(_directory, true);
    }
}
