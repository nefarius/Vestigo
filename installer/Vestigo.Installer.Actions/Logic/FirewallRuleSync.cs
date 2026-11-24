#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
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
            bool enabled)
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
        public const int DirectionInbound = 1;
        public const int ActionAllow = 1;
        public const int ProfilesAll = 0x7FFFFFFF;
        public const string RemoteAny = "*";
        public const int MaxRemovals = 32;

        public static FirewallRuleRecord CreateRule(int port)
        {
            return new FirewallRuleRecord(
                RuleName,
                RuleDescription,
                ProtocolTcp,
                port.ToString(CultureInfo.InvariantCulture),
                RemoteAny,
                DirectionInbound,
                ActionAllow,
                ProfilesAll,
                true);
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
        public static void Sync(ResolvedSettings resolved, IFirewallRuleStore store, Action<string>? log)
        {
            try
            {
                IReadOnlyList<FirewallRuleRecord> previous = Copy(store.FindByName(RuleName));
                if (!resolved.BindsAny)
                {
                    int removed = RemoveAll(store, log);
                    Write(log, "No inbound firewall rule for localhost mode after removing " +
                               removed.ToString(CultureInfo.InvariantCulture) + ".");
                    return;
                }

                FirewallRuleRecord desired = CreateRule(resolved.Port);
                if (previous.Count == 1 && SameRule(previous[0], desired))
                {
                    Write(log, "Kept inbound firewall rule '" + RuleName + "' for TCP port " +
                               resolved.Port.ToString(CultureInfo.InvariantCulture) + ".");
                    return;
                }

                try
                {
                    int removed = RemoveAll(store, log);
                    store.Add(desired);
                    Write(log, "Added inbound firewall rule '" + RuleName + "' for TCP port " +
                               resolved.Port.ToString(CultureInfo.InvariantCulture) +
                               " after removing " + removed.ToString(CultureInfo.InvariantCulture) + ".");
                }
                catch (Exception ex)
                {
                    Write(log, "Could not replace the Windows Firewall rule '" + RuleName + "': " +
                               ex.GetType().Name + ": " + ex.Message + ". Restoring the previous rule.");
                    Restore(previous, store, log);
                }
            }
            catch (Exception ex)
            {
                Write(log, "Could not update the Windows Firewall rule '" + RuleName + "': " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Replaces every rule named <see cref="RuleName" /> with <paramref name="previous" />.</summary>
        public static void Restore(IReadOnlyList<FirewallRuleRecord> previous, IFirewallRuleStore store, Action<string>? log)
        {
            try
            {
                RemoveAll(store, log);
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
                           " firewall rule(s) named '" + RuleName + "'.");
            }
            catch (Exception ex)
            {
                Write(log, "Could not restore the Windows Firewall rule '" + RuleName + "': " + ex.GetType().Name + ": " + ex.Message);
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
                text.Append(rule.Enabled ? "1" : "0");
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
                if (fields.Length != 9)
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
                    fields[8] == "1"));
            }

            return rules;
        }

        /// <summary>Removes every rule named <see cref="RuleName" />. Used on uninstall.</summary>
        public static void Remove(IFirewallRuleStore store, Action<string>? log)
        {
            try
            {
                int removed = RemoveAll(store, log);
                Write(log, "Removed " + removed.ToString(CultureInfo.InvariantCulture) + " firewall rule(s) named '" + RuleName + "'.");
            }
            catch (Exception ex)
            {
                Write(log, "Could not remove the Windows Firewall rule '" + RuleName + "': " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static int RemoveAll(IFirewallRuleStore store, Action<string>? log)
        {
            int removed = 0;
            for (int i = 0; i < MaxRemovals; i++)
            {
                IReadOnlyList<FirewallRuleRecord> found = store.FindByName(RuleName);
                if (found == null || found.Count == 0)
                {
                    break;
                }

                store.Remove(RuleName);
                removed++;
            }

            IReadOnlyList<FirewallRuleRecord> leftover = store.FindByName(RuleName);
            if (removed == MaxRemovals && leftover != null && leftover.Count > 0)
            {
                Write(log, "Stopped after removing " + MaxRemovals.ToString(CultureInfo.InvariantCulture) +
                           " firewall rules named '" + RuleName + "'.");
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

        private static bool SameRule(FirewallRuleRecord left, FirewallRuleRecord right)
        {
            return string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
                   string.Equals(left.Description, right.Description, StringComparison.Ordinal) &&
                   left.Protocol == right.Protocol &&
                   string.Equals(left.LocalPorts, right.LocalPorts, StringComparison.Ordinal) &&
                   string.Equals(left.RemoteAddresses, right.RemoteAddresses, StringComparison.Ordinal) &&
                   left.Direction == right.Direction &&
                   left.Action == right.Action &&
                   left.Profiles == right.Profiles &&
                   left.Enabled == right.Enabled;
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
