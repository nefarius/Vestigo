namespace Vestigo.Diagnostics;

/// <summary>Stored Windows Firewall profile facts. Names describe documented bits. Unknown bits stay in the integers.</summary>
public sealed record FirewallProfile(
    string Name,
    int ProfileType,
    bool? FirewallEnabled,
    bool? BlockAllInboundTraffic,
    bool? NotificationsDisabled,
    bool? UnicastResponsesToMulticastBroadcastDisabled,
    int? DefaultInboundAction,
    string? DefaultInboundActionName,
    int? DefaultOutboundAction,
    string? DefaultOutboundActionName);

/// <summary>Current profile bitmask and the three documented profiles. This is not a verdict about any connection.</summary>
public sealed record FirewallProfiles(
    int CurrentProfileTypes,
    IReadOnlyList<string> CurrentProfileNames,
    int UnknownCurrentProfileBits,
    int? LocalPolicyModifyState,
    string? LocalPolicyModifyStateName,
    IReadOnlyList<FirewallProfile> Profiles);

/// <summary>
/// One stored firewall rule. Address, port, and user fields are the raw Windows expressions.
/// <see cref="Action" /> is the stored action, not a claim that traffic was blocked.
/// </summary>
public sealed record FirewallRule(
    string? Name,
    string? Description,
    string? ApplicationName,
    string? ServiceName,
    int? Protocol,
    string? ProtocolName,
    string? LocalPorts,
    string? RemotePorts,
    string? LocalAddresses,
    string? RemoteAddresses,
    string? IcmpTypesAndCodes,
    int? Direction,
    string? DirectionName,
    string? InterfaceTypes,
    bool? Enabled,
    string? Grouping,
    int? Profiles,
    bool? EdgeTraversal,
    int? Action,
    string? ActionName,
    int? EdgeTraversalOptions,
    string? EdgeTraversalOptionsName,
    string? LocalAppPackageId,
    string? LocalUserOwner,
    string? LocalUserAuthorizedList,
    string? RemoteUserAuthorizedList,
    string? RemoteMachineAuthorizedList,
    int? SecureFlags,
    bool TextTruncated);

/// <summary>A bounded page of stored rules in Windows Firewall collection order.</summary>
public sealed record FirewallRuleList(
    IReadOnlyList<FirewallRule> Rules,
    int Returned,
    int? Total,
    bool Truncated,
    bool Incomplete);

/// <summary>Read-only Windows Firewall profiles and rules.</summary>
public interface IFirewallInventory
{
    OperationResult<FirewallProfiles> ReadProfiles();

    OperationResult<FirewallRuleList> ListRules(int limit);
}
