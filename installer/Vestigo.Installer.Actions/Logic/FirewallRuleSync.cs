#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Vestigo.Installer.Actions.Logic
{
    /// <summary>One Windows Firewall rule the elevated installer creates or removes.</summary>
    public sealed class FirewallRuleRecord
    {
        public FirewallRuleRecord(
            string name,
            string description,
            int protocol,
            string localPorts,
            string remoteAddresses,
            int direction,
            int action,
            int profiles,
            bool enabled,
            string interfaceTypes,
            string localAddresses,
            string applicationName,
            string serviceName,
            string remotePorts,
            bool edgeTraversal)
        {
            Name = name;
            Description = description;
            Protocol = protocol;
            LocalPorts = localPorts;
            RemoteAddresses = remoteAddresses;
            Direction = direction;
            Action = action;
            Profiles = profiles;
            Enabled = enabled;
            InterfaceTypes = interfaceTypes ?? string.Empty;
            LocalAddresses = localAddresses ?? string.Empty;
            ApplicationName = applicationName ?? string.Empty;
            ServiceName = serviceName ?? string.Empty;
            RemotePorts = remotePorts ?? string.Empty;
            EdgeTraversal = edgeTraversal;
        }

        public string Name { get; }

        public string Description { get; }

        public int Protocol { get; }

        public string LocalPorts { get; }

        public string RemoteAddresses { get; }

        public int Direction { get; }

        public int Action { get; }

        public int Profiles { get; }

        public bool Enabled { get; }

        /// <summary>Interface types such as <c>All</c> or <c>Wireless</c>. A restored rule keeps the captured value.</summary>
        public string InterfaceTypes { get; }

        public string LocalAddresses { get; }

        public string ApplicationName { get; }

        public string ServiceName { get; }

        public string RemotePorts { get; }

        public bool EdgeTraversal { get; }
    }

    /// <summary>
    ///     Optional store capability: reports whether the last <see cref="IFirewallRuleStore.FindByName" /> enumerated every
    ///     rule. A lookup that fell back to a single by-name item cannot prove that no duplicate exists.
    /// </summary>
    public interface IFirewallLookupDiagnostics
    {
        bool LastLookupWasComplete { get; }
    }

    /// <summary>
    ///     Firewall store used by <see cref="FirewallRuleSync" />. Methods throw when the firewall service rejects the change.
    /// </summary>
    public interface IFirewallRuleStore
    {
        IReadOnlyList<FirewallRuleRecord> FindByName(string name);

        void Remove(string name);

        void Add(FirewallRuleRecord rule);
    }

    /// <summary>In-memory store for tests and the packaged custom-action smoke check.</summary>
    public sealed class MemoryFirewallRuleStore : IFirewallRuleStore
    {
        private readonly List<FirewallRuleRecord> _rules = new List<FirewallRuleRecord>();

        public bool Fail { get; set; }

        /// <summary>Number of following <see cref="Add" /> calls that throw. Find and remove still work.</summary>
        public int FailAdds { get; set; }

        public string FailMessage { get; set; } = "firewall unavailable";

        public IReadOnlyList<FirewallRuleRecord> Rules
        {
            get { return _rules; }
        }

        public IReadOnlyList<FirewallRuleRecord> FindByName(string name)
        {
            if (Fail)
            {
                throw new InvalidOperationException(FailMessage);
            }

            List<FirewallRuleRecord> found = new List<FirewallRuleRecord>();
            foreach (FirewallRuleRecord rule in _rules)
            {
                if (string.Equals(rule.Name, name, StringComparison.Ordinal))
                {
                    found.Add(rule);
                }
            }

            return found;
        }

        public void Remove(string name)
        {
            if (Fail)
            {
                throw new InvalidOperationException(FailMessage);
            }

            for (int i = 0; i < _rules.Count; i++)
            {
                if (string.Equals(_rules[i].Name, name, StringComparison.Ordinal))
                {
                    _rules.RemoveAt(i);
                    return;
                }
            }

            throw new InvalidOperationException("No firewall rule named '" + name + "'.");
        }

        public void Add(FirewallRuleRecord rule)
        {
            if (Fail || FailAdds > 0)
            {
                if (FailAdds > 0)
                {
                    FailAdds--;
                }

                throw new InvalidOperationException(FailMessage);
            }

            _rules.Add(rule);
        }
    }

    /// <summary>
    ///     Makes the inbound "Vestigo MCP Server" rule match the listen settings written in the same elevated action.
    ///     Failures are logged and do not fail the install.
    /// </summary>
    public static class FirewallRuleSync
    {
        public const string RuleName = "Vestigo MCP Server";
        public const string RuleDescription = "Inbound TCP port for the Vestigo MCP server.";
        public const int ProtocolTcp = 6;
        public const int ProtocolUdp = 17;
        public const int DirectionInbound = 1;
        public const int ActionAllow = 1;
        public const int ProfilesAll = 0x7FFFFFFF;
        public const string RemoteAny = "*";
        public const string RemotePortsAny = "*";
        public const string InterfaceTypesAll = "All";
        public const string LocalAddressesAny = "*";
        public const int MaxRemovals = 32;

        /// <summary>Windows Firewall accepts local and remote ports only for TCP and UDP rules.</summary>
        public static bool ProtocolSupportsPorts(int protocol)
        {
            return protocol == ProtocolTcp || protocol == ProtocolUdp;
        }

        public static FirewallRuleRecord CreateRule(int port, string ruleName = RuleName)
        {
            return new FirewallRuleRecord(
                ruleName,
                RuleDescription,
                ProtocolTcp,
                port.ToString(CultureInfo.InvariantCulture),
                RemoteAny,
                DirectionInbound,
                ActionAllow,
                ProfilesAll,
                true,
                InterfaceTypesAll,
                LocalAddressesAny,
                string.Empty,
                string.Empty,
                RemotePortsAny,
                false);
        }

        /// <summary>
        ///     Unwraps reflection wrappers and appends the COM HRESULT, so the MSI log names the real failure instead of
        ///     "Exception has been thrown by the target of an invocation".
        /// </summary>
        public static string FormatError(Exception ex)
        {
            Exception cause = ex;
            while (cause is TargetInvocationException && cause.InnerException != null)
            {
                cause = cause.InnerException;
            }

            string text = cause.GetType().Name + ": " + cause.Message;
            COMException? com = cause as COMException;
            if (com == null)
            {
                com = cause.InnerException as COMException;
            }

            if (com != null)
            {
                text += " (HRESULT 0x" + com.ErrorCode.ToString("X8", CultureInfo.InvariantCulture) + ")";
            }

            return text;
        }

        /// <summary>True for the HRESULTs Windows uses when a named firewall rule does not exist.</summary>
        public static bool IsRuleMissing(int hresult)
        {
            int code = hresult & 0xFFFF;
            return code == 2 || code == 1168;
        }

        /// <summary>
        ///     Leaves one matching inbound rule when the server binds beyond loopback, or removes the rule for localhost.
        ///     A failed replacement restores the rules that were present before the attempt.
        /// </summary>
        public static void Sync(ResolvedSettings resolved, IFirewallRuleStore store, Action<string>? log, string ruleName = RuleName)
        {
            try
            {
                IReadOnlyList<FirewallRuleRecord> previous = Copy(FindWithRetry(store, ruleName, out bool lookupComplete));
                if (!resolved.BindsAny)
                {
                    int removed = RemoveAll(store, log, ruleName, out _);
                    Write(log, "No inbound firewall rule for localhost mode after removing " +
                               removed.ToString(CultureInfo.InvariantCulture) + ".");
                    return;
                }

                FirewallRuleRecord desired = CreateRule(resolved.Port, ruleName);
                if (lookupComplete && previous.Count == 1 && SameRule(previous[0], desired))
                {
                    Write(log, "Kept inbound firewall rule '" + ruleName + "' for TCP port " +
                               resolved.Port.ToString(CultureInfo.InvariantCulture) + ".");
                    return;
                }

                Write(log, previous.Count == 0
                    ? "Inbound firewall rule '" + ruleName + "' is missing; creating it."
                    : "Inbound firewall rule '" + ruleName + "' does not match (" +
                      previous.Count.ToString(CultureInfo.InvariantCulture) + " found); replacing it.");
                try
                {
                    int removed = RemoveAll(store, log, ruleName, out bool complete);
                    if (!complete)
                    {
                        Write(log, "Could not remove all " + previous.Count.ToString(CultureInfo.InvariantCulture) +
                                   " existing rules named '" + ruleName + "'; not adding a replacement and leaving the " +
                                   "remaining rules in place. Repair the installation again to continue the cleanup.");
                        return;
                    }

                    store.Add(desired);
                    VerifyAdded(store, desired, ruleName);
                    Write(log, "Added inbound firewall rule '" + ruleName + "' for TCP port " +
                               resolved.Port.ToString(CultureInfo.InvariantCulture) +
                               " after removing " + removed.ToString(CultureInfo.InvariantCulture) + ".");
                }
                catch (Exception ex)
                {
                    Write(log, "Could not replace the Windows Firewall rule '" + ruleName + "': " +
                               FormatError(ex) + ". Restoring the previous rule.");
                    Restore(previous, store, log, ruleName);
                }
            }
            catch (Exception ex)
            {
                Write(log, "Could not update the Windows Firewall rule '" + ruleName + "': " + FormatError(ex));
            }
        }

        /// <summary>
        ///     Looks the rules up, retrying a lookup that could not enumerate every rule. <paramref name="complete" /> is
        ///     false when a store that reports lookup diagnostics still returned a partial result.
        /// </summary>
        private static IReadOnlyList<FirewallRuleRecord> FindWithRetry(IFirewallRuleStore store, string ruleName, out bool complete)
        {
            IReadOnlyList<FirewallRuleRecord> found = store.FindByName(ruleName);
            IFirewallLookupDiagnostics? diagnostics = store as IFirewallLookupDiagnostics;
            for (int attempt = 1; diagnostics != null && !diagnostics.LastLookupWasComplete && attempt < 3; attempt++)
            {
                found = store.FindByName(ruleName);
            }

            complete = diagnostics == null || diagnostics.LastLookupWasComplete;
            return found;
        }

        /// <summary>
        ///     Reads the rule back after <c>Add</c>. Throws unless exactly one enabled inbound allow rule for the TCP port
        ///     exists, so a silently dropped or mangled rule is never reported as added.
        /// </summary>
        private static void VerifyAdded(IFirewallRuleStore store, FirewallRuleRecord desired, string ruleName)
        {
            IReadOnlyList<FirewallRuleRecord> after = FindWithRetry(store, ruleName, out bool complete);
            if (!complete)
            {
                throw new InvalidOperationException(
                    "Could not enumerate the rules named '" + ruleName + "' to confirm that exactly one exists after adding it.");
            }

            if (after == null || after.Count != 1)
            {
                throw new InvalidOperationException(
                    "Expected one rule named '" + ruleName + "' after adding it, found " +
                    (after == null ? 0 : after.Count).ToString(CultureInfo.InvariantCulture) + ".");
            }

            FirewallRuleRecord actual = after[0];
            if (!actual.Enabled || actual.Direction != desired.Direction || actual.Action != desired.Action ||
                actual.Protocol != desired.Protocol ||
                !string.Equals(actual.LocalPorts, desired.LocalPorts, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The rule read back after adding '" + ruleName + "' is not the requested inbound allow rule.");
            }

            if (!SameRule(actual, desired))
            {
                throw new InvalidOperationException(
                    "The rule read back after adding '" + ruleName + "' differs from the requested one in its scope (profiles, addresses, interfaces, program, service, remote ports or edge traversal).");
            }
        }

        /// <summary>Replaces every rule named <paramref name="ruleName" /> with <paramref name="previous" />.</summary>
        public static void Restore(IReadOnlyList<FirewallRuleRecord> previous, IFirewallRuleStore store, Action<string>? log, string ruleName = RuleName)
        {
            try
            {
                RemoveAll(store, log, ruleName, out bool complete);
                if (!complete)
                {
                    Write(log, "Could not restore the Windows Firewall rule '" + ruleName +
                               "': matching rules remain after cleanup, so the previous rules were not replayed over them.");
                    return;
                }

                int restored = 0;
                if (previous != null)
                {
                    foreach (FirewallRuleRecord rule in previous)
                    {
                        store.Add(rule);
                        restored++;
                    }
                }

                Write(log, "Restored " + restored.ToString(CultureInfo.InvariantCulture) +
                           " firewall rule(s) named '" + ruleName + "'.");
            }
            catch (Exception ex)
            {
                Write(log, "Could not restore the Windows Firewall rule '" + ruleName + "': " + FormatError(ex));
            }
        }

        public static string Serialize(IReadOnlyList<FirewallRuleRecord> rules)
        {
            StringBuilder text = new StringBuilder();
            if (rules == null)
            {
                return string.Empty;
            }

            foreach (FirewallRuleRecord rule in rules)
            {
                if (text.Length > 0)
                {
                    text.Append('\n');
                }

                text.Append(Escape(rule.Name)).Append('\t');
                text.Append(Escape(rule.Description)).Append('\t');
                text.Append(rule.Protocol.ToString(CultureInfo.InvariantCulture)).Append('\t');
                text.Append(Escape(rule.LocalPorts)).Append('\t');
                text.Append(Escape(rule.RemoteAddresses)).Append('\t');
                text.Append(rule.Direction.ToString(CultureInfo.InvariantCulture)).Append('\t');
                text.Append(rule.Action.ToString(CultureInfo.InvariantCulture)).Append('\t');
                text.Append(rule.Profiles.ToString(CultureInfo.InvariantCulture)).Append('\t');
                text.Append(rule.Enabled ? "1" : "0").Append('\t');
                text.Append(Escape(rule.InterfaceTypes)).Append('\t');
                text.Append(Escape(rule.LocalAddresses)).Append('\t');
                text.Append(Escape(rule.ApplicationName)).Append('\t');
                text.Append(Escape(rule.ServiceName)).Append('\t');
                text.Append(Escape(rule.RemotePorts)).Append('\t');
                text.Append(rule.EdgeTraversal ? "1" : "0");
            }

            return text.ToString();
        }

        public static IReadOnlyList<FirewallRuleRecord> Deserialize(string text)
        {
            List<FirewallRuleRecord> rules = new List<FirewallRuleRecord>();
            if (string.IsNullOrEmpty(text))
            {
                return rules;
            }

            string[] lines = text.Split('\n');
            foreach (string line in lines)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                string[] fields = line.Split('\t');
                if (fields.Length != 15)
                {
                    throw new FormatException("Firewall rollback snapshot is invalid.");
                }

                rules.Add(new FirewallRuleRecord(
                    Unescape(fields[0]),
                    Unescape(fields[1]),
                    int.Parse(fields[2], CultureInfo.InvariantCulture),
                    Unescape(fields[3]),
                    Unescape(fields[4]),
                    int.Parse(fields[5], CultureInfo.InvariantCulture),
                    int.Parse(fields[6], CultureInfo.InvariantCulture),
                    int.Parse(fields[7], CultureInfo.InvariantCulture),
                    fields[8] == "1",
                    Unescape(fields[9]),
                    Unescape(fields[10]),
                    Unescape(fields[11]),
                    Unescape(fields[12]),
                    Unescape(fields[13]),
                    fields[14] == "1"));
            }

            return rules;
        }

        /// <summary>Removes every rule named <paramref name="ruleName" />. Used on uninstall.</summary>
        public static void Remove(IFirewallRuleStore store, Action<string>? log, string ruleName = RuleName)
        {
            try
            {
                int removed = RemoveAll(store, log, ruleName, out _);
                Write(log, "Removed " + removed.ToString(CultureInfo.InvariantCulture) + " firewall rule(s) named '" + ruleName + "'.");
            }
            catch (Exception ex)
            {
                Write(log, "Could not remove the Windows Firewall rule '" + ruleName + "': " + FormatError(ex));
            }
        }

        /// <summary>
        ///     Removes up to <see cref="MaxRemovals" /> rules named <paramref name="ruleName" />. <paramref name="complete" />
        ///     is true only when a final lookup proves that no matching rule remains. When a rule cannot be read back,
        ///     removal falls back to deleting by name only, so a replacement that fails read-back can still be cleaned up.
        /// </summary>
        private static int RemoveAll(IFirewallRuleStore store, Action<string>? log, string ruleName, out bool complete)
        {
            int removed = 0;
            bool blind = false;
            Exception? failure = null;
            try
            {
                for (int i = 0; i < MaxRemovals; i++)
                {
                    IReadOnlyList<FirewallRuleRecord> found = store.FindByName(ruleName);
                    if (found == null || found.Count == 0)
                    {
                        break;
                    }

                    store.Remove(ruleName);
                    removed++;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                blind = true;
                Write(log, "Could not read or remove the rules named '" + ruleName + "' (" + FormatError(ex) +
                           "); removing them by name only.");
            }

            if (blind)
            {
                for (int i = removed; i < MaxRemovals; i++)
                {
                    try
                    {
                        store.Remove(ruleName);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        if (removed == 0)
                        {
                            // Nothing could be deleted at all: the store is down, not empty.
                            throw;
                        }

                        // The end of the loop (no rule left) or a real failure; the final lookup decides which.
                        failure = ex;
                        break;
                    }
                }
            }

            complete = false;
            try
            {
                IReadOnlyList<FirewallRuleRecord> leftover = store.FindByName(ruleName);
                complete = leftover == null || leftover.Count == 0;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (!complete)
            {
                Write(log, "Could not confirm that every firewall rule named '" + ruleName + "' was removed after " +
                           removed.ToString(CultureInfo.InvariantCulture) + " removal(s)" +
                           (failure == null ? "." : " (" + FormatError(failure) + ")."));
            }

            return removed;
        }

        private static void Write(Action<string>? log, string message)
        {
            if (log != null)
            {
                log(message);
            }
        }

        private static IReadOnlyList<FirewallRuleRecord> Copy(IReadOnlyList<FirewallRuleRecord> rules)
        {
            List<FirewallRuleRecord> copy = new List<FirewallRuleRecord>();
            if (rules == null)
            {
                return copy;
            }

            foreach (FirewallRuleRecord rule in rules)
            {
                copy.Add(rule);
            }

            return copy;
        }

        /// <summary>
        ///     True when <paramref name="left" /> is equivalent to <paramref name="right" />. Windows reports unset scope
        ///     fields as <c>*</c> or an empty string interchangeably, and all three profiles as 7 or 0x7FFFFFFF; those do not
        ///     count as drift. A service restriction (<c>*</c> means services only) and every other limit do.
        /// </summary>
        public static bool SameRule(FirewallRuleRecord left, FirewallRuleRecord right)
        {
            return string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
                   string.Equals(left.Description, right.Description, StringComparison.Ordinal) &&
                   left.Protocol == right.Protocol &&
                   string.Equals(left.LocalPorts, right.LocalPorts, StringComparison.Ordinal) &&
                   SameWildcard(left.RemoteAddresses, right.RemoteAddresses, RemoteAny) &&
                   left.Direction == right.Direction &&
                   left.Action == right.Action &&
                   NormalizeProfiles(left.Profiles) == NormalizeProfiles(right.Profiles) &&
                   left.Enabled == right.Enabled &&
                   SameWildcard(left.InterfaceTypes, right.InterfaceTypes, InterfaceTypesAll) &&
                   SameWildcard(left.LocalAddresses, right.LocalAddresses, LocalAddressesAny) &&
                   string.Equals(left.ApplicationName ?? string.Empty, right.ApplicationName ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(left.ServiceName ?? string.Empty, right.ServiceName ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                   SameWildcard(left.RemotePorts, right.RemotePorts, RemotePortsAny) &&
                   left.EdgeTraversal == right.EdgeTraversal;
        }

        private static bool SameWildcard(string? left, string? right, string any)
        {
            return string.Equals(NormalizeWildcard(left, any), NormalizeWildcard(right, any), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeWildcard(string? value, string any)
        {
            string trimmed = (value ?? string.Empty).Trim();
            return trimmed.Length == 0 ? any : trimmed;
        }

        private static int NormalizeProfiles(int profiles)
        {
            return (profiles & 7) == 7 ? ProfilesAll : profiles;
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static string Unescape(string value)
        {
            StringBuilder text = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '\\' && i + 1 < value.Length)
                {
                    char next = value[++i];
                    if (next == 't')
                    {
                        text.Append('\t');
                    }
                    else if (next == 'r')
                    {
                        text.Append('\r');
                    }
                    else if (next == 'n')
                    {
                        text.Append('\n');
                    }
                    else
                    {
                        text.Append(next);
                    }
                }
                else
                {
                    text.Append(value[i]);
                }
            }

            return text.ToString();
        }
    }
}
