#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Vestigo.Installer.Actions.Logic
{
    /// <summary>
    ///     Late-bound HNetCfg.FwPolicy2 store so the custom-action package does not take a firewall assembly dependency.
    ///     Compiled into the net48 custom action and into the tests, so both exercise the same COM binding code.
    ///     CsWin32 is not available to the net48 custom action, which is the documented reason this stays late bound.
    /// </summary>
#if NET
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
    public sealed class WindowsFirewallRuleStore : IFirewallRuleStore, IFirewallLookupDiagnostics, IDisposable
    {
        private object? _policy;
        private object? _rules;

        public bool LastLookupWasComplete { get; private set; }

        public WindowsFirewallRuleStore()
        {
            Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType == null)
            {
                throw new InvalidOperationException("HNetCfg.FwPolicy2 is not available.");
            }

            _policy = Activator.CreateInstance(policyType);
            _rules = GetProperty(_policy!, "Rules");
            if (_rules == null)
            {
                throw new InvalidOperationException("The Windows Firewall policy did not return its rules collection.");
            }
        }

        public IReadOnlyList<FirewallRuleRecord> FindByName(string name)
        {
            List<FirewallRuleRecord> found = new List<FirewallRuleRecord>();
            LastLookupWasComplete = TryCollectByName(name, found);
            if (LastLookupWasComplete)
            {
                return found;
            }

            object? rule = null;
            try
            {
                rule = TryItem(name);
                if (rule == null)
                {
                    return new FirewallRuleRecord[0];
                }

                return new[] { ReadComRule(rule) };
            }
            finally
            {
                Release(ref rule);
            }
        }

        public void Remove(string name)
        {
            Invoke(_rules!, "Remove", new object[] { name }, BindingFlags.InvokeMethod);
        }

        public void Add(FirewallRuleRecord rule)
        {
            Type? ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule");
            if (ruleType == null)
            {
                throw new InvalidOperationException("HNetCfg.FWRule is not available.");
            }

            object? comRule = Activator.CreateInstance(ruleType);
            try
            {
                PopulateComRule(comRule!, rule);
                Invoke(_rules!, "Add", new object[] { comRule! }, BindingFlags.InvokeMethod);
            }
            finally
            {
                Release(ref comRule);
            }
        }

        public void Dispose()
        {
            Release(ref _rules);
            Release(ref _policy);
        }

        /// <summary>
        ///     Writes <paramref name="rule" /> onto an <c>HNetCfg.FWRule</c> in the order and with the omissions Windows
        ///     Firewall requires: the protocol before any port, the direction before edge traversal, and no empty strings
        ///     for optional properties (an empty RemotePorts is rejected with E_INVALIDARG). Ports are only written for TCP
        ///     and UDP. Does not touch machine policy until the rule is added to a rules collection.
        /// </summary>
        public static void PopulateComRule(object comRule, FirewallRuleRecord rule)
        {
            SetProperty(comRule, "Name", rule.Name);
            SetIfNotEmpty(comRule, "Description", rule.Description);
            SetProperty(comRule, "Protocol", rule.Protocol);
            if (FirewallRuleSync.ProtocolSupportsPorts(rule.Protocol))
            {
                SetIfNotEmpty(comRule, "LocalPorts", rule.LocalPorts);
                SetIfNotEmpty(comRule, "RemotePorts", rule.RemotePorts);
            }

            SetIfNotEmpty(comRule, "RemoteAddresses", rule.RemoteAddresses);
            SetIfNotEmpty(comRule, "LocalAddresses", rule.LocalAddresses);
            SetProperty(comRule, "Direction", rule.Direction);
            SetProperty(comRule, "Action", rule.Action);
            SetProperty(comRule, "Profiles", rule.Profiles);
            SetIfNotEmpty(comRule, "InterfaceTypes", rule.InterfaceTypes);
            SetIfNotEmpty(comRule, "ApplicationName", rule.ApplicationName);
            SetIfNotEmpty(comRule, "ServiceName", rule.ServiceName);
            if (rule.EdgeTraversal)
            {
                SetProperty(comRule, "EdgeTraversal", true);
            }

            SetProperty(comRule, "Enabled", rule.Enabled);
        }

        /// <summary>Reads every property the installer manages from an <c>INetFwRule</c> COM object.</summary>
        public static FirewallRuleRecord ReadComRule(object rule)
        {
            return new FirewallRuleRecord(
                GetString(rule, "Name"),
                GetString(rule, "Description"),
                GetInt(rule, "Protocol"),
                GetString(rule, "LocalPorts"),
                GetString(rule, "RemoteAddresses"),
                GetInt(rule, "Direction"),
                GetInt(rule, "Action"),
                GetInt(rule, "Profiles"),
                GetBool(rule, "Enabled"),
                GetString(rule, "InterfaceTypes"),
                GetString(rule, "LocalAddresses"),
                GetString(rule, "ApplicationName"),
                GetString(rule, "ServiceName"),
                GetString(rule, "RemotePorts"),
                GetBool(rule, "EdgeTraversal"));
        }

        private bool TryCollectByName(string name, List<FirewallRuleRecord> found)
        {
            IEnumerable? enumerable = _rules as IEnumerable;
            if (enumerable == null)
            {
                return false;
            }

            IEnumerator? enumerator = null;
            try
            {
                enumerator = enumerable.GetEnumerator();
                if (enumerator == null)
                {
                    return false;
                }

                while (enumerator.MoveNext())
                {
                    object? rule = enumerator.Current;
                    try
                    {
                        if (rule != null && string.Equals(GetString(rule, "Name"), name, StringComparison.Ordinal))
                        {
                            found.Add(ReadComRule(rule));
                        }
                    }
                    finally
                    {
                        Release(ref rule);
                    }
                }

                return true;
            }
            catch (Exception)
            {
                found.Clear();
                return false;
            }
            finally
            {
                try
                {
                    IDisposable? disposable = enumerator as IDisposable;
                    if (disposable != null)
                    {
                        disposable.Dispose();
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private object? TryItem(string name)
        {
            object? rule;
            if (!TryItemCall(name, BindingFlags.GetProperty, out rule))
            {
                TryItemCall(name, BindingFlags.InvokeMethod, out rule);
            }

            return rule;
        }

        /// <summary>
        ///     Returns true when the call produced an answer, including a missing rule. Returns false when this binding
        ///     failed and the other binding should be tried. A missing-rule HRESULT is absence; other COM errors propagate
        ///     from the method binding.
        /// </summary>
        private bool TryItemCall(string name, BindingFlags flags, out object? rule)
        {
            rule = null;
            try
            {
                rule = Invoke(_rules!, "Item", new object[] { name }, flags);
                return true;
            }
            catch (Exception ex) when (IsComFailure(ex))
            {
                if (FirewallRuleSync.IsRuleMissing(ComError(ex)))
                {
                    return true;
                }

                if (flags == BindingFlags.InvokeMethod)
                {
                    throw;
                }

                return false;
            }
        }

        private static int ComError(Exception ex)
        {
            COMException? com = ex as COMException;
            if (com == null)
            {
                TargetInvocationException? invocation = ex as TargetInvocationException;
                com = invocation == null ? null : invocation.InnerException as COMException;
            }

            return com == null ? 0 : com.ErrorCode;
        }

        private static object? GetProperty(object target, string name)
        {
            return Invoke(target, name, null, BindingFlags.GetProperty);
        }

        private static void SetIfNotEmpty(object target, string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                SetProperty(target, name, value!);
            }
        }

        /// <summary>Sets one property. A failure names the property and carries the inner COM HRESULT.</summary>
        private static void SetProperty(object target, string name, object value)
        {
            try
            {
                Invoke(target, name, new object[] { value }, BindingFlags.SetProperty);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not set firewall rule property '" + name + "': " + FirewallRuleSync.FormatError(ex),
                    ex);
            }
        }

        private static object? Invoke(object target, string name, object[]? args, BindingFlags flags)
        {
            return target.GetType().InvokeMember(
                name,
                flags | BindingFlags.Public | BindingFlags.Instance,
                null,
                target,
                args);
        }

        private static string GetString(object target, string name)
        {
            object? value = GetProperty(target, name);
            return value == null ? string.Empty : value.ToString() ?? string.Empty;
        }

        private static int GetInt(object target, string name)
        {
            object? value = GetProperty(target, name);
            return value == null ? 0 : Convert.ToInt32(value);
        }

        private static bool GetBool(object target, string name)
        {
            object? value = GetProperty(target, name);
            return value is bool flag && flag;
        }

        private static bool IsComFailure(Exception ex)
        {
            if (ex is COMException)
            {
                return true;
            }

            TargetInvocationException? invocation = ex as TargetInvocationException;
            return invocation != null && invocation.InnerException is COMException;
        }

        private static void Release(ref object? com)
        {
            if (com != null && Marshal.IsComObject(com))
            {
                Marshal.FinalReleaseComObject(com);
            }

            com = null;
        }
    }
}
