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
        kernel.ProcessStart += data => aggregator.NoteProcess(data.ProcessID, data.ProcessName);
        kernel.ImageLoad += data => aggregator.NoteImage((ulong)data.ImageBase, data.FileName);

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.DiskIo))
        {
            kernel.DiskIORead += data => Disk(aggregator, data, "read", maxEvents);
            kernel.DiskIOWrite += data => Disk(aggregator, data, "write", maxEvents);
            kernel.DiskIOFlushBuffers += data =>
            {
                aggregator.AddDiskIo(
                    data.DiskNumber.ToString(),
                    data.ProcessID,
                    data.ProcessName,
                    "flush",
                    null,
                    0,
                    0,
                    data.ElapsedTimeMSec);
                aggregator.Truncate(maxEvents);
            };
        }

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.FileIo))
        {
            // FileIO *TraceData in TraceEvent 4.0.2 does not expose elapsed time; DiskIO and DPC/ISR do.
            kernel.FileIORead += data =>
            {
                aggregator.AddFileIo(data.FileName, "read", data.ProcessID, data.ProcessName, data.IoSize, data.Offset, 0);
                aggregator.Truncate(maxEvents);
            };
            kernel.FileIOWrite += data =>
            {
                aggregator.AddFileIo(data.FileName, "write", data.ProcessID, data.ProcessName, data.IoSize, data.Offset, 0);
                aggregator.Truncate(maxEvents);
            };
            kernel.FileIOCreate += data =>
            {
                aggregator.AddFileIo(data.FileName, "create", data.ProcessID, data.ProcessName, 0, 0, 0);
                aggregator.Truncate(maxEvents);
            };
            kernel.FileIOClose += data =>
            {
                aggregator.AddFileIo(data.FileName, "close", data.ProcessID, data.ProcessName, 0, 0, 0);
                aggregator.Truncate(maxEvents);
            };
            kernel.FileIOFlush += data =>
            {
                aggregator.AddFileIo(data.FileName, "flush", data.ProcessID, data.ProcessName, 0, 0, 0);
                aggregator.Truncate(maxEvents);
            };
        }

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.DpcIsr))
        {
            kernel.PerfInfoDPC += data =>
            {
                aggregator.AddDpc((ulong)data.Routine, data.ElapsedTimeMSec);
                aggregator.Truncate(maxEvents);
            };
            kernel.PerfInfoISR += data =>
            {
                aggregator.AddIsr((ulong)data.Routine, data.ElapsedTimeMSec);
                aggregator.Truncate(maxEvents);
            };
        }

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.Scheduler))
        {
            kernel.ThreadCSwitch += data =>
            {
                aggregator.AddCSwitch(
                    data.OldProcessID,
                    data.ProcessID,
                    data.OldProcessName,
                    data.ProcessName,
                    data.OldThreadWaitReason.ToString(),
                    0,
                    0);
                aggregator.Truncate(maxEvents);
            };
            kernel.DispatcherReadyThread += data =>
            {
                aggregator.AddReadyDelay(0);
                aggregator.Truncate(maxEvents);
            };
        }

        if (KernelTraceGroups.Includes(groups, KernelTraceGroups.HardFaults))
        {
            kernel.MemoryHardFault += data =>
            {
                aggregator.AddHardFault(data.ProcessID, data.ProcessName, data.FileName);
                aggregator.Truncate(maxEvents);
            };
        }

        source.UnhandledEvents += _ => aggregator.Truncate(maxEvents);
    }

    private static void Disk(KernelLatencyAggregator aggregator, DiskIOTraceData data, string kind, int maxEvents)
    {
        aggregator.AddDiskIo(
            data.DiskNumber.ToString(),
            data.ProcessID,
            data.ProcessName,
            kind,
            data.FileName,
            data.TransferSize,
            data.ByteOffset,
            data.ElapsedTimeMSec);
        aggregator.Truncate(maxEvents);
    }
}
