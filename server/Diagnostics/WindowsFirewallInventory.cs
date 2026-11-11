using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.WindowsFirewall;
using Windows.Win32.System.Com;
using Windows.Win32.System.Ole;
using Windows.Win32.System.Variant;

namespace Vestigo.Diagnostics;

/// <summary>
/// Reads <c>INetFwPolicy2</c> through CsWin32. Rules are enumerated with <c>IEnumVARIANT</c> because
/// <c>INetFwRules</c> has no index. Getters only: no <c>put_</c>, <c>Add</c>, <c>Remove</c>,
/// <c>EnableRuleGroup</c>, or <c>RestoreLocalFirewallDefaults</c>. Interface SAFEARRAYs are not projected.
/// </summary>
internal sealed unsafe class WindowsFirewallInventory : IFirewallInventory
{
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const int EAccessDenied = unchecked((int)0x80070005);
    private const int VariantTypeMask = 0x0FFF;

    public OperationResult<FirewallProfiles> ReadProfiles()
    {
        try
        {
            using ComScope com = new();
            return OperationResult<FirewallProfiles>.Ok(ReadProfiles(com.CreatePolicy()));
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Fail<FirewallProfiles>(ex);
        }
    }

    public OperationResult<FirewallRuleList> ListRules(int limit)
    {
        try
        {
            using ComScope com = new();
            INetFwPolicy2* policy = com.CreatePolicy();
            return OperationResult<FirewallRuleList>.Ok(ReadRules(com, com.Own(policy->Rules), limit));
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Fail<FirewallRuleList>(ex);
        }
    }

    private static FirewallProfiles ReadProfiles(INetFwPolicy2* policy)
    {
        int mask = policy->CurrentProfileTypes;
        int? modify = null;
        string? modifyName = null;
        try
        {
            modify = (int)policy->LocalPolicyModifyState;
            modifyName = FirewallProjection.ModifyStateName(modify.Value);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
        }

        IReadOnlyList<string> names = FirewallProjection.CurrentProfileNames(mask, out int unknown);
        FirewallProfile[] profiles =
        [
            ReadProfile(policy, "domain", NET_FW_PROFILE_TYPE2.NET_FW_PROFILE2_DOMAIN),
            ReadProfile(policy, "private", NET_FW_PROFILE_TYPE2.NET_FW_PROFILE2_PRIVATE),
            ReadProfile(policy, "public", NET_FW_PROFILE_TYPE2.NET_FW_PROFILE2_PUBLIC)
        ];
        return new FirewallProfiles(mask, names, unknown, modify, modifyName, profiles);
    }

    private static FirewallProfile ReadProfile(INetFwPolicy2* policy, string name, NET_FW_PROFILE_TYPE2 type)
    {
        int? inbound = ReadAction(() => policy->get_DefaultInboundAction(type));
        int? outbound = ReadAction(() => policy->get_DefaultOutboundAction(type));
        return new FirewallProfile(
            name,
            (int)type,
            ReadBool(() => policy->get_FirewallEnabled(type)),
            ReadBool(() => policy->get_BlockAllInboundTraffic(type)),
            ReadBool(() => policy->get_NotificationsDisabled(type)),
            ReadBool(() => policy->get_UnicastResponsesToMulticastBroadcastDisabled(type)),
            inbound,
            inbound is int inboundValue ? FirewallProjection.ActionName(inboundValue) : null,
            outbound,
            outbound is int outboundValue ? FirewallProjection.ActionName(outboundValue) : null);
    }

    private static FirewallRuleList ReadRules(ComScope com, INetFwRules* rules, int limit)
    {
        int? total = null;
        try
        {
            total = rules->Count;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
        }

        IUnknown* unknown = com.Own(rules->_NewEnum);
        unknown->QueryInterface(out IEnumVARIANT* enumerator).ThrowOnFailure();
        com.Own(enumerator);
        List<FirewallRule> items = [];
        bool incomplete = false;
        bool exhausted = false;
        int scanned = 0;
        while (scanned < limit)
        {
            VARIANT item = default;
            uint fetched = 0;
            HRESULT next = enumerator->Next(1, &item, &fetched);
            try
            {
                if (next.Failed)
                {
                    next.ThrowOnFailure();
                }

                if (next.Value == 1 || fetched == 0)
                {
                    exhausted = true;
                    break;
                }

                scanned++;
                if (!TryReadRule(com, item, out FirewallRule? rule))
                {
                    incomplete = true;
                    continue;
                }

                items.Add(rule!);
            }
            finally
            {
                PInvoke.VariantClear(ref item);
            }
        }

        bool truncated = !exhausted && (total is null || scanned < total.Value);
        return new FirewallRuleList(items, items.Count, total, truncated, incomplete);
    }

    private static bool TryReadRule(ComScope com, VARIANT item, out FirewallRule? rule)
    {
        rule = null;
        int kind = (int)item.vt & VariantTypeMask;
        IUnknown* unknown = kind switch
        {
            (int)VARENUM.VT_DISPATCH => (IUnknown*)item.pdispVal,
            (int)VARENUM.VT_UNKNOWN => item.punkVal,
            _ => null
        };
        if (unknown is null)
        {
            return false;
        }

        HRESULT rule3 = unknown->QueryInterface(out INetFwRule3* newer);
        if (rule3.Succeeded && newer is not null)
        {
            com.Own(newer);
            rule = Project(newer);
            return true;
        }

        HRESULT older = unknown->QueryInterface(out INetFwRule* basic);
        if (older.Succeeded && basic is not null)
        {
            com.Own(basic);
            rule = Project(basic);
            return true;
        }

        return false;
    }

    private static FirewallRule Project(INetFwRule3* rule)
    {
        bool truncated = false;
        int? protocol = ReadInt(() => rule->Protocol);
        int? direction = ReadInt(() => (int)rule->Direction);
        int? action = ReadInt(() => (int)rule->Action);
        int? edge = ReadInt(() => rule->EdgeTraversalOptions);
        return new FirewallRule(
            ReadText(() => rule->Name, ref truncated),
            ReadText(() => rule->Description, ref truncated),
            ReadText(() => rule->ApplicationName, ref truncated),
            ReadText(() => rule->ServiceName, ref truncated),
            protocol,
            protocol is int protocolValue ? FirewallProjection.ProtocolName(protocolValue) : null,
            ReadText(() => rule->LocalPorts, ref truncated),
            ReadText(() => rule->RemotePorts, ref truncated),
            ReadText(() => rule->LocalAddresses, ref truncated),
            ReadText(() => rule->RemoteAddresses, ref truncated),
            ReadText(() => rule->IcmpTypesAndCodes, ref truncated),
            direction,
            direction is int directionValue ? FirewallProjection.DirectionName(directionValue) : null,
            ReadText(() => rule->InterfaceTypes, ref truncated),
            ReadBool(() => rule->Enabled),
            ReadText(() => rule->Grouping, ref truncated),
            ReadInt(() => rule->Profiles),
            ReadBool(() => rule->EdgeTraversal),
            action,
            action is int actionValue ? FirewallProjection.ActionName(actionValue) : null,
            edge,
            edge is int edgeValue ? FirewallProjection.EdgeTraversalName(edgeValue) : null,
            ReadText(() => rule->LocalAppPackageId, ref truncated),
            ReadText(() => rule->LocalUserOwner, ref truncated),
            ReadText(() => rule->LocalUserAuthorizedList, ref truncated),
            ReadText(() => rule->RemoteUserAuthorizedList, ref truncated),
            ReadText(() => rule->RemoteMachineAuthorizedList, ref truncated),
            ReadInt(() => rule->SecureFlags),
            truncated);
    }

    private static FirewallRule Project(INetFwRule* rule)
    {
        bool truncated = false;
        int? protocol = ReadInt(() => rule->Protocol);
        int? direction = ReadInt(() => (int)rule->Direction);
        int? action = ReadInt(() => (int)rule->Action);
        return new FirewallRule(
            ReadText(() => rule->Name, ref truncated),
            ReadText(() => rule->Description, ref truncated),
            ReadText(() => rule->ApplicationName, ref truncated),
            ReadText(() => rule->ServiceName, ref truncated),
            protocol,
            protocol is int protocolValue ? FirewallProjection.ProtocolName(protocolValue) : null,
            ReadText(() => rule->LocalPorts, ref truncated),
            ReadText(() => rule->RemotePorts, ref truncated),
            ReadText(() => rule->LocalAddresses, ref truncated),
            ReadText(() => rule->RemoteAddresses, ref truncated),
            ReadText(() => rule->IcmpTypesAndCodes, ref truncated),
            direction,
            direction is int directionValue ? FirewallProjection.DirectionName(directionValue) : null,
            ReadText(() => rule->InterfaceTypes, ref truncated),
            ReadBool(() => rule->Enabled),
            ReadText(() => rule->Grouping, ref truncated),
            ReadInt(() => rule->Profiles),
            ReadBool(() => rule->EdgeTraversal),
            action,
            action is int actionValue ? FirewallProjection.ActionName(actionValue) : null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            truncated);
    }

    private static string? ReadText(Func<BSTR> read, ref bool truncated)
    {
        BSTR value;
        try
        {
            value = read();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            return FirewallProjection.Bound(value.ToString(), ref truncated);
        }
        finally
        {
            PInvoke.SysFreeString(value);
        }
    }

    private static bool? ReadBool(Func<VARIANT_BOOL> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? ReadInt(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? ReadAction(Func<NET_FW_ACTION> read)
    {
        try
        {
            return (int)read();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static OperationResult<T> Fail<T>(Exception ex)
    {
        int hr = ex.HResult;
        if (hr == EAccessDenied)
        {
            return OperationResult<T>.Fail("access_denied", "Windows Firewall refused the query.");
        }

        return OperationResult<T>.Fail("firewall_api_failed", "Windows Firewall query failed: 0x" + hr.ToString("X8") + ".");
    }

    /// <summary>Owns each acquired COM reference and the apartment initialized for this call.</summary>
    private sealed class ComScope : IDisposable
    {
        private readonly List<nint> _interfaces = [];
        private readonly bool _uninit;

        public ComScope()
        {
            HRESULT hr = PInvoke.CoInitializeEx(COINIT.COINIT_MULTITHREADED);
            _uninit = hr.Succeeded;
            if (hr.Failed && hr.Value != RpcEChangedMode)
            {
                hr.ThrowOnFailure();
            }
        }

        public INetFwPolicy2* CreatePolicy()
        {
            NetFwPolicy2.CreateInstance(out INetFwPolicy2* value).ThrowOnFailure();
            return Own(value);
        }

        public T* Own<T>(T* value) where T : unmanaged
        {
            if (value is null)
            {
                throw new InvalidOperationException("A successful COM call returned a null interface.");
            }

            _interfaces.Add((nint)value);
            return value;
        }

        public void Dispose()
        {
            try
            {
                for (int i = _interfaces.Count - 1; i >= 0; i--)
                {
                    ((IUnknown*)_interfaces[i])->Release();
                }
            }
            finally
            {
                if (_uninit)
                {
                    PInvoke.CoUninitialize();
                }
            }
        }
    }
}
