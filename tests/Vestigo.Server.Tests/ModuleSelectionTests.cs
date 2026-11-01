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

        foreach (ModuleDefinition definition in ModuleSelection.All.Where(m => !ModuleSelection.IsOptionToggle(m.Name)))
        {
            VestigoModule module = Assert.Single(ModuleRegistry.All, m => m.Name == definition.Name);
            Assert.Equal(module.EnabledByDefault, definition.DefaultOn);
            foreach (string dependency in module.Dependencies)
            {
                Assert.True(IsAncestor(dependency, definition.Name), $"{definition.Name} must be nested below {dependency}.");
            }
        }

        Assert.False(ModuleSelection.Defaults()[ModuleSelection.SoftwareUninstall]);
        Assert.False(ModuleSelection.Defaults()[ModuleSelection.AudioSetDefault]);
        Assert.Equal("Audio", ModuleSelection.Find(ModuleSelection.AudioSetDefault)?.Parent);
        Assert.False(ModuleSelection.Defaults()[ModuleSelection.BootConfigurationChanges]);
        Assert.Equal("System", ModuleSelection.Find(ModuleSelection.BootConfigurationChanges)?.Parent);
    }

    private static void VestigoModuleNames()
    {
        Assert.Equal(
            ModuleRegistry.All.Select(m => m.Name).OrderBy(n => n),
            ModuleSelection.All.Where(m => !ModuleSelection.IsOptionToggle(m.Name)).Select(m => m.Name).OrderBy(n => n));
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
            ["SoftwareUninstall"] = true,
            ["Audio"] = true,
            ["AudioSetDefault"] = true,
            ["BootConfigurationChanges"] = true
        };

        string merged = ListenSettings.Merge(existing, Open(), states);
        IDictionary<string, bool> read = ModuleSelection.ReadExisting(merged);

        Assert.True(read["Processes"]);
        Assert.True(read["Logs"]);
        Assert.True(read["SoftwareUninstall"]);
        Assert.True(read["Audio"]);
        Assert.True(read["AudioSetDefault"]);
        Assert.True(read["BootConfigurationChanges"]);
        Assert.Contains("\"SymbolServer\"", merged);
        Assert.Contains("\"Extra\": 1", merged);
        Assert.Contains("\"Serilog\"", merged);
        Assert.Contains("\"ApiTokens\": []", merged);
        Assert.Contains("\"AllowSetDefault\": true", merged);
        Assert.Contains("\"AllowBootConfigurationChanges\": true", merged);
    }

    [Fact]
    public void Merge_MatchesKeysCaseInsensitively_WithoutDuplicates()
    {
        const string existing = """
            { "Vestigo": { "modules": { "processes": { "enabled": false } }, "software": { "allowuninstall": false }, "system": { "allowbootconfigurationchanges": false } } }
            """;

        string merged = ListenSettings.Merge(
            existing,
            Open(),
            new Dictionary<string, bool>
            {
                ["Processes"] = true,
                ["SoftwareUninstall"] = true,
                ["BootConfigurationChanges"] = true
            });

        Assert.Contains("\"enabled\": true", merged);
        Assert.Contains("\"allowuninstall\": true", merged);
        Assert.Contains("\"allowbootconfigurationchanges\": true", merged);
        Assert.DoesNotContain("\"Enabled\"", merged);
        Assert.DoesNotContain("\"Modules\"", merged);
        Assert.DoesNotContain("\"AllowUninstall\"", merged);
        Assert.DoesNotContain("\"AllowBootConfigurationChanges\"", merged);
        Assert.True(ModuleSelection.ReadExisting(merged)["Processes"]);
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
            """{ "Vestigo": { "Modules": { "devices": { "Enabled": true }, "Bogus": { "Enabled": true } }, "Software": { "AllowUninstall": false }, "Audio": { "AllowSetDefault": true }, "System": { "AllowBootConfigurationChanges": true } } }""");

        Assert.Equal(4, read.Count);
        Assert.True(read["Devices"]);
        Assert.False(read["SoftwareUninstall"]);
        Assert.True(read["AudioSetDefault"]);
        Assert.True(read["BootConfigurationChanges"]);
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
