using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace Vestigo.Performance;

/// <summary>Maps TraceEvent kernel callbacks onto <see cref="KernelLatencyAggregator"/>.</summary>
internal static class KernelTraceEventSink
{
    public static void Attach(ETWTraceEventSource source, KernelLatencyAggregator aggregator, IReadOnlyList<string> groups, int maxEvents)
    {
        KernelTraceEventParser kernel = source.Kernel;
        SchedulerTimes scheduler = new();
        kernel.ProcessStart += data => aggregator.NoteProcess(data.ProcessID, data.ProcessName);
        kernel.ImageLoad += data => aggregator.NoteImage((ulong)data.ImageBase, (ulong)data.ImageSize, data.FileName);

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.DiskIo))
        {
            kernel.DiskIORead += data => Disk(aggregator, data, "read");
            kernel.DiskIOWrite += data => Disk(aggregator, data, "write");
            kernel.DiskIOFlushBuffers += data => aggregator.AddDiskIo(
                data.DiskNumber.ToString(),
                data.ProcessID,
                data.ProcessName,
                "flush",
                null,
                0,
                0,
                data.ElapsedTimeMSec);
        }

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.FileIo))
        {
            // FileIO *TraceData in TraceEvent 4.0.2 does not expose elapsed time; DiskIO and DPC/ISR do.
            kernel.FileIORead += data =>
                aggregator.AddFileIo(data.FileName, "read", data.ProcessID, data.ProcessName, data.IoSize, data.Offset, null);
            kernel.FileIOWrite += data =>
                aggregator.AddFileIo(data.FileName, "write", data.ProcessID, data.ProcessName, data.IoSize, data.Offset, null);
            kernel.FileIOCreate += data =>
                aggregator.AddFileIo(data.FileName, "create", data.ProcessID, data.ProcessName, 0, 0, null);
            kernel.FileIOClose += data =>
                aggregator.AddFileIo(data.FileName, "close", data.ProcessID, data.ProcessName, 0, 0, null);
            kernel.FileIOFlush += data =>
                aggregator.AddFileIo(data.FileName, "flush", data.ProcessID, data.ProcessName, 0, 0, null);
        }

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.DpcIsr))
        {
            kernel.PerfInfoDPC += data => aggregator.AddDpc((ulong)data.Routine, data.ElapsedTimeMSec);
            kernel.PerfInfoISR += data => aggregator.AddIsr((ulong)data.Routine, data.ElapsedTimeMSec);
        }

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.Scheduler))
        {
            kernel.ThreadCSwitch += data => scheduler.OnCSwitch(aggregator, data);
            kernel.DispatcherReadyThread += data => scheduler.OnReady(aggregator, data);
        }

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.HardFaults))
        {
            kernel.MemoryHardFault += data => aggregator.AddHardFault(data.ProcessID, data.ProcessName, data.FileName);
        }

        _ = maxEvents;
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

        public void OnCSwitch(KernelLatencyAggregator aggregator, CSwitchTraceData data)
        {
            double now = data.TimeStampRelativeMSec;
            (int Pid, int Tid) outgoing = (data.OldProcessID, data.OldThreadID);
            (int Pid, int Tid) incoming = (data.ProcessID, data.NewThreadID);
            double cpuMs = _lastSwitch.TryGetValue(outgoing, out double lastOut) ? Math.Max(0, now - lastOut) : 0;
            double blockedMs = _lastSwitch.TryGetValue(incoming, out double lastIn) ? Math.Max(0, now - lastIn) : 0;
            aggregator.AddCSwitch(
                data.OldProcessID,
                data.ProcessID,
                data.OldProcessName,
                data.ProcessName,
                data.OldThreadWaitReason.ToString(),
                blockedMs,
                cpuMs);
            if (_lastReady.TryGetValue(incoming, out double readyAt))
            {
                aggregator.AddReadyDelay(Math.Max(0, now - readyAt));
                _lastReady.Remove(incoming);
            }

            _lastSwitch[outgoing] = now;
            _lastSwitch[incoming] = now;
        }

        public void OnReady(KernelLatencyAggregator aggregator, DispatcherReadyThreadTraceData data)
        {
            _lastReady[(data.ProcessID, data.ThreadID)] = data.TimeStampRelativeMSec;
            _ = aggregator;
        }
    }
}
