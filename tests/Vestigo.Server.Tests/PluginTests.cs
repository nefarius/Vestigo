using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Loader;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Modules;
using Vestigo.Plugins;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

[Collection("VestigoHost")]
public sealed class PluginTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-plugins-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");
    private readonly IConfiguration _configuration = new ConfigurationBuilder().Build();

    public PluginTests()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);
        StateDirectories.EnsureSecure();
    }

    public void Dispose() => Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _previous);

    private static VestigoOptions Options() => new()
    {
        Plugins = new() { Enabled = true, TrustMode = PluginTrustMode.AdministratorManaged }
    };

    private string Install(string name = "example", string fixture = "example")
    {
        string source = Path.Combine(AppContext.BaseDirectory, "plugin-fixtures", fixture);
        Assert.True(Directory.Exists(source), source);
        string destination = StateDirectories.EnsureSubdirectory(Path.Combine(VestigoPaths.PluginDirectory, name));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        if (fixture != "example")
            File.WriteAllText(Path.Combine(destination, "plugin.json"), JsonSerializer.Serialize(new
            {
                id = "fixture-" + fixture, version = "1.0.0", hostApiVersion = "1.0",
                entryAssembly = fixture == "v1" ? "PluginV1.dll" : "PluginV2.dll",
                entryType = "Vestigo.PluginFixture.Plugin"
            }));
        return destination;
    }

    private static void ChangeManifest(string directory, string field, string? value)
    {
        string path = Path.Combine(directory, "plugin.json");
        JsonObject json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (value is null) json.Remove(field); else json[field] = value;
        File.WriteAllText(path, json.ToJsonString());
    }

    [Fact]
    public void EmptyOrDisabled_DoesNotCreatePluginRoot()
    {
        PluginManager manager = new();
        Assert.Empty(manager.LoadModules(new(), _configuration));
        Assert.Equal("disabled", Assert.Single(manager.Diagnostics).Status);
        Assert.Empty(new PluginManager().LoadModules(Options(), _configuration));
        Assert.False(Directory.Exists(VestigoPaths.PluginDirectory));
    }

    [Fact]
    public void DisabledLoading_DoesNotInspectInstalledCode()
    {
        string directory = Install();
        File.WriteAllText(Path.Combine(directory, "plugin.json"), "invalid");
        PluginManager manager = new();
        Assert.Empty(manager.LoadModules(new(), _configuration));
        Assert.Equal("disabled", Assert.Single(manager.Diagnostics).Status);
    }

    [Fact]
    public void Capabilities_WarnForNewModulesWithoutAuthentication()
    {
        TestModule module = new("CapabilityFixture", [], capabilities: ["Sensitive information"]);
        Assert.Contains("CapabilityFixture", ModuleRegistry.GetPrivilegedModuleWarning(new(), [module]));
        Assert.Null(ModuleRegistry.GetPrivilegedModuleWarning(new() { ApiTokens = ["fixture"] }, [module]));
    }

    [Fact]
    public void CustomRoot_CannotOverlapMutableStateOutsideTheReservedPluginDirectory()
    {
        PluginOptions options = new() { Root = Path.Combine(_settings, "custom-code") };
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.Root = _settings;
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.Root = VestigoPaths.PluginDirectory;
        options.Validate();
    }

    [Theory]
    [InlineData(@"\\?\C:\plugins")]
    [InlineData(@"\\.\C:\plugins")]
    [InlineData(@"\\?\UNC\server\share\plugins")]
    public void CustomRoot_RejectsDevicePaths(string root)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => new PluginOptions { Root = root }.Validate());
        Assert.Contains("device-path prefix", exception.Message);
    }

    [Fact]
    public void PublishedPlugin_SharesContractsAndKeepsDependenciesPrivate()
    {
        Install();
        VestigoOptions options = Options();
        PluginManager manager = new();
        VestigoModule module = Assert.Single(manager.LoadModules(options, _configuration));
        Assert.Equal("loaded", Assert.Single(manager.Diagnostics).Status);
        Assert.Equal("ExampleEcho", module.Name);
        Assert.False(module.IsEnabled(options));
        Type tools = Assert.Single(module.ToolTypes);
        AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(tools.Assembly)!;
        Assert.NotSame(AssemblyLoadContext.Default, context);
        Assert.False(context.IsCollectible);
        Type dependency = tools.GetConstructors().Single().GetParameters().Single().ParameterType;
        Assert.Same(context, AssemblyLoadContext.GetLoadContext(dependency.Assembly));
        Assert.Same(typeof(Extensibility.VestigoModule), tools.Assembly.GetType("Vestigo.ExamplePlugin.EchoModule")!.BaseType);
        options.Modules[module.Name] = new() { Enabled = true };
        ModuleRegistry.ValidateConfiguration(options, [.. ModuleRegistry.All, module]);
        PluginToolValidation.Validate([module]);
    }

    [Theory]
    [InlineData("hostApiVersion", null)]
    [InlineData("hostApiVersion", "0.1")]
    [InlineData("hostApiVersion", "2.0")]
    [InlineData("hostApiVersion", "1.1")]
    [InlineData("entryAssembly", "../escape.dll")]
    [InlineData("entryAssembly", "missing.dll")]
    [InlineData("entryAssembly", "C:\\escape.dll")]
    [InlineData("entryType", "Missing.Type")]
    [InlineData("entryType", "Vestigo.ExamplePlugin.EchoModule")]
    [InlineData("id", "../bad")]
    [InlineData("id", "example-plugin\n")]
    [InlineData("version", "invalid")]
    [InlineData("unknownField", "value")]
    public void InvalidManifestOrEntry_RejectsEntirePlugin(string field, string? value)
    {
        string directory = Install();
        ChangeManifest(directory, field, value);
        PluginManager manager = new();
        Assert.Empty(manager.LoadModules(Options(), _configuration));
        Assert.Equal("rejected", Assert.Single(manager.Diagnostics).Status);
    }

    [Fact]
    public void MalformedJsonAndUnpublishedLayout_AreRejected()
    {
        string directory = Install();
        File.WriteAllText(Path.Combine(directory, "plugin.json"), "{");
        Assert.Empty(new PluginManager().LoadModules(Options(), _configuration));
        directory = Install();
        File.Delete(Path.Combine(directory, "Vestigo.ExamplePlugin.deps.json"));
        Assert.Empty(new PluginManager().LoadModules(Options(), _configuration));
    }

    [Fact]
    public void DuplicateIds_RejectsEveryConflictingDeployment()
    {
        Install("one");
        Install("two");
        PluginManager manager = new();
        Assert.Empty(manager.LoadModules(Options(), _configuration));
        Assert.Equal(2, manager.Diagnostics.Count);
        Assert.All(manager.Diagnostics, diagnostic => Assert.Equal("rejected", diagnostic.Status));
    }

    [Fact]
    public void SeparatePlugins_UseSeparateContextsAndDuplicateModulesFail()
    {
        Install("one");
        ChangeManifest(Install("two"), "id", "second-plugin");
        IReadOnlyList<VestigoModule> modules = new PluginManager().LoadModules(Options(), _configuration);
        Assert.Equal(2, modules.Count);
        Assert.NotSame(AssemblyLoadContext.GetLoadContext(modules[0].ToolTypes[0].Assembly), AssemblyLoadContext.GetLoadContext(modules[1].ToolTypes[0].Assembly));
        Assert.Throws<InvalidOperationException>(() => ModuleRegistry.ValidateConfiguration(Options(), modules));
    }

    [Fact]
    public void PinnedPolicy_RejectsUnpinnedOrModifiedDeployments()
    {
        string directory = Install();
        VestigoOptions options = Options();
        options.Plugins.TrustMode = PluginTrustMode.PinnedSha256;
        Assert.Empty(new PluginManager().LoadModules(options, _configuration));
        options.Plugins.PackageHashes["example-plugin"] = PluginDeployment.ComputeHash(directory, PluginDeployment.ValidateTree(directory));
        Assert.Single(new PluginManager().LoadModules(options, _configuration));
        File.AppendAllText(Path.Combine(directory, "plugin.json"), " ");
        Assert.Empty(new PluginManager().LoadModules(options, _configuration));
    }

    [Fact]
    public void NativeDependency_ResolvesFromPublishedRidAssets()
    {
        string directory = Install("native", "v1");
        VestigoPluginLoadContext context = new(Path.Combine(directory, "PluginV1.dll"));
        var method = typeof(VestigoPluginLoadContext).GetMethod("LoadUnmanagedDll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        IntPtr handle = (IntPtr)method.Invoke(context, ["libsodium"])!;
        try
        {
            Assert.NotEqual(IntPtr.Zero, System.Runtime.InteropServices.NativeLibrary.GetExport(handle, "sodium_version_string"));
        }
        finally { System.Runtime.InteropServices.NativeLibrary.Free(handle); }
    }

    [Fact]
    public void SharedAssembly_IncompatibleIdentityIsRejected()
    {
        string directory = Install();
        VestigoPluginLoadContext context = new(Path.Combine(directory, "Vestigo.ExamplePlugin.dll"));
        Assert.ThrowsAny<Exception>(() => context.LoadFromAssemblyName(new("Vestigo.Abstractions, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null")));
        Assert.ThrowsAny<Exception>(() => context.LoadFromAssemblyName(new("Vestigo.Abstractions, Version=1.0.0.0, Culture=neutral, PublicKeyToken=0123456789abcdef")));
    }

    [Fact]
    public void MissingPrivateDependency_DoesNotFallBackToTheHost()
    {
        string directory = Install();
        File.Delete(Path.Combine(directory, "Vestigo.ExamplePlugin.Dependency.dll"));
        PluginManager manager = new();
        Assert.Empty(manager.LoadModules(Options(), _configuration));
        Assert.Equal("rejected", Assert.Single(manager.Diagnostics).Status);
    }

    [Fact]
    public void InstallerMerge_PreservesPluginTrustAndModuleConfiguration()
    {
        const string json = """{"Vestigo":{"Plugins":{"Enabled":true,"TrustMode":"PinnedSha256","PackageHashes":{"example-plugin":"ABC"}},"Modules":{"ExampleEcho":{"Enabled":true}}}}""";
        string merged = Vestigo.Installer.Actions.Logic.ListenSettings.Merge(json,
            new Vestigo.Installer.Actions.Logic.ResolvedSettings("localhost", "none", 5790, "", "127.0.0.1"));
        JsonNode before = JsonNode.Parse(json)!;
        JsonNode after = JsonNode.Parse(merged)!;
        Assert.True(JsonNode.DeepEquals(before["Vestigo"]!["Plugins"], after["Vestigo"]!["Plugins"]));
        Assert.True(JsonNode.DeepEquals(before["Vestigo"]!["Modules"], after["Vestigo"]!["Modules"]));
    }

    [Fact]
    public void StateRepair_LeavesUntrustedPluginDllAclForLoaderRejection()
    {
        string directory = Install();
        FileInfo file = new(Path.Combine(directory, "Vestigo.ExamplePlugin.dll"));
        FileSecurity security = file.GetAccessControl();
        FileSystemAccessRule rule = new(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Write, AccessControlType.Allow);
        security.AddAccessRule(rule);
        file.SetAccessControl(security);
        // Leave only the plugin subtree during root repair; the managed state directories are empty.
        foreach (string managed in new[] { VestigoPaths.LogDirectory, VestigoPaths.SpoolDirectory, VestigoPaths.DefaultSymbolCache, VestigoPaths.TraceDirectory })
            Directory.Delete(managed);
        DirectoryInfo settings = new(_settings);
        DirectorySecurity settingsSecurity = settings.GetAccessControl();
        settingsSecurity.AddAccessRule(rule);
        settings.SetAccessControl(settingsSecurity);
        Assert.NotEmpty(StateDirectories.EnsureSecure());
        Assert.NotNull(AclTrust.FindUntrustedWriter(file.GetAccessControl(), FileSystemRights.Write, WindowsIdentity.GetCurrent().User!.Value));
        Assert.Empty(new PluginManager().LoadModules(Options(), _configuration));
    }

    [Fact]
    public void PluginAndSettingsWrites_AreDeniedEvenWhenLoadingIsDisabled()
    {
        string directory = Install();
        VestigoOptions options = new()
        {
            AllowedOutputRoots = [_settings],
            Roots = [new() { Name = "state", Path = _settings, Access = RootAccess.Read | RootAccess.Write | RootAccess.Execute }]
        };
        RootPolicy roots = new(options);
        foreach (string path in new[] { directory, Path.Combine(directory, "new.dll"), _settings, VestigoPaths.SettingsFilePath })
        {
            Assert.False(roots.Resolve(path, RootAccess.Write).Success);
            Assert.False(new PathPolicy(options).RequireWritableDirectory(path).Success);
        }
        Assert.True(roots.Resolve(Path.Combine(_settings, "traces", "ok.etl"), RootAccess.Write).Success);
    }

    [Fact]
    public void DependencyGraph_OrdersAcrossModulesAndRejectsMissingDisabledAndCycles()
    {
        VestigoOptions options = new();
        TestModule first = new("First", ["Second"]);
        TestModule second = new("Second", ["Jobs"]);
        IReadOnlyList<VestigoModule> all = [first, second, .. ModuleRegistry.All];
        ModuleRegistry.ValidateConfiguration(options, all);
        string[] ordered = ModuleRegistry.Enabled(options, all).Select(module => module.Name).ToArray();
        Assert.True(Array.IndexOf(ordered, "Jobs") < Array.IndexOf(ordered, "Second"));
        Assert.True(Array.IndexOf(ordered, "Second") < Array.IndexOf(ordered, "First"));
        Assert.Throws<InvalidOperationException>(() => ModuleRegistry.ValidateConfiguration(options, [first]));
        Assert.Throws<InvalidOperationException>(() => ModuleRegistry.ValidateConfiguration(options, [first, new TestModule("Second", ["First"])]));
        options.Modules["Second"] = new() { Enabled = false };
        Assert.Throws<InvalidOperationException>(() => ModuleRegistry.ValidateConfiguration(options, all));
        options.Modules["Unknown"] = new() { Enabled = true };
        Assert.Throws<InvalidOperationException>(() => ModuleRegistry.ValidateConfiguration(options, all));
    }

    [Fact]
    public void ToolCollision_UsesSdkResolvedNames()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => PluginToolValidation.Validate(
            [new TestModule("One", [], [typeof(CollisionTools)]), new TestModule("Two", [], [typeof(OtherCollisionTools)])]));
        string name = McpServerTool.Create(typeof(CollisionTools).GetMethod(nameof(CollisionTools.ResolvedName))!, (RequestContext<CallToolRequestParams> _) => new CollisionTools()).ProtocolTool.Name;
        Assert.Contains($"Duplicate MCP tool name '{name}'", exception.Message);
    }

    [Fact]
    public void NonPublicToolCollision_IsRejectedBeforeRegistration()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => PluginToolValidation.Validate(
            [new TestModule("PrivateTools", [], [typeof(NonPublicCollisionTools)])]));
        Assert.Contains("Duplicate MCP tool name 'nonpublic_collision'", exception.Message);
    }

    [Fact]
    public void NetStandardDependency_WithVersionFourFrameworkReferenceLoads()
    {
        string directory = Install("legacy", "v1");
        ChangeManifest(directory, "entryType", "Vestigo.PluginFixture.LegacyPlugin");
        PluginManager manager = new();
        VestigoModule module = Assert.Single(manager.LoadModules(Options(), _configuration));
        Assert.Equal("Legacy4", module.Name);
        AssemblyLoadContext context = AssemblyLoadContext.All.Single(item => item.Name == $"Vestigo plugin: {Path.Combine(directory, "PluginV1.dll")}");
        var helper = context.Assemblies.Single(assembly => assembly.GetName().Name == "LegacyFramework");
        Assert.Equal(".NETStandard,Version=v2.0", System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>(helper)!.FrameworkName);
        Assert.Contains(context.Assemblies.SelectMany(assembly => assembly.GetReferencedAssemblies()),
            reference => reference.Name == "System.Runtime.CompilerServices.Unsafe" && reference.Version!.Major == 4);
    }

    [Fact]
    public void MultipleModulesAndDifferentPrivateVersions_Coexist()
    {
        Install("first", "v1");
        Install("second", "v2");
        VestigoOptions options = Options();
        IReadOnlyList<VestigoModule> modules = new PluginManager().LoadModules(options, _configuration);
        Assert.Equal(new[] { "Private1", "Secondary1", "Private2", "Secondary2" }, modules.Select(module => module.Name));
        Assert.All(modules, module => Assert.False(module.IsEnabled(options)));
        foreach (VestigoModule module in modules) options.Modules[module.Name] = new() { Enabled = true };
        ModuleRegistry.ValidateConfiguration(options, [.. ModuleRegistry.All, .. modules]);
    }

    [Fact]
    public void FailedEntryPoint_PublishesNoModulesOrSecretMessages()
    {
        string directory = Install("failed", "v1");
        ChangeManifest(directory, "entryType", "Vestigo.PluginFixture.FailedPlugin");
        PluginManager manager = new();
        Assert.Empty(manager.LoadModules(Options(), _configuration));
        PluginDiagnostic diagnostic = Assert.Single(manager.Diagnostics);
        Assert.Equal("failed", diagnostic.Status);
        Assert.DoesNotContain("fixture-secret", diagnostic.Detail);
    }

    [Theory]
    [InlineData("AnonymousPlugin", "AnonymousFixture", "declares an anonymous endpoint")]
    [InlineData("GroupAnonymousPlugin", "GroupAnonymousFixture", "declares an anonymous endpoint")]
    [InlineData("FailedServicesPlugin", "FailedServices", "service registration failed")]
    [InlineData("MissingDiPlugin", "MissingDi", "Unable to resolve service")]
    public void ActivationFailures_AbortBeforeServing(string entry, string module, string expectedDiagnostic)
    {
        string directory = Install("failure", "v1");
        ChangeManifest(directory, "entryType", "Vestigo.PluginFixture." + entry);
        File.WriteAllText(VestigoPaths.SettingsFilePath, JsonSerializer.Serialize(new
        {
            Vestigo = new
            {
                ApiTokens = new[] { "plugin-test-secret" },
                Plugins = new { Enabled = true, TrustMode = "AdministratorManaged" },
                Modules = new Dictionary<string, object> { [module] = new { Enabled = true } }
            }
        }));
        using WebApplicationFactory<Program> factory = new();
        Exception exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(expectedDiagnostic, exception.ToString());
        Assert.DoesNotContain("fixture-secret", exception.ToString());
    }

    [Fact]
    public void ReparseDirectory_IsRejected()
    {
        string root = StateDirectories.EnsureSubdirectory(VestigoPaths.PluginDirectory);
        string outside = StateDirectories.EnsureSubdirectory(Path.Combine(_settings, "outside"));
        string link = Path.Combine(root, "escape");
        // Directory junctions do not require the symlink privilege on Windows.
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe", Arguments = $"/c mklink /J \"{link}\" \"{outside}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        PluginManager manager = new();
        Assert.Empty(manager.LoadModules(Options(), _configuration));
        Assert.Contains("reparse", Assert.Single(manager.Diagnostics).Detail);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealHost_UsesExistingMcpAuthenticationAndPluginEndpoint(bool enabled)
    {
        Install();
        File.WriteAllText(VestigoPaths.SettingsFilePath, JsonSerializer.Serialize(new
        {
            Vestigo = new
            {
                ApiTokens = new[] { "plugin-test-secret" },
                Plugins = new { Enabled = true, TrustMode = "AdministratorManaged" },
                Modules = new Dictionary<string, object> { ["ExampleEcho"] = new { Enabled = enabled } }
            }
        }));
        await using WebApplicationFactory<Program> factory = new();
        using HttpClient http = factory.CreateClient();
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await http.GetAsync("/plugins/example-plugin/status", cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("/mcp", new StringContent("{}"), cancellation)).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "plugin-test-secret");
        HttpResponseMessage status = await http.GetAsync("/plugins/example-plugin/status", cancellation);
        Assert.Equal(enabled ? HttpStatusCode.OK : HttpStatusCode.NotFound, status.StatusCode);
        if (enabled) Assert.Equal("ok", await status.Content.ReadAsStringAsync(cancellation));
        await using HttpClientTransport transport = new(new() { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http);
        await using McpClient client = await McpClient.CreateAsync(transport, cancellationToken: cancellation);
        var tools = await client.ListToolsAsync(cancellationToken: cancellation);
        Assert.Contains(tools, tool => tool.Name == "list_jobs");
        Assert.Equal(enabled, tools.Any(tool => tool.Name == "example_plugin_echo"));
        if (enabled)
        {
            CallToolResult result = await client.CallToolAsync("example_plugin_echo", new Dictionary<string, object?> { ["value"] = "hello plugins" }, cancellationToken: cancellation);
            Assert.False(result.IsError ?? false);
            Assert.Contains(result.Content, content => content is TextContentBlock { Text: "hello plugins" });
        }
    }

    private sealed class TestModule(string name, IReadOnlyList<string> dependencies, IReadOnlyList<Type>? tools = null, IReadOnlyList<string>? capabilities = null) : VestigoModule
    {
        public override string Name => name;
        public override string Description => name;
        public override IReadOnlyList<string> Dependencies => dependencies;
        public override IReadOnlyList<Type> ToolTypes => tools ?? [];
        public override IReadOnlyList<string> Capabilities => capabilities ?? [];
    }
}

[McpServerToolType]
public sealed class CollisionTools
{
    [McpServerTool]
    public string ResolvedName() => "ok";
}

[McpServerToolType]
public sealed class OtherCollisionTools
{
    [McpServerTool]
    public string ResolvedName() => "different class, same SDK tool name";
}

[McpServerToolType]
public sealed class NonPublicCollisionTools
{
    [McpServerTool(Name = "nonpublic_collision")]
    public string Public() => "public";
    [McpServerTool(Name = "nonpublic_collision")]
    private static string Hidden() => "private";
}
