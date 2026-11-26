using System.Text.Json.Nodes;

using Bunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using MudBlazor.Services;

using Vestigo.Configuration;
using Vestigo.Diagnostics;
using Vestigo.Hosting;
using Vestigo.Hosting.Components;
using Vestigo.Installer.Actions.Logic;
using Vestigo.Modules;
using Vestigo.Performance;

namespace Vestigo.Server.Tests;

[Collection("VestigoHost")]
public sealed class InDepthNetworkTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("vestigo-network-settings").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("VESTIGO_SETTINGS_DIR");

    public InDepthNetworkTests()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _settings);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VESTIGO_SETTINGS_DIR", _previous);
        try
        {
            Directory.Delete(_settings, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ConnectionProjection_FiltersHistogramAndEphemeralUse()
    {
        ConnectionRecord[] rows =
        [
            new("tcp", "ipv4", "established", "127.0.0.1", 50000, "203.0.113.9", 443, 10),
            new("tcp", "ipv4", "timeWait", "127.0.0.1", 49152, "203.0.113.9", 80, 11),
            new("tcp", "ipv4", "closeWait", "127.0.0.1", 80, "203.0.113.9", 50001, 12),
            new("udp", "ipv6", null, "::", 53, null, null, 13)
        ];

        Assert.True(NetworkConnectionProjection.Matches(rows[0], "tcp", "ipv4", "established", 50000, 443, 10));
        Assert.False(NetworkConnectionProjection.Matches(rows[0], "udp", null, null, null, null, null));
        IReadOnlyList<Diagnostics.TcpStateCount> histogram = NetworkConnectionProjection.Histogram(rows);
        Assert.Equal(3, histogram.Count);
        Assert.Equal(1, NetworkConnectionProjection.UdpCount(rows));
        Performance.EphemeralPortUse ephemeral = NetworkConnectionProjection.Ephemeral(rows);
        Assert.Equal(49152, ephemeral.Start);
        Assert.Equal(2, ephemeral.Used);
        Assert.Equal(16384, ephemeral.Capacity);
    }

    [Fact]
    public void FirewallMatcher_AppliesFiltersBeforeTheCap()
    {
        FirewallRule allow = Rule("Contoso", true, "out", "allow", 2, "C:\\Windows\\System32\\app.exe", "443");
        FirewallRule block = Rule("Blocker", false, "in", "block", 4, "C:\\Tools\\vpn.exe", "80,443");
        Assert.True(FirewallRuleMatcher.Matches(allow, new FirewallRuleFilter(true, "out", "allow", "private", "cont", "app.exe", "443")));
        Assert.False(FirewallRuleMatcher.Matches(block, new FirewallRuleFilter(true, null, null, null, null, null, null)));
        Assert.True(FirewallRuleMatcher.Matches(block, new FirewallRuleFilter(false, "in", "block", "public", "block", "vpn", "80")));
        Assert.False(FirewallRuleMatcher.Matches(allow, new FirewallRuleFilter(false, null, null, "domain", null, null, null)));
    }

    [Fact]
    public void ProbeMath_ComputesJitterLossAndPmtuSearch()
    {
        Assert.Equal(0, NetworkProbeMath.Rfc3550JitterMs([]));
        double jitter = NetworkProbeMath.Rfc3550JitterMs([10, 20, 10]);
        Assert.True(jitter > 0);
        Assert.Equal(50, NetworkProbeMath.LossPercent(4, 2));
        Assert.Equal(1472, NetworkProbeMath.PayloadBytesForMtu(1500));
        Assert.Equal(1500, NetworkProbeMath.MtuForPayload(1472));
        (int low, int high, int mid, bool done) = NetworkProbeMath.NextPmtuSearch(68, 1500, 784, tooBig: false);
        Assert.False(done);
        Assert.Equal(784, low);
        Assert.Equal(1500, high);
        Assert.True(mid > low);
        (low, high, mid, done) = NetworkProbeMath.NextPmtuSearch(784, 1500, 1143, tooBig: true);
        Assert.False(done);
        Assert.Equal(784, low);
        Assert.Equal(1142, high);
        (low, high, mid, done) = NetworkProbeMath.NextPmtuSearch(1400, 1400, 1400, tooBig: false);
        Assert.True(done);
        Assert.Equal("packetTooBig", NetworkProbeMath.StatusName(System.Net.NetworkInformation.IPStatus.PacketTooBig));
        Assert.Equal("ttlExpired", NetworkProbeMath.StatusName(System.Net.NetworkInformation.IPStatus.TtlExpired));
    }

    [Fact]
    public void PktmonPlanner_UsesDropOnlyArgumentsAndParsesCounters()
    {
        IReadOnlyList<string> start = PktmonPlanner.StartArguments(@"C:\state\drop.etl");
        Assert.True(PktmonPlanner.IsDropOnlyStart(start));
        Assert.Equal("drop", start[start.ToList().IndexOf("--type") + 1]);
        Assert.Equal(PktmonPlanner.MetadataFlags, start[start.ToList().IndexOf("--flags") + 1]);
        Assert.DoesNotContain("--drop-only", start);
        Assert.DoesNotContain("--pkt-size", start);
        Assert.False(PktmonPlanner.IsDropOnlyStart(["start", "--capture", "--pkt-size", "0", "--drop-only", "--comp", "all"]));
        Assert.Equal(["stop"], PktmonPlanner.StopArguments());
        Assert.Equal(["status"], PktmonPlanner.StatusArguments());
        Assert.Equal(["counters", "--type", "drop", "--json"], PktmonPlanner.CountersArguments());
        Assert.True(PktmonPlanner.IndicatesIdle("Packet Monitor is not running."));
        Assert.False(PktmonPlanner.IndicatesIdle("pktmon status failed"));
        Assert.True(PktmonPlanner.ConfirmsIdle(true, "Packet Monitor is not running."));
        Assert.False(PktmonPlanner.ConfirmsIdle(false, "Packet Monitor is not running."));
        Assert.True(PktmonPlanner.IndicatesActive("Packet Monitor is running."));
        Assert.False(PktmonPlanner.IndicatesActive("Packet Monitor is not running."));
        Assert.True(PktmonPlanner.IndicatesSessionInUse("Packet Monitor is already running."));
        Assert.False(PktmonPlanner.IndicatesSessionInUse("Failed to open log file."));
        Assert.Equal("disabled", NetworkTopologyPlanner.TimestampPolicy(0, null));
        Assert.Equal("enabled", NetworkTopologyPlanner.TimestampPolicy(1, "1"));
        Assert.Equal("allowed", NetworkTopologyPlanner.TimestampPolicy(2, null));
        Assert.Equal("Enabled", NetworkTopologyPlanner.TimestampPolicy(null, "Enabled"));
        Dictionary<string, string> byGuid = new(StringComparer.OrdinalIgnoreCase) { ["aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"] = "A" };
        Dictionary<string, string> byName = new(StringComparer.OrdinalIgnoreCase) { ["Ethernet"] = "A" };
        Dictionary<string, string> byDescription = new(StringComparer.OrdinalIgnoreCase) { ["Ethernet"] = "B" };
        Assert.Equal(
            "A",
            NetworkTopologyPlanner.MatchAdapter("{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}", "Ethernet", "Intel", byGuid, byName, byDescription));
        Assert.Equal(
            "B",
            NetworkTopologyPlanner.MatchAdapter("missing", "Other", "Ethernet", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), byName, byDescription));

        IReadOnlyList<PacketDropCounter> counters = PktmonPlanner.ParseCounters(
            """
            [
              {"Id":31,"Name":"Windows Filtering Platform","Drop":{"In":{"Packets":4,"Bytes":320,"Reason":"WFP drop"},"Out":{"Packets":0,"Bytes":0}}},
              {"Id":12,"Name":"Intel Ethernet","Drop":{"In":{"Packets":7,"Bytes":518,"Reason":"FILTER_DROP"},"Out":{"Packets":0,"Bytes":0}}}
            ]
            """);
        Assert.Equal(2, counters.Count);
        Assert.Equal(4, counters[0].Dropped);
        Assert.Equal("WFP drop", counters[0].DropReason);

        IReadOnlyList<PacketDropCounter> table = PktmonPlanner.ParseCounters(
            """
            Id Name                        Dir Packets Drop reason
            31 Windows Filtering Platform  Rx  42      WFP drop
            """);
        Assert.Equal("Windows Filtering Platform", Assert.Single(table).Component);
        Assert.Equal(42, table[0].Dropped);

        IReadOnlyList<PacketDropCounter> reasons = PktmonPlanner.ParseDropReasons(
            """
            Packet 1
              DropReason      : INGEST_DISCARD
              Drop: TCP 192.0.2.1:1 > 192.0.2.2:2
            Packet 2
              DropReason      : INGEST_DISCARD
            Packet 3
              DropReason      : FILTER_DROP
            [30]0000.0000::<DateTime> [Microsoft-Windows-PktMon] Drop: PktGroupId 1, PktNumber 1, Appearance 0, Direction Rx , Type IP , Component 49, Filter 1, DropReason INET: transport endpoint was not found , DropLocation 0xE000460A, OriginalSize 402, LoggedSize 148
                   Drop: ip: 192.0.2.1.50005 > 192.0.2.2.50005: UDP, length 374
            """);
        Assert.Contains(reasons, item => item.DropReason == "INGEST_DISCARD" && item.Dropped == 2);
        Assert.Contains(reasons, item => item.DropReason == "FILTER_DROP" && item.Dropped == 1);
        Assert.Contains(reasons, item => item.DropReason == "INET: transport endpoint was not found" && item.Dropped == 1);
        PacketDropResult combined = PktmonPlanner.Combine(15000, counters, reasons, truncated: false, partial: true, "format failed");
        Assert.Equal(11, combined.TotalDropped);
        Assert.True(combined.Partial);
    }

    [Fact]
    public void KernelAggregator_SummarizesNetworkEvents()
    {
        KernelLatencyAggregator aggregator = new();
        aggregator.AddNetwork(42, "curl", 1000, 200, 3, 2, 1, 1, 0);
        aggregator.AddNetwork(42, "curl", 50, 10, 1, 1, 0, 0, 1);
        KernelLatencySummary summary = aggregator.Build("s", 1000, [KernelTraceGroups.Network], null);
        ProcessNetworkSummary row = Assert.Single(summary.Network);
        Assert.Equal(42, row.Pid);
        Assert.Equal("curl", row.Name);
        Assert.Equal(1050, row.BytesSent);
        Assert.Equal(210, row.BytesReceived);
        Assert.Equal(4, row.SendCount);
        Assert.Equal(1, row.Retransmits);
        Assert.Equal(1, row.Failures);
        Assert.Contains(KernelTraceGroups.Network, KernelTraceGroups.All);
        Assert.Contains(@"\Network Interface(*)\Bytes Received/sec", CounterPresets.Paths["network"]);
        Assert.True(CounterPresets.IsKnown("network_rss"));
        Assert.True(KernelTraceEventParserKeywordsContainsNetwork());
    }

    [Fact]
    public void Options_ValidateNewNetworkBounds()
    {
        new VestigoOptions
        {
            Diagnostics =
            {
                MaxNetworkConnections = 500,
                MaxNetworkProbeSamples = 20,
                MaxLatencyProbeDuration = TimeSpan.FromSeconds(15),
                MaxPathProbeHops = 30
            },
            Performance = { MaxPacketDropDuration = TimeSpan.FromSeconds(15) }
        }.Validate();
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { Diagnostics = { MaxNetworkConnections = 0 } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { Diagnostics = { MaxNetworkProbeSamples = 101 } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { Diagnostics = { MaxPathProbeHops = 65 } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { Performance = { MaxPacketDropDuration = TimeSpan.Zero } }.Validate());
    }

    [Fact]
    public void Installer_NestsPacketDropUnderPerformanceAndLeavesItOff()
    {
        Assert.False(ModuleSelection.Defaults()[ModuleSelection.PerformancePacketDropCapture]);
        Assert.Equal("Performance", ModuleSelection.Find(ModuleSelection.PerformancePacketDropCapture)?.Parent);
        Assert.Contains(ModuleSelection.OptionToggles, item => item.Name == ModuleSelection.PerformancePacketDropCapture);
    }

    [Fact]
    public void ModuleWarning_NamesPacketDropAndExpandedNetworkProbes()
    {
        ModuleSettings on = new() { Enabled = true };
        VestigoOptions gated = new()
        {
            Modules = { ["Performance"] = on, ["Jobs"] = on, ["Diagnostics"] = on },
            Performance = { AllowPacketDropCapture = true },
            Diagnostics = { AllowNetworkProbes = true }
        };
        string? warning = ModuleRegistry.GetPrivilegedModuleWarning(gated);
        Assert.Contains("Packet-drop capture", warning);
        Assert.Contains("latency, and path", warning);
        Assert.Contains("NDIS/WFP", warning);
    }

    [Fact]
    public async Task PacketDropTools_StayDisabledUntilTheGateIsOn()
    {
        FakePacketDrop capture = new();
        PacketDropTools tools = new(capture, new VestigoOptions());
        Assert.Equal("packet_drop_disabled", (await tools.CapturePacketDrops()).ErrorCode);
        Assert.Equal(0, capture.Calls);
    }

    [Fact]
    public void LatencyAndPathTools_StayDisabledUntilTheGateIsOn()
    {
        VestigoOptions options = new();
        LatencyProbeTools latency = new(new FakeLatency(), options, NullLogger<LatencyProbeTools>.Instance);
        PathProbeTools path = new(new FakePath(), options, NullLogger<PathProbeTools>.Instance);
        Assert.Equal("network_probes_disabled", latency.ProbeNetworkLatency("example.com").Result.ErrorCode);
        Assert.Equal("network_probes_disabled", path.ProbeNetworkPath("example.com").Result.ErrorCode);
        options.Diagnostics.AllowNetworkProbes = true;
        options.Diagnostics.AllowedProbeDestinations = ["example.com"];
        Assert.True(latency.ProbeNetworkLatency("example.com", protocol: "icmp").Result.Success);
        Assert.True(path.ProbeNetworkPath("example.com").Result.Success);
        Assert.Equal("destination_not_allowed", latency.ProbeNetworkLatency("other.test").Result.ErrorCode);
    }

    [Fact]
    public void Windows_ReadsTopologyFiltersAndConfiguration()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Network topology, filters, configuration, and stack stats require Windows.");

        OperationResult<NetworkTopology> topology = new WindowsNetworkTopology().Read();
        Assert.True(topology.Success, topology.Error);
        Assert.NotEmpty(topology.Value!.Adapters);

        OperationResult<NetworkFilterList> filters = new WindowsNetworkFilterSource().List();
        Assert.True(filters.Success, filters.Error);

        OperationResult<NetworkConfiguration> config = new WindowsNetworkConfiguration().Read(32, 32);
        Assert.True(config.Success, config.Error);
        Assert.NotNull(config.Value!.Dns);

        OperationResult<NetworkStackStats> stats = new WindowsNetworkStackStats().Read();
        Assert.True(stats.Success, stats.Error);
        Assert.NotEmpty(stats.Value!.Interfaces);

        TableRead connections = IpHelperConnections.ReadAll(64);
        Assert.True(connections.Success, connections.Error);
    }

    [Fact]
    public async Task ConfigPage_ExposesAndSavesNetworkDiagnosticBounds()
    {
        await using BunitContext context = new();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        JsonObject values = new()
        {
            ["AllowNetworkProbes"] = false,
            ["MaxNetworkConnections"] = 500,
            ["MaxNetworkProbeSamples"] = 20,
            ["MaxLatencyProbeDuration"] = "00:00:15",
            ["MaxPathProbeHops"] = 30
        };
        IRenderedComponent<ConfigFields> cut = context.Render<ConfigFields>(parameters => parameters
            .Add(field => field.Values, values)
            .Add(field => field.Prefix, "Diagnostics"));
        Assert.Contains("Diagnostics:AllowNetworkProbes", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Diagnostics:MaxNetworkConnections", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Diagnostics:MaxNetworkProbeSamples", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Diagnostics:MaxLatencyProbeDuration", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Diagnostics:MaxPathProbeHops", cut.Markup, StringComparison.Ordinal);

        ConfigurationEditor editor = new(new VestigoOptions(), ModuleRegistry.All);
        ConfigurationSnapshot snapshot = editor.Load();
        snapshot.Values["Diagnostics"]!["MaxNetworkConnections"] = 750;
        snapshot.Values["Diagnostics"]!["MaxNetworkProbeSamples"] = 40;
        snapshot.Values["Diagnostics"]!["MaxLatencyProbeDuration"] = "00:00:20";
        snapshot.Values["Diagnostics"]!["MaxPathProbeHops"] = 40;
        ConfigurationSnapshot saved = editor.Save(snapshot);
        Assert.Equal(750, saved.Values["Diagnostics"]!["MaxNetworkConnections"]!.GetValue<int>());
        Assert.Equal(40, saved.Values["Diagnostics"]!["MaxNetworkProbeSamples"]!.GetValue<int>());
        Assert.Equal("00:00:20", saved.Values["Diagnostics"]!["MaxLatencyProbeDuration"]!.GetValue<string>());
        Assert.Equal(40, saved.Values["Diagnostics"]!["MaxPathProbeHops"]!.GetValue<int>());
    }

    private static bool KernelTraceEventParserKeywordsContainsNetwork() =>
        KernelEtwWorker.KeywordsFor([KernelTraceGroups.Network]).HasFlag(Microsoft.Diagnostics.Tracing.Parsers.KernelTraceEventParser.Keywords.NetworkTCPIP);

    private static FirewallRule Rule(string name, bool enabled, string direction, string action, int profiles, string program, string ports) =>
        new(name, null, program, null, 6, "tcp", ports, "*", "*", "*", null, direction == "in" ? 1 : 2, direction, "All", enabled, null, profiles, false, action == "allow" ? 1 : 0, action, 0, "deny", null, null, null, null, null, 0, false);

    private sealed class FakePacketDrop : IPacketDropCapture
    {
        public int Calls { get; private set; }

        public OperationResult<string> PlanStart(TimeSpan duration)
        {
            Calls++;
            return OperationResult<string>.Fail("packet_drop_disabled", "off");
        }

        public Task<OperationResult<PacketDropResult>> CaptureAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(PlanStart(duration).FailAs<PacketDropResult>());
        }
    }

    private sealed class FakeLatency : INetworkLatencyProbe
    {
        public Task<OperationResult<NetworkLatencyResult>> ProbeAsync(
            string host,
            int port,
            string protocol,
            int samples,
            TimeSpan timeout,
            bool includeResolvedAddress,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult<NetworkLatencyResult>.Ok(new NetworkLatencyResult(
                host,
                protocol,
                port,
                samples,
                samples,
                0,
                PercentileStats.Empty,
                0,
                [],
                false)));
    }

    private sealed class FakePath : INetworkPathProbe
    {
        public Task<OperationResult<NetworkPathResult>> ProbeAsync(
            string host,
            int maxHops,
            int samplesPerHop,
            TimeSpan hopTimeout,
            bool includeHopAddresses,
            bool discoverPmtu,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult<NetworkPathResult>.Ok(new NetworkPathResult(host, [], null, false)));
    }
}
