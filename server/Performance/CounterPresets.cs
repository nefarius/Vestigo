namespace Vestigo.Performance;

/// <summary>English PDH counter paths for curated sampling presets.</summary>
public static class CounterPresets
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Paths = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["overview"] =
        [
            @"\Processor(_Total)\% Processor Time",
            @"\Processor(_Total)\% Privileged Time",
            @"\Processor(_Total)\% DPC Time",
            @"\Processor(_Total)\% Interrupt Time",
            @"\System\Processor Queue Length",
            @"\System\Context Switches/sec",
            @"\Memory\Available MBytes",
            @"\Memory\Page Reads/sec",
            @"\PhysicalDisk(_Total)\Avg. Disk sec/Read",
            @"\PhysicalDisk(_Total)\Avg. Disk sec/Write",
            @"\PhysicalDisk(_Total)\Current Disk Queue Length"
        ],
        ["storage"] =
        [
            @"\PhysicalDisk(*)\Avg. Disk sec/Read",
            @"\PhysicalDisk(*)\Avg. Disk sec/Write",
            @"\PhysicalDisk(*)\Current Disk Queue Length",
            @"\PhysicalDisk(*)\Split IO/Sec",
            @"\PhysicalDisk(*)\Disk Reads/sec",
            @"\PhysicalDisk(*)\Disk Writes/sec",
            @"\PhysicalDisk(*)\Disk Bytes/sec"
        ],
        ["cpu"] =
        [
            @"\Processor(*)\% Processor Time",
            @"\Processor(*)\% Privileged Time",
            @"\Processor(*)\% DPC Time",
            @"\Processor(*)\% Interrupt Time",
            @"\Processor Information(*)\% Processor Performance",
            @"\Processor Information(*)\% of Maximum Frequency",
            @"\System\Processor Queue Length",
            @"\System\Context Switches/sec",
            @"\System\DPCs Queued/sec"
        ],
        ["memory"] =
        [
            @"\Memory\Available MBytes",
            @"\Memory\% Committed Bytes In Use",
            @"\Memory\Page Reads/sec",
            @"\Memory\Pages/sec",
            @"\Memory\Pool Nonpaged Bytes",
            @"\Memory\Pool Paged Bytes",
            @"\Memory\Cache Bytes"
        ],
        ["network"] =
        [
            @"\Network Interface(*)\Bytes Total/sec",
            @"\Network Interface(*)\Bytes Received/sec",
            @"\Network Interface(*)\Bytes Sent/sec",
            @"\Network Interface(*)\Current Bandwidth",
            @"\Network Interface(*)\Packets/sec",
            @"\Network Interface(*)\Output Queue Length",
            @"\Network Interface(*)\Packets Received Discarded",
            @"\Network Interface(*)\Packets Received Errors",
            @"\Network Interface(*)\Packets Outbound Discarded",
            @"\Network Interface(*)\Packets Outbound Errors",
            @"\TCPv4\Connections Established",
            @"\TCPv4\Connection Failures",
            @"\TCPv4\Connections Reset",
            @"\TCPv4\Segments Retransmitted/sec",
            @"\TCPv6\Connections Established",
            @"\TCPv6\Connection Failures",
            @"\TCPv6\Connections Reset",
            @"\TCPv6\Segments Retransmitted/sec",
            @"\UDPv4\Datagrams Received Errors",
            @"\UDPv4\Datagrams No Port/sec",
            @"\UDPv6\Datagrams Received Errors",
            @"\UDPv6\Datagrams No Port/sec"
        ],
        ["network_rss"] =
        [
            @"\Per Processor Network Interface Card Activity(*)\Received Packets/sec",
            @"\Per Processor Network Interface Card Activity(*)\Interrupts/sec",
            @"\Per Processor Network Interface Card Activity(*)\DPCs Queued/sec",
            @"\Per Processor Network Interface Card Activity(*)\Low Resource Received Packets/sec"
        ],
        ["hypervisor"] =
        [
            @"\Hyper-V Hypervisor Logical Processor(_Total)\% Total Run Time",
            @"\Hyper-V Hypervisor Virtual Processor(*)\% Guest Run Time",
            @"\Hyper-V Hypervisor Virtual Processor(*)\% Hypervisor Run Time",
            @"\Hyper-V Dynamic Memory VM(*)\Current Pressure",
            @"\VM Processor(*)\% Processor Time",
            @"\VM Memory(*)\Memory Ballooned in MB"
        ]
    };

    public static bool IsKnown(string preset) => Paths.ContainsKey(preset);

    public static IReadOnlyList<string> Names { get; } = Paths.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
}
