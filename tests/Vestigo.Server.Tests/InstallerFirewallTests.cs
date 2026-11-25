using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;

using Vestigo.Installer.Actions.Logic;

namespace Vestigo.Server.Tests;

/// <summary>
///     Install, upgrade and repair must detect a missing or drifted inbound firewall rule and recreate it. These tests
///     cover the reconcile logic with fakes and the real Windows Firewall COM binding with a throwaway rule object.
/// </summary>
public class InstallerFirewallTests
{
    private const string TestRulePrefix = "Vestigo-test-fw-";

    [Fact]
    public void Sync_CreatesAMissingRuleOnFreshInstallAndUpgrade()
    {
        ResolvedSettings lan = ListenSettings.Resolve("any", "none", "6000", null, null);
        MemoryFirewallRuleStore store = new();
        List<string> log = [];

        FirewallRuleSync.Sync(lan, store, log.Add);

        FirewallRuleRecord rule = Assert.Single(store.Rules);
        Assert.Equal("6000", rule.LocalPorts);
        Assert.Equal(FirewallRuleSync.RemotePortsAny, rule.RemotePorts);
        Assert.Contains(log, line => line.Contains("is missing", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("Added inbound firewall rule", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("port")]
    [InlineData("profiles")]
    [InlineData("service")]
    [InlineData("program")]
    [InlineData("remoteports")]
    [InlineData("wireless")]
    [InlineData("edge")]
    [InlineData("localaddress")]
    public void Sync_ReplacesADriftedRule(string drift)
    {
        ResolvedSettings lan = ListenSettings.Resolve("any", "none", "6000", null, null);
        FirewallRuleRecord good = FirewallRuleSync.CreateRule(6000);
        FirewallRuleRecord drifted = drift switch
        {
            "disabled" => Copy(good, enabled: false),
            "port" => Copy(good, localPorts: "5098"),
            "profiles" => Copy(good, profiles: 1),
            "service" => Copy(good, serviceName: "*"),
            "program" => Copy(good, applicationName: "C:\\Windows\\System32\\other.exe"),
            "remoteports" => Copy(good, remotePorts: "1024-65535"),
            "wireless" => Copy(good, interfaceTypes: "Wireless"),
            "edge" => Copy(good, edgeTraversal: true),
            _ => Copy(good, localAddresses: "192.168.1.5")
        };
        MemoryFirewallRuleStore store = new();
        store.Add(drifted);
        List<string> log = [];

        FirewallRuleSync.Sync(lan, store, log.Add);

        FirewallRuleRecord rule = Assert.Single(store.Rules);
        Assert.True(FirewallRuleSync.SameRule(rule, good), drift);
        Assert.Contains(log, line => line.Contains("does not match", StringComparison.Ordinal));
    }

    [Fact]
    public void SameRule_IgnoresWindowsWildcardSpellingButNotRealLimits()
    {
        FirewallRuleRecord good = FirewallRuleSync.CreateRule(6000);
        FirewallRuleRecord windows = Copy(
            good,
            remotePorts: string.Empty,
            interfaceTypes: string.Empty,
            localAddresses: "*",
            remoteAddresses: string.Empty,
            profiles: 7);

        Assert.True(FirewallRuleSync.SameRule(windows, good));
        Assert.False(FirewallRuleSync.SameRule(Copy(good, serviceName: "*"), good));
        Assert.False(FirewallRuleSync.SameRule(Copy(good, remotePorts: "443"), good));
        Assert.False(FirewallRuleSync.SameRule(Copy(good, remoteAddresses: "10.0.0.1"), good));
        Assert.False(FirewallRuleSync.SameRule(Copy(good, profiles: 3), good));
    }

    [Fact]
    public void Sync_DoesNotReportAnAddThatWindowsDroppedAndRestoresThePreviousRule()
    {
        ResolvedSettings lan = ListenSettings.Resolve("any", "none", "6000", null, null);
        FakeStore store = new() { DropNextAdd = true };
        store.Rules.Add(FirewallRuleSync.CreateRule(5098));
        List<string> log = [];

        FirewallRuleSync.Sync(lan, store, log.Add);

        Assert.DoesNotContain(log, line => line.StartsWith("Added inbound firewall rule", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("Expected one rule", StringComparison.Ordinal));
        FirewallRuleRecord restored = Assert.Single(store.Rules);
        Assert.Equal("5098", restored.LocalPorts);
    }

    [Fact]
    public void Sync_RestoresThePreviousRuleWhenTheReplacementCannotBeReadBack()
    {
        ResolvedSettings lan = ListenSettings.Resolve("any", "none", "6000", null, null);
        FakeStore store = new() { UnreadablePort = "6000" };
        store.Rules.Add(FirewallRuleSync.CreateRule(5098));
        List<string> log = [];

        FirewallRuleSync.Sync(lan, store, log.Add);

        Assert.DoesNotContain(log, line => line.StartsWith("Added inbound firewall rule", StringComparison.Ordinal));
        Assert.Contains(log, line => line.StartsWith("Restored 1", StringComparison.Ordinal));
        FirewallRuleRecord restored = Assert.Single(store.Rules);
        Assert.Equal("5098", restored.LocalPorts);
    }

    [Fact]
    public void Sync_RejectsAReplacementWhoseScopeWasNarrowed()
    {
        ResolvedSettings lan = ListenSettings.Resolve("any", "none", "6000", null, null);
        NarrowingStore store = new();
        store.Add(FirewallRuleSync.CreateRule(5098));
        List<string> log = [];

        FirewallRuleSync.Sync(lan, store, log.Add);

        Assert.DoesNotContain(log, line => line.StartsWith("Added inbound firewall rule", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("differs from the requested one", StringComparison.Ordinal));
        FirewallRuleRecord restored = Assert.Single(store.Rules);
        Assert.Equal("5098", restored.LocalPorts);
    }

    [Fact]
    public void Sync_DoesNotAddOrReplayWhenCleanupHitsTheRemovalCap()
    {
        ResolvedSettings lan = ListenSettings.Resolve("any", "none", "6000", null, null);
        FakeStore store = new();
        for (int i = 0; i < FirewallRuleSync.MaxRemovals + 2; i++)
        {
            store.Rules.Add(FirewallRuleSync.CreateRule(5098));
        }

        List<string> log = [];

        FirewallRuleSync.Sync(lan, store, log.Add);

        Assert.Equal(2, store.Rules.Count);
        Assert.All(store.Rules, rule => Assert.Equal("5098", rule.LocalPorts));
        Assert.Contains(log, line => line.Contains("not adding a replacement", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.StartsWith("Restored", StringComparison.Ordinal));

        FirewallRuleSync.Sync(lan, store, log.Add);
        FirewallRuleRecord converged = Assert.Single(store.Rules);
        Assert.Equal("6000", converged.LocalPorts);
    }

    [Fact]
    public void Sync_LogsTheInnerComErrorInsteadOfTheReflectionWrapper()
    {
        ResolvedSettings lan = ListenSettings.Resolve("any", "none", "6000", null, null);
        FakeStore store = new()
        {
            AddFailure = new TargetInvocationException(new COMException("The parameter is incorrect.", unchecked((int)0x80070057)))
        };
        List<string> log = [];

        FirewallRuleSync.Sync(lan, store, log.Add);

        string failure = Assert.Single(log, line => line.Contains("Could not replace", StringComparison.Ordinal));
        Assert.Contains("COMException", failure, StringComparison.Ordinal);
        Assert.Contains("0x80070057", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("target of an invocation", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatError_UnwrapsReflectionAndKeepsTheHresult()
    {
        Exception wrapped = new InvalidOperationException(
            "Could not set firewall rule property 'RemotePorts'",
            new COMException("bad", unchecked((int)0x80070057)));

        string text = FirewallRuleSync.FormatError(wrapped);

        Assert.Contains("RemotePorts", text, StringComparison.Ordinal);
        Assert.Contains("0x80070057", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateRule_UsesOnlyValuesWindowsFirewallAccepts()
    {
        FirewallRuleRecord rule = FirewallRuleSync.CreateRule(5098, "custom");

        Assert.Equal("custom", rule.Name);
        Assert.Equal("*", rule.RemotePorts);
        Assert.True(FirewallRuleSync.ProtocolSupportsPorts(rule.Protocol));
        Assert.True(FirewallRuleSync.ProtocolSupportsPorts(FirewallRuleSync.ProtocolUdp));
        Assert.False(FirewallRuleSync.ProtocolSupportsPorts(256));
    }

    [Fact]
    public void InstallerSource_UsesTheSharedWindowsFirewallStore()
    {
        string root = RepositoryRoot();
        string actions = File.ReadAllText(Path.Combine(root, "installer", "Vestigo.Installer.Actions", "CustomActions.cs"));

        Assert.DoesNotContain("class WindowsFirewallRuleStore", actions, StringComparison.Ordinal);
        Assert.Contains("new WindowsFirewallRuleStore()", actions, StringComparison.Ordinal);
        Assert.Contains("FirewallRuleSync.FormatError", actions, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void ComRule_RoundTripsTheUnrestrictedRuleThroughRealWindowsFirewallObjects()
    {
        FirewallRuleRecord desired = FirewallRuleSync.CreateRule(5098, TestRulePrefix + Guid.NewGuid().ToString("N"));

        FirewallRuleRecord read = RoundTrip(desired);

        Assert.True(FirewallRuleSync.SameRule(read, desired));
        Assert.Equal("5098", read.LocalPorts);
        Assert.True(read.Enabled);
    }

    [WindowsFact]
    public void ComRule_RoundTripsRestrictedAndPortlessRules()
    {
        string name = TestRulePrefix + Guid.NewGuid().ToString("N");
        FirewallRuleRecord restricted = Copy(
            FirewallRuleSync.CreateRule(6000, name),
            interfaceTypes: "Wireless",
            applicationName: "C:\\Windows\\System32\\svchost.exe",
            edgeTraversal: true);
        FirewallRuleRecord anyProtocol = new(
            name,
            "portless",
            256,
            "*",
            "*",
            FirewallRuleSync.DirectionInbound,
            FirewallRuleSync.ActionAllow,
            FirewallRuleSync.ProfilesAll,
            false,
            "All",
            "*",
            string.Empty,
            string.Empty,
            "*",
            false);

        FirewallRuleRecord readRestricted = RoundTrip(restricted);
        FirewallRuleRecord readAny = RoundTrip(anyProtocol);

        Assert.Equal("Wireless", readRestricted.InterfaceTypes);
        Assert.Equal("C:\\Windows\\System32\\svchost.exe", readRestricted.ApplicationName, ignoreCase: true);
        Assert.True(readRestricted.EdgeTraversal);
        Assert.False(FirewallRuleSync.SameRule(readRestricted, FirewallRuleSync.CreateRule(6000, name)));
        Assert.Equal(256, readAny.Protocol);
        Assert.False(readAny.Enabled);
    }

    [WindowsElevatedFact]
    [Trait("Category", "WindowsElevated")]
    public void RealFirewall_CreatesKeepsReplacesAndRemovesTheRule()
    {
        string name = TestRulePrefix + Guid.NewGuid().ToString("N");
        ResolvedSettings lan = ListenSettings.Resolve("any", "none", "5098", null, null);
        ResolvedSettings localhost = ListenSettings.Resolve("localhost", "none", "5098", null, null);
        List<string> log = [];
        WindowsFirewallRuleStore store = new();
        bool cleanupFailed = false;
        try
        {
            FirewallRuleSync.Sync(lan, store, log.Add, name);
            FirewallRuleRecord created = Assert.Single(store.FindByName(name));
            Assert.True(FirewallRuleSync.SameRule(created, FirewallRuleSync.CreateRule(5098, name)));
            Assert.Contains(log, line => line.Contains("Added inbound firewall rule", StringComparison.Ordinal));

            log.Clear();
            FirewallRuleSync.Sync(lan, store, log.Add, name);
            Assert.Contains(log, line => line.Contains("Kept inbound firewall rule", StringComparison.Ordinal));

            store.Remove(name);
            store.Add(Copy(FirewallRuleSync.CreateRule(5098, name), interfaceTypes: "Wireless"));
            log.Clear();
            FirewallRuleSync.Sync(lan, store, log.Add, name);
            FirewallRuleRecord replaced = Assert.Single(store.FindByName(name));
            Assert.True(FirewallRuleSync.SameRule(replaced, FirewallRuleSync.CreateRule(5098, name)));

            IReadOnlyList<FirewallRuleRecord> snapshot = FirewallRuleSync.Deserialize(
                FirewallRuleSync.Serialize(store.FindByName(name)));
            FirewallRuleSync.Remove(store, log.Add, name);
            Assert.Empty(store.FindByName(name));
            FirewallRuleSync.Restore(snapshot, store, log.Add, name);
            Assert.Single(store.FindByName(name));

            FirewallRuleSync.Sync(localhost, store, log.Add, name);
            Assert.Empty(store.FindByName(name));
        }
        finally
        {
            try
            {
                List<string> cleanup = [];
                FirewallRuleSync.Remove(store, cleanup.Add, name);
                if (store.FindByName(name).Count > 0)
                {
                    // Reported without masking an earlier assertion failure from the try block.
                    Console.Error.WriteLine("Could not remove the temporary firewall rule '" + name + "': " + string.Join(" ", cleanup));
                    cleanupFailed = true;
                }
            }
            finally
            {
                store.Dispose();
            }
        }

        Assert.False(cleanupFailed, "The temporary firewall rule '" + name + "' was not removed.");
    }

    private static FirewallRuleRecord RoundTrip(FirewallRuleRecord rule)
    {
        Type? type = Type.GetTypeFromProgID("HNetCfg.FWRule");
        Assert.NotNull(type);
        object com = Activator.CreateInstance(type)!;
        try
        {
            WindowsFirewallRuleStore.PopulateComRule(com, rule);
            return WindowsFirewallRuleStore.ReadComRule(com);
        }
        finally
        {
            Marshal.FinalReleaseComObject(com);
        }
    }

    private static FirewallRuleRecord Copy(
        FirewallRuleRecord source,
        string? localPorts = null,
        string? remoteAddresses = null,
        int? profiles = null,
        bool? enabled = null,
        string? interfaceTypes = null,
        string? localAddresses = null,
        string? applicationName = null,
        string? serviceName = null,
        string? remotePorts = null,
        bool? edgeTraversal = null) =>
        new(
            source.Name,
            source.Description,
            source.Protocol,
            localPorts ?? source.LocalPorts,
            remoteAddresses ?? source.RemoteAddresses,
            source.Direction,
            source.Action,
            profiles ?? source.Profiles,
            enabled ?? source.Enabled,
            interfaceTypes ?? source.InterfaceTypes,
            localAddresses ?? source.LocalAddresses,
            applicationName ?? source.ApplicationName,
            serviceName ?? source.ServiceName,
            remotePorts ?? source.RemotePorts,
            edgeTraversal ?? source.EdgeTraversal);

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

    private sealed class FakeStore : IFirewallRuleStore
    {
        public List<FirewallRuleRecord> Rules { get; } = [];

        public bool DropNextAdd { get; set; }

        public Exception? AddFailure { get; set; }

        /// <summary>When set, reading rules throws while a rule with this local port exists (a replacement that fails read-back).</summary>
        public string? UnreadablePort { get; set; }

        public IReadOnlyList<FirewallRuleRecord> FindByName(string name)
        {
            if (UnreadablePort is not null && Rules.Any(rule => rule.Name == name && rule.LocalPorts == UnreadablePort))
            {
                throw new InvalidOperationException("Could not read firewall rule property 'LocalPorts'.");
            }

            return Rules.Where(rule => rule.Name == name).ToList();
        }

        public void Remove(string name)
        {
            int index = Rules.FindIndex(rule => rule.Name == name);
            if (index < 0)
            {
                throw new InvalidOperationException("No firewall rule named '" + name + "'.");
            }

            Rules.RemoveAt(index);
        }

        public void Add(FirewallRuleRecord rule)
        {
            if (AddFailure is not null)
            {
                throw AddFailure;
            }

            if (DropNextAdd)
            {
                DropNextAdd = false;
                return;
            }

            Rules.Add(rule);
        }
    }

    private sealed class NarrowingStore : IFirewallRuleStore
    {
        public List<FirewallRuleRecord> Rules { get; } = [];

        public IReadOnlyList<FirewallRuleRecord> FindByName(string name) =>
            Rules.Where(rule => rule.Name == name).ToList();

        public void Remove(string name)
        {
            int index = Rules.FindIndex(rule => rule.Name == name);
            if (index >= 0)
            {
                Rules.RemoveAt(index);
            }
        }

        public void Add(FirewallRuleRecord rule)
        {
            // Simulates Windows keeping only the Domain profile for the new rule.
            Rules.Add(rule.LocalPorts == "6000"
                ? new FirewallRuleRecord(
                    rule.Name, rule.Description, rule.Protocol, rule.LocalPorts, rule.RemoteAddresses, rule.Direction,
                    rule.Action, 1, rule.Enabled, rule.InterfaceTypes, rule.LocalAddresses, rule.ApplicationName,
                    rule.ServiceName, rule.RemotePorts, rule.EdgeTraversal)
                : rule);
        }
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Requires Windows Firewall COM objects.";
            }
        }
    }

    private sealed class WindowsElevatedFactAttribute : FactAttribute
    {
        public WindowsElevatedFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Requires an elevated Windows session.";
                return;
            }

            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Skip = "Requires an elevated Windows session.";
            }
        }
    }
}
