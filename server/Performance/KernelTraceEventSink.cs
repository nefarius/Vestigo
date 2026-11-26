using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace Vestigo.Performance;

/// <summary>Maps TraceEvent kernel callbacks onto <see cref="KernelLatencyAggregator"/>.</summary>
internal static class KernelTraceEventSink
{
    public static void Attach(ETWTraceEventSource source, KernelLatencyAggregator aggregator, IReadOnlyList<string> groups)
    {
        KernelTraceEventParser kernel = source.Kernel;
        SchedulerTimes scheduler = new();
        kernel.ProcessStart += data => BoundSource(
            source,
            aggregator,
            () => aggregator.NoteProcess(data.ProcessID, data.ProcessName));
        kernel.ImageLoadGroup += data => BoundSource(
            source,
            aggregator,
            () => aggregator.NoteImage((ulong)data.ImageBase, (ulong)data.ImageSize, data.FileName));

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.DiskIo))
        {
            kernel.DiskIORead += data => Bound(source, aggregator, () => Disk(aggregator, data, "read"));
            kernel.DiskIOWrite += data => Bound(source, aggregator, () => Disk(aggregator, data, "write"));
            kernel.DiskIOFlushBuffers += data => Bound(
                source,
                aggregator,
                () => aggregator.AddDiskIo(
                    data.DiskNumber.ToString(),
                    data.ProcessID,
                    data.ProcessName,
                    "flush",
                    null,
                    0,
                    0,
                    data.ElapsedTimeMSec));
        }

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.FileIo))
        {
            // FileIO *TraceData in TraceEvent 4.0.2 does not expose elapsed time; DiskIO and DPC/ISR do.
            kernel.FileIORead += data => Bound(
                source,
                aggregator,
                () => aggregator.AddFileIo(data.FileName, "read", data.ProcessID, data.ProcessName, data.IoSize, data.Offset, null));
            kernel.FileIOWrite += data => Bound(
                source,
                aggregator,
                () => aggregator.AddFileIo(data.FileName, "write", data.ProcessID, data.ProcessName, data.IoSize, data.Offset, null));
            kernel.FileIOCreate += data => Bound(
                source,
                aggregator,
                () => aggregator.AddFileIo(data.FileName, "create", data.ProcessID, data.ProcessName, 0, 0, null));
            kernel.FileIOClose += data => Bound(
                source,
                aggregator,
                () => aggregator.AddFileIo(data.FileName, "close", data.ProcessID, data.ProcessName, 0, 0, null));
            kernel.FileIOFlush += data => Bound(
                source,
                aggregator,
                () => aggregator.AddFileIo(data.FileName, "flush", data.ProcessID, data.ProcessName, 0, 0, null));
        }

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.DpcIsr))
        {
            kernel.PerfInfoDPC += data => Bound(source, aggregator, () => aggregator.AddDpc((ulong)data.Routine, data.ElapsedTimeMSec));
            kernel.PerfInfoISR += data => Bound(source, aggregator, () => aggregator.AddIsr((ulong)data.Routine, data.ElapsedTimeMSec));
        }

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.Scheduler))
        {
            kernel.ThreadCSwitch += data => Bound(source, aggregator, () => scheduler.OnCSwitch(aggregator, data));
            kernel.DispatcherReadyThread += data => BoundSource(source, aggregator, () => scheduler.OnReady(data));
        }

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.HardFaults))
        {
            kernel.MemoryHardFault += data => Bound(
                source,
                aggregator,
                () => aggregator.AddHardFault(data.ProcessID, data.ProcessName, data.FileName));
        }

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.Network))
        {
            kernel.TcpIpSend += data => Bound(source, aggregator, () => aggregator.AddNetwork(data.ProcessID, data.ProcessName, data.size, 0, 1, 0, 0, 0, 0));
            kernel.TcpIpRecv += data => Bound(source, aggregator, () => aggregator.AddNetwork(data.ProcessID, data.ProcessName, 0, data.size, 0, 1, 0, 0, 0));
            kernel.TcpIpRetransmit += data => Bound(source, aggregator, () => aggregator.AddNetwork(data.ProcessID, data.ProcessName, 0, 0, 0, 0, 1, 0, 0));
            kernel.TcpIpReconnect += data => Bound(source, aggregator, () => aggregator.AddNetwork(data.ProcessID, data.ProcessName, 0, 0, 0, 0, 0, 0, 0));
            kernel.TcpIpDisconnect += data => Bound(source, aggregator, () => aggregator.AddNetwork(data.ProcessID, data.ProcessName, 0, 0, 0, 0, 0, 1, 0));
            kernel.TcpIpFail += data => Bound(source, aggregator, () => aggregator.AddNetwork(data.ProcessID, data.ProcessName, 0, 0, 0, 0, 0, 0, 1));
            kernel.UdpIpSend += data => Bound(source, aggregator, () => aggregator.AddNetwork(data.ProcessID, data.ProcessName, data.size, 0, 1, 0, 0, 0, 0));
            kernel.UdpIpRecv += data => Bound(source, aggregator, () => aggregator.AddNetwork(data.ProcessID, data.ProcessName, 0, data.size, 0, 1, 0, 0, 0));
            kernel.TcpIpSendIPV6 += data => Bound(source, aggregator, () => aggregator.AddNetwork(data.ProcessID, data.ProcessName, data.size, 0, 1, 0, 0, 0, 0));
            kernel.TcpIpRecvIPV6 += data => Bound(source, aggregator, () => aggregator.AddNetwork(data.ProcessID, data.ProcessName, 0, data.size, 0, 1, 0, 0, 0));
            kernel.TcpIpRetransmitIPV6 += data => Bound(source, aggregator, () => aggregator.AddNetwork(data.ProcessID, data.ProcessName, 0, 0, 0, 0, 1, 0, 0));
            kernel.TcpIpReconnectIPV6 += data => Bound(source, aggregator, () => aggregator.AddNetwork(data.ProcessID, data.ProcessName, 0, 0, 0, 0, 0, 0, 0));
            kernel.TcpIpDisconnectIPV6 += data => Bound(source, aggregator, () => aggregator.AddNetwork(data.ProcessID, data.ProcessName, 0, 0, 0, 0, 0, 1, 0));
            kernel.UdpIpSendIPV6 += data => Bound(source, aggregator, () => aggregator.AddNetwork(data.ProcessID, data.ProcessName, data.size, 0, 1, 0, 0, 0, 0));
            kernel.UdpIpRecvIPV6 += data => Bound(source, aggregator, () => aggregator.AddNetwork(data.ProcessID, data.ProcessName, 0, data.size, 0, 1, 0, 0, 0));
        }
    }

    private static void BoundSource(ETWTraceEventSource source, KernelLatencyAggregator aggregator, Action action) =>
        Bound(source, aggregator, () =>
        {
            if (!aggregator.Observe())
            {
                return;
            }

            action();
        });

    private static void Bound(ETWTraceEventSource source, KernelLatencyAggregator aggregator, Action action)
    {
        if (aggregator.Truncated)
        {
            source.StopProcessing();
            return;
        }

        action();
        if (aggregator.Truncated)
        {
            source.StopProcessing();
        }
    }

    private static void Disk(KernelLatencyAggregator aggregator, DiskIOTraceData data, string kind) =>
        aggregator.AddDiskIo(
            data.DiskNumber.ToString(),
            data.ProcessID,
            data.ProcessName,
            kind,
            data.FileName,
            data.TransferSize,
            data.ByteOffset,
            data.ElapsedTimeMSec);

    private sealed class SchedulerTimes
    {
        private readonly Dictionary<(int Pid, int Tid), double> _lastSwitch = [];
        private readonly Dictionary<(int Pid, int Tid), double> _lastReady = [];
        private readonly Dictionary<(int Pid, int Tid), string> _waitReasons = [];

        public void OnCSwitch(KernelLatencyAggregator aggregator, CSwitchTraceData data)
        {
            double now = data.TimeStampRelativeMSec;
            (int Pid, int Tid) outgoing = (data.OldProcessID, data.OldThreadID);
            (int Pid, int Tid) incoming = (data.ProcessID, data.NewThreadID);
            double cpuMs = _lastSwitch.TryGetValue(outgoing, out double lastOut) ? Math.Max(0, now - lastOut) : 0;
            double blockedMs = _lastSwitch.TryGetValue(incoming, out double lastIn) ? Math.Max(0, now - lastIn) : 0;
            string outgoingWait = data.OldThreadWaitReason.ToString();
            _waitReasons[outgoing] = outgoingWait;
            string incomingWait = _waitReasons.GetValueOrDefault(incoming) ?? outgoingWait;
            aggregator.AddCSwitch(
                data.OldProcessID,
                data.ProcessID,
                data.OldProcessName,
                data.ProcessName,
                incomingWait,
                blockedMs,
                cpuMs);
            if (data.ProcessID != 0 && _lastReady.TryGetValue(incoming, out double readyAt))
            {
                aggregator.AddReadyDelay(data.ProcessID, data.ProcessName, Math.Max(0, now - readyAt));
                _lastReady.Remove(incoming);
            }

            _lastSwitch[outgoing] = now;
            _lastSwitch[incoming] = now;
        }

        public void OnReady(DispatcherReadyThreadTraceData data)
        {
            if (data.AwakenedProcessID != 0)
            {
                _lastReady[(data.AwakenedProcessID, data.AwakenedThreadID)] = data.TimeStampRelativeMSec;
            }
        }
    }
}
