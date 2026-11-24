#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

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
            if (Fail)
            {
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

        /// <summary>
        ///     Removes every rule named <see cref="RuleName" />, then adds one inbound TCP rule when the server binds beyond loopback.
        /// </summary>
        public static void Sync(ResolvedSettings resolved, IFirewallRuleStore store, Action<string>? log)
        {
            try
            {
                int removed = RemoveAll(store, log);
                if (resolved.BindsAny)
                {
                    store.Add(CreateRule(resolved.Port));
                    Write(log, "Added inbound firewall rule '" + RuleName + "' for TCP port " +
                               resolved.Port.ToString(CultureInfo.InvariantCulture) +
                               " after removing " + removed.ToString(CultureInfo.InvariantCulture) + ".");
                }
                else
                {
                    Write(log, "No inbound firewall rule for localhost mode after removing " +
                               removed.ToString(CultureInfo.InvariantCulture) + ".");
                }
            }
            catch (Exception ex)
            {
                Write(log, "Could not update the Windows Firewall rule '" + RuleName + "': " + ex.GetType().Name + ": " + ex.Message);
            }
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
    }
}
