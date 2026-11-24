using Vestigo.Installer.Actions.Logic;

namespace Vestigo.Server.Tests;

public class InstallerHandoffTests
{
    private const string Token = "existing-token-value-xyz";
    private const string Replacement = "replacement-token-xyz";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProtectedUpgrade_KeepsLanAddressPortAndFirewall(bool interactive)
    {
        string json = Settings("0.0.0.0", 6000);
        Handoff run = Execute(json, isUpgrade: true, isInstalled: false, interactive, settingsProtected: true);

        AssertKept(run, "0.0.0.0", 6000, Token);
        Assert.Equal("0", run.Data[SettingsHandoff.DataListenModeExplicit]);
        Assert.Equal("0", run.Data[SettingsHandoff.DataPortExplicit]);
        Assert.Equal(string.Empty, run.Data[SettingsHandoff.DataListenMode]);
        Assert.Equal(string.Empty, run.Data[SettingsHandoff.DataPort]);
        AssertRule(run.Firewall, 6000);
        Assert.Contains("keep-me", run.Merged, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedUpgrade_KeepsSpecificLanAddress()
    {
        Handoff run = Execute(Settings("192.168.1.5", 6000), isUpgrade: true, isInstalled: false, interactive: true, settingsProtected: true);

        AssertKept(run, "192.168.1.5", 6000, Token);
        AssertRule(run.Firewall, 6000);
    }

    [Fact]
    public void ProtectedUpgrade_KeepsIpv6AnyAndLoopbackBytes()
    {
        Handoff any = Execute(Settings("::", 6000), isUpgrade: true, isInstalled: false, interactive: false, settingsProtected: true);
        Handoff loopback = Execute(Settings("::1", 6000), isUpgrade: true, isInstalled: false, interactive: false, settingsProtected: true);

        AssertKept(any, "::", 6000, Token);
        AssertRule(any.Firewall, 6000);
        AssertKept(loopback, "::1", 6000, Token);
        AssertNoRule(loopback.Firewall);
    }

    [Fact]
    public void ProtectedRepair_RestoresMissingOrDriftedFirewallAndKeepsLoopbackQuiet()
    {
        Handoff lan = Execute(
            Settings("0.0.0.0", 6000),
            isUpgrade: false,
            isInstalled: true,
            interactive: false,
            settingsProtected: true,
            seedFirewall: store =>
            {
                store.Add(FirewallRuleSync.CreateRule(1));
                store.Add(FirewallRuleSync.CreateRule(2));
            });
        Handoff loopback = Execute(
            Settings("127.0.0.1", 6000),
            isUpgrade: false,
            isInstalled: true,
            interactive: true,
            settingsProtected: true,
            seedFirewall: store => store.Add(FirewallRuleSync.CreateRule(6000)));

        AssertKept(lan, "0.0.0.0", 6000, Token);
        AssertRule(lan.Firewall, 6000);
        AssertKept(loopback, "127.0.0.1", 6000, Token);
        AssertNoRule(loopback.Firewall);
    }

    [Fact]
    public void ProtectedInteractive_AddressOnlyAndPortOnlyOverridesAreIndependent()
    {
        Handoff localhost = Execute(
            Settings("192.168.1.5", 6000),
            isUpgrade: true,
            isInstalled: false,
            interactive: true,
            settingsProtected: true,
            edit: properties => properties.Set(SettingsHandoff.ListenUiModeProperty, ListenSettings.UiListenLocalhost));
        Handoff port = Execute(
            Settings("192.168.1.5", 6000),
            isUpgrade: true,
            isInstalled: false,
            interactive: true,
            settingsProtected: true,
            edit: properties => properties.Set(SettingsHandoff.PortProperty, "7000"));
        Handoff explicitDefaultPort = Execute(
            Settings("0.0.0.0", 6000),
            isUpgrade: true,
            isInstalled: false,
            interactive: true,
            settingsProtected: true,
            edit: properties => properties.Set(SettingsHandoff.PortProperty, "5098"));
        Handoff widen = Execute(
            Settings("127.0.0.1", 6000),
            isUpgrade: true,
            isInstalled: false,
            interactive: true,
            settingsProtected: true,
            edit: properties => properties.Set(SettingsHandoff.ListenUiModeProperty, ListenSettings.UiListenAny));
        Handoff keepLan = Execute(
            Settings("192.168.1.5", 6000),
            isUpgrade: true,
            isInstalled: false,
            interactive: true,
            settingsProtected: true,
            edit: properties => properties.Set(SettingsHandoff.ListenUiModeProperty, ListenSettings.UiListenAny));

        AssertKept(localhost, "127.0.0.1", 6000, Token);
        AssertNoRule(localhost.Firewall);
        AssertKept(port, "192.168.1.5", 7000, Token);
        AssertRule(port.Firewall, 7000);
        AssertKept(explicitDefaultPort, "0.0.0.0", 5098, Token);
        AssertRule(explicitDefaultPort.Firewall, 5098);
        AssertKept(widen, "0.0.0.0", 6000, Token);
        AssertRule(widen.Firewall, 6000);
        AssertKept(keepLan, "192.168.1.5", 6000, Token);
        AssertRule(keepLan.Firewall, 6000);
    }

    [Fact]
    public void ProtectedUnattended_AddressOnlyAndPortOnlyOverridesAreIndependent()
    {
        Handoff localhost = Execute(
            Settings("0.0.0.0", 6000),
            isUpgrade: true,
            isInstalled: false,
            interactive: false,
            settingsProtected: true,
            edit: properties => properties.Set(SettingsHandoff.ListenModeProperty, "localhost"));
        Handoff port = Execute(
            Settings("192.168.1.5", 6000),
            isUpgrade: true,
            isInstalled: false,
            interactive: false,
            settingsProtected: true,
            edit: properties => properties.Set(SettingsHandoff.PortProperty, "7000"));
        Handoff both = Execute(
            Settings("192.168.1.5", 6000),
            isUpgrade: true,
            isInstalled: false,
            interactive: false,
            settingsProtected: true,
            edit: properties =>
            {
                properties.Set(SettingsHandoff.ListenModeProperty, "LOCALHOST");
                properties.Set(SettingsHandoff.PortProperty, "5098");
            });

        AssertKept(localhost, "127.0.0.1", 6000, Token);
        AssertNoRule(localhost.Firewall);
        AssertKept(port, "192.168.1.5", 7000, Token);
        AssertRule(port.Firewall, 7000);
        AssertKept(both, "127.0.0.1", 5098, Token);
        AssertNoRule(both.Firewall);
    }

    [Fact]
    public void ProtectedWizard_ReturningToKeepDiscardsAddressAndPortEdits()
    {
        DictionaryInstallerProperties properties = new();
        List<string> log = [];
        string json = Settings("192.168.1.5", 6000);
        SettingsHandoff.Load(properties, null, true, true, false, log.Add);
        properties.Set(SettingsHandoff.ListenUiModeProperty, ListenSettings.UiListenAny);
        properties.Set(SettingsHandoff.PortProperty, "7000");
        SettingsHandoff.Apply(properties, null, true, true, false);
        properties.Set(SettingsHandoff.ListenUiModeProperty, ListenSettings.UiListenKeep);
        properties.Set(SettingsHandoff.PortProperty, string.Empty);
        SettingsHandoff.Apply(properties, null, true, true, false);
        SettingsHandoff.Load(properties, null, true, true, false, log.Add);

        Dictionary<string, string> data = SettingsHandoff.Prepare(properties, true, false, string.Empty);
        ResolvedSettings resolved = SettingsHandoff.ResolveWrite(data, ListenSettings.ReadExisting(json));
        string merged = ListenSettings.Merge(json, resolved);
        MemoryFirewallRuleStore firewall = new();
        FirewallRuleSync.Sync(resolved, firewall, log.Add);

        Assert.Equal("192.168.1.5", ListenSettings.ReadExisting(merged)!.Address);
        Assert.Equal(6000, ListenSettings.ReadExisting(merged)!.Port);
        AssertRule(firewall, 6000);
        AssertNoSecrets(log, resolved.Token);
    }

    [Fact]
    public void ProtectedUpgrade_ReadableExecuteLoadHonorsExplicitLocalhost()
    {
        DictionaryInstallerProperties properties = new();
        string json = Settings("0.0.0.0", 6000);
        SettingsHandoff.Load(properties, null, true, true, false, null);
        properties.Set(SettingsHandoff.ListenUiModeProperty, ListenSettings.UiListenLocalhost);
        properties.Set(SettingsHandoff.ValidateTokenProperty, "0");
        SettingsHandoff.Apply(properties, null, true, true, false);
        SettingsHandoff.Load(properties, json, false, true, false, null);

        Assert.Equal("1", properties.Get(SettingsHandoff.ListenModeExplicitProperty));
        Assert.Equal(ListenSettings.ListenLocalhost, properties.Get(SettingsHandoff.ListenModeProperty));
        Assert.Equal("0", properties.Get(SettingsHandoff.PortExplicitProperty));
        Dictionary<string, string> data = SettingsHandoff.Prepare(properties, true, false, string.Empty);
        ResolvedSettings resolved = SettingsHandoff.ResolveWrite(data, ListenSettings.ReadExisting(json));
        ExistingSettings saved = ListenSettings.ReadExisting(ListenSettings.Merge(json, resolved))!;

        Assert.Equal("127.0.0.1", saved.Address);
        Assert.Equal(6000, saved.Port);
        Assert.Equal([Token], saved.Tokens);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void MissingOrBrokenSettings_UseLocalhostFallbackOnUpgradeAndRepair(bool isUpgrade, bool isInstalled)
    {
        Handoff missing = Execute(null, isUpgrade, isInstalled, interactive: false, settingsProtected: false);
        Handoff broken = Execute("{", isUpgrade, isInstalled, interactive: true, settingsProtected: false);

        AssertKept(missing, "127.0.0.1", 5098, string.Empty);
        Assert.Equal(ListenSettings.AuthNone, missing.Resolved.AuthMode);
        AssertNoRule(missing.Firewall);
        AssertKept(broken, "127.0.0.1", 5098, string.Empty);
        AssertNoRule(broken.Firewall);
    }

    [Fact]
    public void FreshInstall_KeepsAnyAddressGeneratedTokenAndFirewall()
    {
        Handoff run = Execute(null, isUpgrade: false, isInstalled: false, interactive: true, settingsProtected: false);

        Assert.Equal("0.0.0.0", ListenSettings.ReadExisting(run.Merged)!.Address);
        Assert.Equal(5098, run.Resolved.Port);
        Assert.Equal(ListenSettings.AuthToken, run.Resolved.AuthMode);
        Assert.Equal(43, run.Resolved.Token.Length);
        AssertRule(run.Firewall, 5098);
        AssertNoSecrets(run.Log, run.Resolved.Token);
    }

    [Fact]
    public void ReadableUpgrade_PortEditKeepsSpecificAddress()
    {
        Handoff run = Execute(
            Settings("192.168.1.5", 6000),
            isUpgrade: true,
            isInstalled: false,
            interactive: true,
            settingsProtected: false,
            edit: properties => properties.Set(SettingsHandoff.PortProperty, "7000"));

        AssertKept(run, "192.168.1.5", 7000, Token);
        Assert.Equal("0", run.Data[SettingsHandoff.DataListenModeExplicit]);
        Assert.Equal("1", run.Data[SettingsHandoff.DataPortExplicit]);
        AssertRule(run.Firewall, 7000);
    }

    [Fact]
    public void ProtectedUpgrade_KeepReplaceAndNoneDoNotDisturbTheListenAddress()
    {
        Handoff keep = Execute(Settings("0.0.0.0", 6000), true, false, false, true);
        Handoff replace = Execute(
            Settings("0.0.0.0", 6000),
            true,
            false,
            false,
            true,
            properties => properties.Set(SettingsHandoff.TokenProperty, Replacement));
        Handoff none = Execute(
            Settings("0.0.0.0", 6000),
            true,
            false,
            false,
            true,
            properties => properties.Set(SettingsHandoff.AuthModeProperty, ListenSettings.AuthNone));

        AssertKept(keep, "0.0.0.0", 6000, Token);
        AssertKept(replace, "0.0.0.0", 6000, Replacement);
        Assert.Equal([Replacement], ListenSettings.ReadExisting(replace.Merged)!.Tokens);
        AssertKept(none, "0.0.0.0", 6000, string.Empty);
        Assert.Empty(ListenSettings.ReadExisting(none.Merged)!.Tokens);
        AssertNoSecrets(keep.Log, Token);
        AssertNoSecrets(replace.Log, Replacement);
        AssertNoSecrets(none.Log, Token);
    }

    [Fact]
    public void InvalidListenOrPort_FailsSilentInstallAndBlocksNext()
    {
        DictionaryInstallerProperties silent = new();
        silent.Set(SettingsHandoff.PortProperty, "0");
        Assert.Throws<ArgumentException>(() => SettingsHandoff.Load(silent, null, true, true, false, null));

        DictionaryInstallerProperties wizard = new();
        SettingsHandoff.Load(wizard, null, true, true, false, null);
        wizard.Set(SettingsHandoff.PortProperty, "70000");
        SettingsHandoff.Apply(wizard, null, true, true, false);
        Assert.Contains("port", wizard.Get(SettingsHandoff.ErrorProperty), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirewallSync_ReplacesDuplicatesAndDoesNotThrowWhenTheStoreFails()
    {
        ResolvedSettings lan = ListenSettings.Resolve("any", "none", "6000", null, null);
        MemoryFirewallRuleStore duplicates = new();
        duplicates.Add(FirewallRuleSync.CreateRule(1));
        duplicates.Add(FirewallRuleSync.CreateRule(2));
        duplicates.Add(FirewallRuleSync.CreateRule(3));
        List<string> log = [];
        FirewallRuleSync.Sync(lan, duplicates, log.Add);
        AssertRule(duplicates, 6000);

        MemoryFirewallRuleStore down = new() { Fail = true };
        FirewallRuleSync.Sync(lan, down, log.Add);
        FirewallRuleSync.Remove(down, log.Add);
        Assert.Contains(log, line => line.Contains("Could not update", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("Could not remove", StringComparison.Ordinal));

        MemoryFirewallRuleStore present = new();
        present.Add(FirewallRuleSync.CreateRule(5098));
        present.Add(FirewallRuleSync.CreateRule(5098));
        FirewallRuleSync.Remove(present, log.Add);
        AssertNoRule(present);
        Assert.Contains(log, line => line.Contains("Removed 2", StringComparison.Ordinal));
    }

    [Fact]
    public void FirewallSync_KeepsThePreviousRuleWhenReplacementFails()
    {
        ResolvedSettings lan = ListenSettings.Resolve("any", "none", "6000", null, null);
        MemoryFirewallRuleStore store = new();
        store.Add(FirewallRuleSync.CreateRule(5098));
        store.FailAdds = 1;
        List<string> log = [];

        FirewallRuleSync.Sync(lan, store, log.Add);

        AssertRule(store, 5098);
        Assert.Contains(log, line => line.Contains("Restoring the previous rule", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("Restored 1", StringComparison.Ordinal));
    }

    [Fact]
    public void FirewallSync_LeavesAnExactRuleInPlace()
    {
        ResolvedSettings lan = ListenSettings.Resolve("any", "none", "6000", null, null);
        MemoryFirewallRuleStore store = new();
        store.Add(FirewallRuleSync.CreateRule(6000));
        store.FailAdds = 1;
        List<string> log = [];

        FirewallRuleSync.Sync(lan, store, log.Add);

        AssertRule(store, 6000);
        Assert.Contains(log, line => line.Contains("Kept inbound firewall rule", StringComparison.Ordinal));
    }

    [Fact]
    public void FirewallSync_ReplacesARuleLimitedToWireless()
    {
        ResolvedSettings lan = ListenSettings.Resolve("any", "none", "6000", null, null);
        FirewallRuleRecord kept = FirewallRuleSync.CreateRule(6000);
        FirewallRuleRecord wireless = new(
            kept.Name,
            kept.Description,
            kept.Protocol,
            kept.LocalPorts,
            kept.RemoteAddresses,
            kept.Direction,
            kept.Action,
            kept.Profiles,
            kept.Enabled,
            "Wireless",
            kept.LocalAddresses,
            kept.ApplicationName,
            kept.ServiceName,
            kept.RemotePorts,
            kept.EdgeTraversal);
        MemoryFirewallRuleStore store = new();
        store.Add(wireless);
        List<string> log = [];

        FirewallRuleSync.Sync(lan, store, log.Add);

        AssertRule(store, 6000);
        Assert.DoesNotContain(log, line => line.Contains("Kept inbound firewall rule", StringComparison.Ordinal));
    }

    [Fact]
    public void FirewallRollback_RestoresInterfaceAndProgramRestrictions()
    {
        FirewallRuleRecord restricted = new(
            FirewallRuleSync.RuleName,
            FirewallRuleSync.RuleDescription,
            FirewallRuleSync.ProtocolTcp,
            "6000",
            "10.0.0.0/8",
            FirewallRuleSync.DirectionInbound,
            FirewallRuleSync.ActionAllow,
            FirewallRuleSync.ProfilesAll,
            true,
            "Wireless",
            "192.168.1.5",
            "C:\\Program Files\\Vestigo\\Vestigo.Server.exe",
            "Vestigo",
            "1024-65535",
            true);
        IReadOnlyList<FirewallRuleRecord> restored = FirewallRuleSync.Deserialize(FirewallRuleSync.Serialize(new[] { restricted }));
        FirewallRuleRecord rule = Assert.Single(restored);
        Assert.Equal("Wireless", rule.InterfaceTypes);
        Assert.Equal("192.168.1.5", rule.LocalAddresses);
        Assert.Equal("C:\\Program Files\\Vestigo\\Vestigo.Server.exe", rule.ApplicationName);
        Assert.Equal("Vestigo", rule.ServiceName);
        Assert.Equal("1024-65535", rule.RemotePorts);
        Assert.True(rule.EdgeTraversal);

        MemoryFirewallRuleStore store = new();
        store.Add(FirewallRuleSync.CreateRule(1));
        FirewallRuleSync.Restore(restored, store, _ => { });
        FirewallRuleRecord saved = Assert.Single(store.Rules);
        Assert.Equal("Wireless", saved.InterfaceTypes);
        Assert.Equal("192.168.1.5", saved.LocalAddresses);
        Assert.Equal(restricted.ApplicationName, saved.ApplicationName);
    }

    [Fact]
    public void FirewallRollback_RoundTripsThePreviousRules()
    {
        FirewallRuleRecord first = FirewallRuleSync.CreateRule(6000);
        FirewallRuleRecord second = FirewallRuleSync.CreateRule(5098);
        string snapshot = FirewallRuleSync.Serialize(new[] { first, second });
        IReadOnlyList<FirewallRuleRecord> restored = FirewallRuleSync.Deserialize(snapshot);
        Assert.Equal(2, restored.Count);
        Assert.Equal("6000", restored[0].LocalPorts);
        Assert.Equal("5098", restored[1].LocalPorts);

        MemoryFirewallRuleStore store = new();
        store.Add(FirewallRuleSync.CreateRule(1));
        FirewallRuleSync.Restore(restored, store, _ => { });
        Assert.Equal(2, store.Rules.Count);
        Assert.Equal("6000", store.Rules[0].LocalPorts);
        Assert.Equal("5098", store.Rules[1].LocalPorts);
        Assert.True(FirewallRuleSync.IsRuleMissing(unchecked((int)0x80070002)));
        Assert.True(FirewallRuleSync.IsRuleMissing(unchecked((int)0x80070490)));
        Assert.False(FirewallRuleSync.IsRuleMissing(unchecked((int)0x800706D9)));
    }

    [Fact]
    public void Apply_KeepsEditableLocalhostWhenTheNextReadIsProtected()
    {
        DictionaryInstallerProperties properties = new();
        string json = Settings("192.168.1.5", 6000);
        SettingsHandoff.Load(properties, json, false, true, false, null);
        properties.Set(SettingsHandoff.ListenModeProperty, ListenSettings.ListenLocalhost);
        SettingsHandoff.Apply(properties, null, true, true, false);

        Assert.True(string.IsNullOrEmpty(properties.Get(SettingsHandoff.ErrorProperty)));
        Assert.Equal("1", properties.Get(SettingsHandoff.ListenModeExplicitProperty));
        Assert.Equal(ListenSettings.ListenLocalhost, properties.Get(SettingsHandoff.ListenModeProperty));
        Assert.Equal("0", properties.Get(SettingsHandoff.PortExplicitProperty));
        Dictionary<string, string> data = SettingsHandoff.Prepare(properties, true, false, string.Empty);
        ResolvedSettings resolved = SettingsHandoff.ResolveWrite(data, ListenSettings.ReadExisting(json));
        ExistingSettings saved = ListenSettings.ReadExisting(ListenSettings.Merge(json, resolved))!;
        Assert.Equal("127.0.0.1", saved.Address);
        Assert.Equal(6000, saved.Port);
    }

    [Fact]
    public void Apply_RefreshesPrefillWhenAnUntouchedPortChangesOnDisk()
    {
        DictionaryInstallerProperties properties = new();
        SettingsHandoff.Load(properties, Settings("192.168.1.5", 6000), false, true, false, null);
        SettingsHandoff.Apply(properties, Settings("192.168.1.5", 7000), false, true, false);
        Assert.Equal("0", properties.Get(SettingsHandoff.PortExplicitProperty));
        Assert.Equal("7000", properties.Get(SettingsHandoff.PortProperty));
        Assert.Equal("7000", properties.Get(SettingsHandoff.PortPrefillProperty));

        SettingsHandoff.Apply(properties, Settings("192.168.1.5", 8000), false, true, false);
        Assert.Equal("0", properties.Get(SettingsHandoff.PortExplicitProperty));
        Assert.Equal("8000", properties.Get(SettingsHandoff.PortProperty));
        Dictionary<string, string> data = SettingsHandoff.Prepare(properties, true, false, string.Empty);
        Assert.Equal(string.Empty, data[SettingsHandoff.DataPort]);
        string json = Settings("192.168.1.5", 8000);
        ResolvedSettings resolved = SettingsHandoff.ResolveWrite(data, ListenSettings.ReadExisting(json));
        ExistingSettings saved = ListenSettings.ReadExisting(ListenSettings.Merge(json, resolved))!;
        Assert.Equal("192.168.1.5", saved.Address);
        Assert.Equal(8000, saved.Port);
    }

    [Fact]
    public void Describe_OmitsTheApiToken()
    {
        DictionaryInstallerProperties properties = new();
        properties.Set(SettingsHandoff.TokenProperty, Token);
        properties.Set(SettingsHandoff.ListenModeExplicitProperty, "0");
        properties.Set(SettingsHandoff.PortExplicitProperty, "1");
        properties.Set(SettingsHandoff.PortProperty, "6000");

        string described = SettingsHandoff.Describe(properties);

        Assert.Contains("(keep)", described, StringComparison.Ordinal);
        Assert.Contains("6000", described, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, described, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerSource_SecuresHandoffPropertiesAndRemovesThePreelevationFirewallComponent()
    {
        string root = RepositoryRoot();
        string listen = File.ReadAllText(Path.Combine(root, "installer", "ListenPage.cs"));
        string actions = File.ReadAllText(Path.Combine(root, "installer", "Vestigo.Installer.Actions", "CustomActions.cs"));
        string workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "build.yml"));

        foreach (string property in new[]
        {
            "VESTIGO_LISTEN_UI_MODE",
            "VESTIGO_LISTEN_MODE_EXPLICIT",
            "VESTIGO_PORT_EXPLICIT",
            "VESTIGO_LISTEN_MODE_PREFILL",
            "VESTIGO_PORT_PREFILL",
            "VESTIGO_LOAD_INITIALIZED"
        })
        {
            Assert.Contains($"SecureProperty(\"{property}\", null)", listen, StringComparison.Ordinal);
        }

        Assert.Contains("Keep existing listen address (recommended)", listen, StringComparison.Ordinal);
        Assert.Contains("const string RemovingProduct = \"NOT UPGRADINGPRODUCTCODE AND REMOVE~=\\\"ALL\\\"\"", listen, StringComparison.Ordinal);
        Assert.Contains("Sequence(\"VestigoRemoveFirewallRule\", \"Before\", \"RemoveFiles\", RemovingProduct)", listen, StringComparison.Ordinal);
        Assert.Contains("const string NotUpgrading = \"NOT UPGRADINGPRODUCTCODE\"", listen, StringComparison.Ordinal);
        Assert.Contains("Sequence(\"VestigoRollbackFirewallRule\", \"Before\", \"VestigoRemoveFirewallRule\", NotUpgrading)", listen, StringComparison.Ordinal);
        Assert.Contains("Sequence(\"VestigoCommitFirewallRule\", \"Before\", \"VestigoRemoveFirewallRule\", NotUpgrading)", listen, StringComparison.Ordinal);
        Assert.Contains("VestigoRollbackFirewallRule", actions, StringComparison.Ordinal);
        Assert.Contains("VestigoCommitFirewallRule", actions, StringComparison.Ordinal);
        Assert.DoesNotContain("WixExtension.Fire", listen, StringComparison.Ordinal);
        Assert.DoesNotContain("FirewallException", listen, StringComparison.Ordinal);
        Assert.Contains("FirewallRuleSync.Sync", actions, StringComparison.Ordinal);
        Assert.Contains("VestigoRemoveFirewallRule", actions, StringComparison.Ordinal);
        Assert.DoesNotContain("WixToolset.Firewall.wixext", workflow, StringComparison.Ordinal);
    }

    private static Handoff Execute(
        string? existingJson,
        bool isUpgrade,
        bool isInstalled,
        bool interactive,
        bool settingsProtected,
        Action<DictionaryInstallerProperties>? edit = null,
        Action<MemoryFirewallRuleStore>? seedFirewall = null)
    {
        DictionaryInstallerProperties properties = new();
        List<string> log = [];
        string? immediateJson = settingsProtected ? null : existingJson;
        if (interactive)
        {
            SettingsHandoff.Load(properties, immediateJson, settingsProtected, isUpgrade, isInstalled, log.Add);
            edit?.Invoke(properties);
            properties.Set(SettingsHandoff.ValidateTokenProperty, "0");
            SettingsHandoff.Apply(properties, immediateJson, settingsProtected, isUpgrade, isInstalled);
            Assert.True(string.IsNullOrEmpty(properties.Get(SettingsHandoff.ErrorProperty)), properties.Get(SettingsHandoff.ErrorProperty));
            properties.Set(SettingsHandoff.ValidateTokenProperty, "1");
            SettingsHandoff.Apply(properties, immediateJson, settingsProtected, isUpgrade, isInstalled);
            Assert.True(string.IsNullOrEmpty(properties.Get(SettingsHandoff.ErrorProperty)), properties.Get(SettingsHandoff.ErrorProperty));
            SettingsHandoff.Load(properties, immediateJson, settingsProtected, isUpgrade, isInstalled, log.Add);
        }
        else
        {
            edit?.Invoke(properties);
            SettingsHandoff.Load(properties, immediateJson, settingsProtected, isUpgrade, isInstalled, log.Add);
        }

        Dictionary<string, string> data = SettingsHandoff.Prepare(properties, isUpgrade, isInstalled, string.Empty);
        ResolvedSettings resolved = SettingsHandoff.ResolveWrite(data, ListenSettings.ReadExisting(existingJson));
        string merged;
        try
        {
            merged = ListenSettings.Merge(existingJson, resolved);
        }
        catch (FormatException)
        {
            merged = ListenSettings.Merge(null, resolved);
        }

        MemoryFirewallRuleStore firewall = new();
        seedFirewall?.Invoke(firewall);
        FirewallRuleSync.Sync(resolved, firewall, log.Add);
        AssertNoSecrets(log, resolved.Token);
        return new Handoff(properties, data, resolved, merged, firewall, log);
    }

    private static void AssertKept(Handoff run, string address, int port, string token)
    {
        ExistingSettings saved = ListenSettings.ReadExisting(run.Merged)!;
        Assert.Equal(address, saved.Address);
        Assert.Equal(port, saved.Port);
        if (token.Length == 0)
        {
            Assert.Empty(saved.Tokens);
        }
        else
        {
            Assert.Equal([token], saved.Tokens);
        }
    }

    private static void AssertRule(MemoryFirewallRuleStore store, int port)
    {
        FirewallRuleRecord rule = Assert.Single(store.Rules);
        Assert.Equal(FirewallRuleSync.RuleName, rule.Name);
        Assert.Equal(FirewallRuleSync.RuleDescription, rule.Description);
        Assert.Equal(FirewallRuleSync.ProtocolTcp, rule.Protocol);
        Assert.Equal(port.ToString(), rule.LocalPorts);
        Assert.Equal(FirewallRuleSync.RemoteAny, rule.RemoteAddresses);
        Assert.Equal(FirewallRuleSync.DirectionInbound, rule.Direction);
        Assert.Equal(FirewallRuleSync.ActionAllow, rule.Action);
        Assert.Equal(FirewallRuleSync.ProfilesAll, rule.Profiles);
        Assert.True(rule.Enabled);
        Assert.Equal(FirewallRuleSync.InterfaceTypesAll, rule.InterfaceTypes);
        Assert.Equal(FirewallRuleSync.LocalAddressesAny, rule.LocalAddresses);
        Assert.Equal(string.Empty, rule.ApplicationName);
        Assert.Equal(string.Empty, rule.ServiceName);
        Assert.Equal(string.Empty, rule.RemotePorts);
        Assert.False(rule.EdgeTraversal);
    }

    private static void AssertNoRule(MemoryFirewallRuleStore store) => Assert.Empty(store.Rules);

    private static void AssertNoSecrets(List<string> log, string token)
    {
        Assert.DoesNotContain(log, line => line.Contains(Token, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(token))
        {
            Assert.DoesNotContain(log, line => line.Contains(token, StringComparison.Ordinal));
        }
    }

    private static string Settings(string address, int port) =>
        "{ \"Vestigo\": { \"ListenAddress\": \"" + address + "\", \"ListenPort\": " + port.ToString() +
        ", \"ApiTokens\": [\"" + Token + "\"], \"SymbolServer\": \"keep-me\" } }";

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vestigo.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    private sealed class Handoff(
        DictionaryInstallerProperties properties,
        Dictionary<string, string> data,
        ResolvedSettings resolved,
        string merged,
        MemoryFirewallRuleStore firewall,
        List<string> log)
    {
        public DictionaryInstallerProperties Properties { get; } = properties;
        public Dictionary<string, string> Data { get; } = data;
        public ResolvedSettings Resolved { get; } = resolved;
        public string Merged { get; } = merged;
        public MemoryFirewallRuleStore Firewall { get; } = firewall;
        public List<string> Log { get; } = log;
    }
}
