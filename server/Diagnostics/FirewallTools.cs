using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;

namespace Vestigo.Diagnostics;

/// <summary>Gated, read-only Windows Firewall profile and rule inventory. Stored actions are not connection verdicts.</summary>
[McpServerToolType]
public sealed class FirewallTools(IFirewallInventory inventory, VestigoOptions options, ILogger<FirewallTools> logger)
{
    [McpServerTool(Name = "get_firewall_profiles")]
    [Description(
        "Reads Windows Firewall profile policy. Requires Vestigo:Diagnostics:AllowFirewallInventory. "
        + "Returns the current profile bitmask, unknown bits outside domain, private, and public, the local policy modify state, "
        + "and firewallEnabled, blockAllInboundTraffic, notification, unicast-response, and default inbound and outbound actions for the domain, private, and public profiles. "
        + "Actions stay integers. A stored block or allow is not a verdict about a connection, and silence from a probe is not reported as a firewall block. "
        + "This does not change policy.")]
    public OperationResult<FirewallProfiles> GetFirewallProfiles()
    {
        if (!options.Diagnostics.AllowFirewallInventory)
        {
            return OperationResult<FirewallProfiles>.Fail(
                "firewall_inventory_disabled",
                "Firewall inventory is disabled. Set Vestigo:Diagnostics:AllowFirewallInventory to true.");
        }

        OperationResult<FirewallProfiles> result = inventory.ReadProfiles();
        logger.LogInformation(
            "get_firewall_profiles success {Success} code {Code}",
            result.Success,
            result.ErrorCode);
        return result;
    }

    [McpServerTool(Name = "list_firewall_rules")]
    [Description(
        "Lists stored Windows Firewall rules in collection order. Requires Vestigo:Diagnostics:AllowFirewallInventory. "
        + "Address, port, ICMP, interface, package, and user fields are the raw Windows expressions, capped at 512 characters. "
        + "profiles is the stored profile bitmask. action is the stored action and is not a verdict about a connection. "
        + "limit is clamped to 1..Vestigo:Diagnostics:MaxFirewallRules. "
        + "Interface SAFEARRAYs are omitted. This does not change rules and does not report whether traffic was blocked.")]
    public OperationResult<FirewallRuleList> ListFirewallRules(
        [Description("Maximum rules. Clamped to 1..Vestigo:Diagnostics:MaxFirewallRules.")] int limit = 50)
    {
        if (!options.Diagnostics.AllowFirewallInventory)
        {
            return OperationResult<FirewallRuleList>.Fail(
                "firewall_inventory_disabled",
                "Firewall inventory is disabled. Set Vestigo:Diagnostics:AllowFirewallInventory to true.");
        }

        int bounded = Math.Clamp(limit, 1, options.Diagnostics.MaxFirewallRules);
        OperationResult<FirewallRuleList> result = inventory.ListRules(bounded);
        logger.LogInformation(
            "list_firewall_rules success {Success} code {Code} returned {Returned}",
            result.Success,
            result.ErrorCode,
            result.Value?.Returned);
        return result;
    }
}
