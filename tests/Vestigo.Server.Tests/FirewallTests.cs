using Microsoft.Extensions.Logging.Abstractions;

using Vestigo.Configuration;
using Vestigo.Diagnostics;
using Vestigo.Installer.Actions.Logic;

namespace Vestigo.Server.Tests;

public class FirewallTests
{
    [Fact]
    public void Projection_KeepsUnknownBitsAndRefusesUnnamedConstants()
    {
        IReadOnlyList<string> names = FirewallProjection.CurrentProfileNames(0x9, out int unknown);

        Assert.Equal(["domain"], names);
        Assert.Equal(0x8, unknown);
        Assert.Null(FirewallProjection.ActionName(5));
        Assert.Equal("allow", FirewallProjection.ActionName(1));
        Assert.Null(FirewallProjection.DirectionName(0));
        Assert.Null(FirewallProjection.ProtocolName(1));
        Assert.Equal("udp", FirewallProjection.ProtocolName(17));
        Assert.Null(FirewallProjection.ModifyStateName(9));
        Assert.Null(FirewallProjection.EdgeTraversalName(4));

        bool truncated = false;
        string? bounded = FirewallProjection.Bound(new string('a', FirewallProjection.MaxTextChars + 3), ref truncated);
        Assert.True(truncated);
        Assert.Equal(FirewallProjection.MaxTextChars, bounded!.Length);
    }

    [Fact]
    public void Tools_StayDisabledUntilTheGateIsOn()
    {
        FakeFirewall firewall = new();
        FirewallTools tools = new(firewall, new VestigoOptions(), NullLogger<FirewallTools>.Instance);

        OperationResult<FirewallProfiles> profiles = tools.GetFirewallProfiles();
        OperationResult<FirewallRuleList> rules = tools.ListFirewallRules();

        Assert.False(profiles.Success);
        Assert.Equal("firewall_inventory_disabled", profiles.ErrorCode);
        Assert.False(rules.Success);
        Assert.Equal("firewall_inventory_disabled", rules.ErrorCode);
        Assert.Equal(0, firewall.Calls);
    }

    [Fact]
    public void Tools_ReturnStoredFactsWithoutAConnectionVerdict()
    {
        FakeFirewall firewall = new();
        FirewallTools tools = new(
            firewall,
            new VestigoOptions { Diagnostics = { AllowFirewallInventory = true, MaxFirewallRules = 10 } },
            NullLogger<FirewallTools>.Instance);

        OperationResult<FirewallProfiles> profiles = tools.GetFirewallProfiles();
        OperationResult<FirewallRuleList> rules = tools.ListFirewallRules(50);

        Assert.True(profiles.Success);
        Assert.Equal(0x9, profiles.Value!.CurrentProfileTypes);
        Assert.Equal(0x8, profiles.Value.UnknownCurrentProfileBits);
        Assert.Equal(1, profiles.Value.Profiles[0].DefaultInboundAction);
        Assert.Equal("allow", profiles.Value.Profiles[0].DefaultInboundActionName);
        Assert.True(rules.Success);
        Assert.Equal(1, rules.Value!.Returned);
        Assert.Equal("C:\\Windows\\System32\\contoso.sys", rules.Value.Rules[0].ApplicationName);
        Assert.Equal("1,80,443", rules.Value.Rules[0].LocalPorts);
        Assert.Equal(0x7, rules.Value.Rules[0].Profiles);
        Assert.Equal(0, rules.Value.Rules[0].Action);
        Assert.Equal("block", rules.Value.Rules[0].ActionName);
        Assert.DoesNotContain("blockedByFirewall", System.Text.Json.JsonSerializer.Serialize(rules.Value), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, firewall.Calls);
    }

    [Fact]
    public void Installer_NestsFirewallInventoryUnderDiagnosticsAndLeavesItOff()
    {
        Assert.False(ModuleSelection.Defaults()[ModuleSelection.DiagnosticsFirewallInventory]);
        Assert.Equal("Diagnostics", ModuleSelection.Find(ModuleSelection.DiagnosticsFirewallInventory)?.Parent);
    }

    [Fact]
    public void Windows_ReadsProfilesAndABoundedRulePage()
    {
        WindowsFirewallInventory inventory = new();

        OperationResult<FirewallProfiles> profiles = inventory.ReadProfiles();
        OperationResult<FirewallRuleList> rules = inventory.ListRules(5);

        Assert.True(profiles.Success, profiles.Error);
        Assert.Equal(3, profiles.Value!.Profiles.Count);
        Assert.Equal(["domain", "private", "public"], profiles.Value.Profiles.Select(profile => profile.Name));
        Assert.Equal([1, 2, 4], profiles.Value.Profiles.Select(profile => profile.ProfileType));
        Assert.True(rules.Success, rules.Error);
        Assert.InRange(rules.Value!.Returned, 0, 5);
        Assert.All(rules.Value.Rules, rule => Assert.True(rule.LocalPorts is null || rule.LocalPorts.Length <= FirewallProjection.MaxTextChars));
        Assert.DoesNotContain("blockedByFirewall", System.Text.Json.JsonSerializer.Serialize(rules.Value), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeFirewall : IFirewallInventory
    {
        public int Calls { get; private set; }

        public OperationResult<FirewallProfiles> ReadProfiles()
        {
            Calls++;
            FirewallProfile domain = new("domain", 1, true, false, false, false, 1, "allow", 0, "block");
            return OperationResult<FirewallProfiles>.Ok(new FirewallProfiles(0x9, ["domain"], 0x8, 0, "ok", [domain]));
        }

        public OperationResult<FirewallRuleList> ListRules(int limit)
        {
            Calls++;
            Assert.Equal(10, limit);
            FirewallRule rule = new(
                "Contoso",
                null,
                "C:\\Windows\\System32\\contoso.sys",
                null,
                6,
                "tcp",
                "1,80,443",
                "*",
                "*",
                "LocalSubnet",
                null,
                1,
                "in",
                "All",
                true,
                null,
                0x7,
                false,
                0,
                "block",
                0,
                "deny",
                null,
                null,
                null,
                null,
                null,
                0,
                false);
            return OperationResult<FirewallRuleList>.Ok(new FirewallRuleList([rule], 1, 1, false, false));
        }
    }
}
