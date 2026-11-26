namespace Vestigo.Performance;

/// <summary>Pure kernel-event aggregator. Feeds from TraceEvent live or offline parsers.</summary>
public sealed class KernelLatencyAggregator
{
    public const int TopN = 12;

    private readonly Dictionary<string, List<double>> _diskByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, List<double>> _diskByPid = new();
    private readonly Dictionary<int, string> _processNames = [];
    private readonly List<SlowIo> _slowDisk = [];
    private readonly Dictionary<(string Path, string Kind), List<double>> _fileIo = [];
    private readonly Dictionary<(string Path, string Kind), int> _fileIoCounts = [];
    private readonly List<SlowIo> _slowFile = [];
    private readonly Dictionary<string, List<double>> _dpc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<double>> _isr = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, SchedulerAcc> _scheduler = [];
    private readonly Dictionary<int, string> _waitReasons = [];
    private readonly List<double> _readyDelay = [];
    private readonly Dictionary<(int Pid, string File), int> _hardFaults = [];
    private readonly Dictionary<int, NetworkAcc> _network = [];
    private readonly Dictionary<ulong, (string Name, ulong Size)> _images = [];
    private readonly int _maxEvents;

    public KernelLatencyAggregator(int maxEvents = int.MaxValue) =>
        _maxEvents = maxEvents < 1 ? 1 : maxEvents;

    public int EventCount { get; private set; }
    public bool Truncated { get; private set; }
    public bool EventsLost { get; set; }
    public bool BuffersLost { get; set; }

    public bool Observe() => Accept();

    public void NoteProcess(int pid, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _processNames[pid] = name;
    }

    public void NoteImage(ulong address, ulong size, string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        _images[address] = (ImageName(file), size == 0 ? 1 : size);
    }

    public void AddDiskIo(string disk, int pid, string? process, string kind, string? path, long bytes, long offset, double elapsedMs)
    {
        if (!Accept())
        {
            return;
        }

        NoteProcess(pid, process);
        Add(_diskByName, disk, elapsedMs);
        Add(_diskByPid, pid, elapsedMs);
        ConsiderSlow(_slowDisk, new SlowIo(kind, pid, NameOf(pid), path, disk, bytes, offset, elapsedMs));
    }

    public void AddFileIo(string path, string kind, int pid, string? process, long bytes, long offset, double? elapsedMs)
    {
        if (!Accept())
        {
            return;
        }

        NoteProcess(pid, process);
        string key = string.IsNullOrWhiteSpace(path) ? "(unknown)" : path;
        (string Path, string Kind) fileKey = (key, kind);
        _fileIoCounts.TryGetValue(fileKey, out int count);
        _fileIoCounts[fileKey] = count + 1;
        if (elapsedMs is not { } measured)
        {
            return;
        }

        Add(_fileIo, fileKey, measured);
        ConsiderSlow(_slowFile, new SlowIo(kind, pid, NameOf(pid), key, null, bytes, offset, measured));
    }

    public void AddDpc(ulong routine, double elapsedMs) => AddTimed(_dpc, ResolveImage(routine), elapsedMs);

    public void AddIsr(ulong routine, double elapsedMs) => AddTimed(_isr, ResolveImage(routine), elapsedMs);

    public void AddCSwitch(int oldPid, int newPid, string? oldName, string? newName, string waitReason, double blockedMs, double cpuMs)
    {
        if (!Accept())
        {
            return;
        }

        NoteProcess(oldPid, oldName);
        NoteProcess(newPid, newName);
        Acc(oldPid).CpuMs += Math.Max(0, cpuMs);
        SchedulerAcc incoming = Acc(newPid);
        incoming.BlockedMs += Math.Max(0, blockedMs);
        incoming.WaitReasons.TryGetValue(waitReason, out (int Count, double Ms) wait);
        incoming.WaitReasons[waitReason] = (wait.Count + 1, wait.Ms + Math.Max(0, blockedMs));
        _waitReasons[newPid] = waitReason;
    }

    public void AddReadyDelay(double delayMs) => _readyDelay.Add(Math.Max(0, delayMs));

    public void AddHardFault(int pid, string? process, string? file)
    {
        if (!Accept())
        {
            return;
        }

        NoteProcess(pid, process);
        (int Pid, string File) key = (pid, string.IsNullOrWhiteSpace(file) ? "(unknown)" : file);
        _hardFaults.TryGetValue(key, out int count);
        _hardFaults[key] = count + 1;
    }

    public void AddNetwork(int pid, string? process, long bytesSent, long bytesReceived, int sends, int receives, int retransmits, int disconnects, int failures)
    {
        if (!Accept())
        {
            return;
        }

        NoteProcess(pid, process);
        NetworkAcc acc = Network(pid);
        acc.BytesSent += Math.Max(0, bytesSent);
        acc.BytesReceived += Math.Max(0, bytesReceived);
        acc.SendCount += sends;
        acc.ReceiveCount += receives;
        acc.Retransmits += retransmits;
        acc.Disconnects += disconnects;
        acc.Failures += failures;
    }

    public KernelLatencySummary Build(string sessionName, int durationMs, IReadOnlyList<string> groups, string? error)
    {
        List<DiskIoSummary> disks = _diskByName
            .OrderByDescending(pair => pair.Value.Count == 0 ? 0 : pair.Value.Max())
            .Take(TopN)
            .Select(pair => new DiskIoSummary(pair.Key, pair.Value.Count, PercentileStats.From(pair.Value), TakeSlow(_slowDisk, pair.Key)))
            .ToList();
        List<ProcessIoSummary> byProcess = _diskByPid
            .OrderByDescending(pair => pair.Value.Count == 0 ? 0 : pair.Value.Max())
            .Take(TopN)
            .Select(pair => new ProcessIoSummary(pair.Key, NameOf(pair.Key), pair.Value.Count, PercentileStats.From(pair.Value)))
            .ToList();
        List<FileIoSummary> files = _fileIoCounts
            .OrderByDescending(pair => pair.Value)
            .Take(TopN)
            .Select(pair => new FileIoSummary(
                pair.Key.Path,
                pair.Key.Kind,
                pair.Value,
                PercentileStats.From(_fileIo.GetValueOrDefault(pair.Key) ?? [])))
            .ToList();
        List<SchedulerProcessSummary> scheduler = _scheduler
            .Select(pair => new SchedulerProcessSummary(
                pair.Key,
                NameOf(pair.Key),
                pair.Value.CpuMs,
                pair.Value.BlockedMs,
                pair.Value.CpuMs <= 0 ? pair.Value.BlockedMs : pair.Value.BlockedMs / Math.Max(pair.Value.CpuMs, 0.001),
                pair.Value.WaitReasons
                    .OrderByDescending(wait => wait.Value.Ms)
                    .Take(5)
                    .Select(wait => new WaitReasonCount(wait.Key, wait.Value.Count, wait.Value.Ms))
                    .ToList()))
            .OrderByDescending(item => item.WaitRatio)
            .Take(TopN)
            .ToList();
        List<ProcessNetworkSummary> network = _network
            .Select(pair => new ProcessNetworkSummary(
                pair.Key,
                NameOf(pair.Key),
                pair.Value.BytesSent,
                pair.Value.BytesReceived,
                pair.Value.SendCount,
                pair.Value.ReceiveCount,
                pair.Value.Retransmits,
                pair.Value.Disconnects,
                pair.Value.Failures))
            .OrderByDescending(item => item.BytesSent + item.BytesReceived)
            .Take(TopN)
            .ToList();
        List<HardFaultSummary> faults = _hardFaults
            .OrderByDescending(pair => pair.Value)
            .Take(TopN)
            .Select(pair => new HardFaultSummary(pair.Key.Pid, NameOf(pair.Key.Pid), pair.Key.File, pair.Value))
            .ToList();

        return new KernelLatencySummary(
            sessionName,
            durationMs,
            groups,
            EventCount,
            Truncated,
            EventsLost,
            BuffersLost,
            disks,
            byProcess,
            _slowDisk.OrderByDescending(item => item.ElapsedMs).Take(TopN).ToList(),
            files,
            _slowFile.OrderByDescending(item => item.ElapsedMs).Take(TopN).ToList(),
            SummarizeDrivers(_dpc),
            SummarizeDrivers(_isr),
            scheduler,
            _readyDelay.Count == 0 ? null : new ReadyDelaySummary(PercentileStats.From(_readyDelay)),
            faults,
            network,
            error);
    }

    private bool Accept()
    {
        if (Truncated || EventCount >= _maxEvents)
        {
            Truncated = true;
            return false;
        }

        EventCount++;
        if (EventCount >= _maxEvents)
        {
            Truncated = true;
        }

        return true;
    }

    public void Truncate(int maxEvents)
    {
        if (EventCount >= maxEvents)
        {
            Truncated = true;
        }
    }

    private static void Add<TKey>(Dictionary<TKey, List<double>> map, TKey key, double value)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out List<double>? list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(Math.Max(0, value));
    }

    private void AddTimed(Dictionary<string, List<double>> map, string image, double elapsedMs)
    {
        if (!Accept())
        {
            return;
        }

        Add(map, image, elapsedMs);
    }

    private static void ConsiderSlow(List<SlowIo> list, SlowIo item)
    {
        list.Add(item);
        if (list.Count <= TopN * 4)
        {
            return;
        }

        list.Sort(static (left, right) => right.ElapsedMs.CompareTo(left.ElapsedMs));
        if (list.Count > TopN * 2)
        {
            list.RemoveRange(TopN * 2, list.Count - (TopN * 2));
        }
    }

    private List<SlowIo> TakeSlow(IEnumerable<SlowIo> source, string disk) =>
        source.Where(item => string.Equals(item.Disk, disk, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.ElapsedMs)
            .Take(5)
            .ToList();

    private List<DriverDpcSummary> SummarizeDrivers(Dictionary<string, List<double>> map) =>
        map.Select(pair => new DriverDpcSummary(
                pair.Key,
                pair.Value.Count,
                pair.Value.Sum(),
                pair.Value.Count == 0 ? 0 : pair.Value.Max(),
                PercentileStats.From(pair.Value)))
            .OrderByDescending(item => item.MaxMs)
            .Take(TopN)
            .ToList();

    private string NameOf(int pid) => _processNames.GetValueOrDefault(pid) ?? "unknown";

    private string ResolveImage(ulong routine)
    {
        ulong best = 0;
        string name = "unknown";
        foreach ((ulong address, (string file, ulong size)) in _images)
        {
            if (address <= routine && routine < address + size && address >= best)
            {
                best = address;
                name = file;
            }
        }

        return name;
    }

    private static string ImageName(string file)
    {
        int slash = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
        return slash >= 0 ? file[(slash + 1)..] : file;
    }

    private SchedulerAcc Acc(int pid)
    {
        if (!_scheduler.TryGetValue(pid, out SchedulerAcc? acc))
        {
            acc = new SchedulerAcc();
            _scheduler[pid] = acc;
        }

        return acc;
    }

    private NetworkAcc Network(int pid)
    {
        if (!_network.TryGetValue(pid, out NetworkAcc? acc))
        {
            acc = new NetworkAcc();
            _network[pid] = acc;
        }

        return acc;
    }

    private sealed class SchedulerAcc
    {
        public double CpuMs;
        public double BlockedMs;
        public Dictionary<string, (int Count, double Ms)> WaitReasons { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class NetworkAcc
    {
        public long BytesSent;
        public long BytesReceived;
        public int SendCount;
        public int ReceiveCount;
        public int Retransmits;
        public int Disconnects;
        public int Failures;
    }
}
