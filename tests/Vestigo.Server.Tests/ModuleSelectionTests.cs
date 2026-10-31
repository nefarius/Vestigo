using Microsoft.Extensions.Configuration;

using Vestigo.Installer.Actions.Logic;
using Vestigo.Modules;

namespace Vestigo.Server.Tests;

public class ModuleSelectionTests
{
    private static ResolvedSettings Open() =>
        new(ListenSettings.ListenAny, ListenSettings.AuthNone, 5098, string.Empty, null);

    [Fact]
    public void Definitions_MatchServerModulesAndDefaults()
    {
        VestigoModuleNames();

        foreach (ModuleDefinition definition in ModuleSelection.All.Where(m => m.Name != ModuleSelection.SoftwareUninstall))
        {
            VestigoModule module = Assert.Single(ModuleRegistry.All, m => m.Name == definition.Name);
            Assert.Equal(module.EnabledByDefault, definition.DefaultOn);
            foreach (string dependency in module.Dependencies)
            {
                Assert.True(IsAncestor(dependency, definition.Name), $"{definition.Name} must be nested below {dependency}.");
            }
        }

        Assert.False(ModuleSelection.Defaults()[ModuleSelection.SoftwareUninstall]);
    }

    private static void VestigoModuleNames()
    {
        Assert.Equal(
            ModuleRegistry.All.Select(m => m.Name).OrderBy(n => n),
            ModuleSelection.All.Where(m => m.Name != ModuleSelection.SoftwareUninstall).Select(m => m.Name).OrderBy(n => n));
    }

    private static bool IsAncestor(string ancestor, string name)
    {
        for (string? parent = ModuleSelection.Find(name)?.Parent; parent is not null; parent = ModuleSelection.Find(parent)?.Parent)
        {
            if (parent == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void Merge_WritesModulesAndAllowUninstall_KeepingOtherKeys()
    {
        const string existing = """
            { "Vestigo": { "SymbolServer": "https://sym", "Modules": { "Logs": { "Enabled": false, "Extra": 1 } } }, "Serilog": {} }
            """;
        Dictionary<string, bool> states = new()
        {
            ["Processes"] = true,
            ["Logs"] = true,
            ["SoftwareUninstall"] = true
        };

        string merged = ListenSettings.Merge(existing, Open(), states);
        IDictionary<string, bool> read = ModuleSelection.ReadExisting(merged);

        Assert.True(read["Processes"]);
        Assert.True(read["Logs"]);
        Assert.True(read["SoftwareUninstall"]);
        Assert.Contains("\"SymbolServer\"", merged);
        Assert.Contains("\"Extra\": 1", merged);
        Assert.Contains("\"Serilog\"", merged);
        Assert.Contains("\"ApiTokens\": []", merged);
    }

    [Fact]
    public void Merge_WithoutModules_LeavesModulesAlone()
    {
        string merged = ListenSettings.Merge("""{ "Vestigo": { "Modules": { "Devices": { "Enabled": true } } } }""", Open());

        Assert.True(ModuleSelection.ReadExisting(merged)["Devices"]);
    }

    [Fact]
    public void ReadExisting_OnlyReturnsMentionedModules()
    {
        Assert.Empty(ModuleSelection.ReadExisting(null));
        Assert.Empty(ModuleSelection.ReadExisting("not json"));
        Assert.Empty(ModuleSelection.ReadExisting("""{ "Vestigo": { "ListenPort": 1 } }"""));

        IDictionary<string, bool> read = ModuleSelection.ReadExisting(
            """{ "Vestigo": { "Modules": { "devices": { "Enabled": true }, "Bogus": { "Enabled": true } }, "Software": { "AllowUninstall": false } } }""");

        Assert.Equal(2, read.Count);
        Assert.True(read["Devices"]);
        Assert.False(read["SoftwareUninstall"]);
    }

    [Fact]
    public void CustomActionData_RoundTrips()
    {
        IDictionary<string, bool> states = ModuleSelection.Defaults();
        states["Processes"] = true;

        IDictionary<string, bool> parsed = ModuleSelection.Parse(ModuleSelection.Serialize(states));

        Assert.Equal(states.Count, parsed.Count);
        foreach (KeyValuePair<string, bool> pair in states)
        {
            Assert.Equal(pair.Value, parsed[pair.Key]);
        }

        Assert.Empty(ModuleSelection.Parse("Bogus=1;Trace=2;=1;Trace"));
    }

    [Fact]
    public void OpenServerWithPowerfulModules_ProducesValidConfigWithWarningOnly()
    {
        Dictionary<string, bool> states = new() { ["Processes"] = true, ["SoftwareUninstall"] = true };
        string merged = ListenSettings.Merge(null, Open(), states);

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(merged)))
            .Build();
        Vestigo.Configuration.VestigoOptions options = new();
        configuration.GetSection("Vestigo").Bind(options);

        options.Validate();
        ModuleRegistry.ValidateConfiguration(options);
        Assert.True(options.IsBoundBeyondLoopback);
        Assert.NotNull(ModuleRegistry.GetPrivilegedModuleWarning(options));
        Assert.Contains(ModuleRegistry.Enabled(options), m => m.Name == "Processes");
    }
}
