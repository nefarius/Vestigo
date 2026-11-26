using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

using Vestigo.Processes;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace Vestigo.Performance;

/// <summary>Samples per-process CPU, I/O, memory, and EcoQoS over a bounded window.</summary>
public sealed class WindowsProcessMetrics : IProcessMetricSource
{
    public async Task<OperationResult<ProcessMetricResult>> SampleAsync(
        TimeSpan duration,
        int? pid,
        string? nameFilter,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<ProcessMetricResult>.Fail("windows_required", "Process metric sampling requires Windows.");
        }

        List<Snapshot> first = Capture(pid, nameFilter);
        long started = TimeProvider.System.GetTimestamp();
        await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        List<Snapshot> second = Capture(pid, nameFilter);
        Dictionary<int, Snapshot> later = second.ToDictionary(item => item.Pid);
        double elapsedSec = Math.Max(TimeProvider.System.GetElapsedTime(started).TotalSeconds, 0.001);
        List<ProcessMetricSample> samples = [];
        foreach (Snapshot before in first)
        {
            if (before.Error is not null
                || !later.TryGetValue(before.Pid, out Snapshot? after)
                || after.Error is not null
                || after.StartTime != before.StartTime)
            {
                continue;
            }

            samples.Add(after.Delta(before, elapsedSec));
        }

        samples = samples
            .OrderByDescending(item => item.CpuPercent)
            .ThenByDescending(item => item.IoReadBytesPerSec + item.IoWriteBytesPerSec)
            .Take(limit)
            .ToList();
        return OperationResult<ProcessMetricResult>.Ok(new ProcessMetricResult(
            (int)duration.TotalMilliseconds,
            samples,
            first.Count > limit || second.Count > limit));
    }

    private static List<Snapshot> Capture(int? pid, string? nameFilter)
    {
        List<Snapshot> result = [];
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (pid is { } wanted && process.Id != wanted)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(nameFilter)
                    && process.ProcessName.IndexOf(nameFilter.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (ProcessGuards.CheckKillTarget(process.Id, -1) is not null && process.Id is 0 or 4)
                {
                    continue;
                }

                result.Add(Snapshot.Read(process));
            }
        }

        return result;
    }

    private sealed class Snapshot
    {
        public required int Pid { get; init; }
        public required string Name { get; init; }
        public DateTimeOffset? StartTime { get; init; }
        public long KernelTime { get; init; }
        public long UserTime { get; init; }
        public IO_COUNTERS Io { get; init; }
        public long WorkingSet { get; init; }
        public long PrivateBytes { get; init; }
        public uint PageFaults { get; init; }
        public uint Handles { get; init; }
        public int Threads { get; init; }
        public int? Gdi { get; init; }
        public int? User { get; init; }
        public string? Priority { get; init; }
        public bool? EcoQos { get; init; }
        public string? Error { get; init; }

        public static Snapshot Read(Process process)
        {
            DateTimeOffset? start = null;
            try
            {
                start = process.StartTime;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
            }

            using SafeFileHandle? handle = Open(process.Id);
            if (handle is null)
            {
                return new Snapshot
                {
                    Pid = process.Id,
                    Name = process.ProcessName,
                    StartTime = start,
                    Threads = SafeThreads(process),
                    Error = "access_denied"
                };
            }

            IO_COUNTERS io = default;
            _ = PInvoke.GetProcessIoCounters(handle, out io);
            NativePsapi.ProcessMemoryCountersEx memory = default;
            memory.cb = (uint)Marshal.SizeOf<NativePsapi.ProcessMemoryCountersEx>();
            _ = NativePsapi.GetProcessMemoryInfo(handle.DangerousGetHandle(), ref memory, memory.cb);
            GC.KeepAlive(handle);
            uint handles = 0;
            _ = PInvoke.GetProcessHandleCount(handle, out handles);
            if (ProcessStartTime.TryRead(handle, out DateTimeOffset nativeStart, out _) && nativeStart != default)
            {
                start = nativeStart;
            }
            TryTimes(handle, out long kernel, out long user);
            return new Snapshot
            {
                Pid = process.Id,
                Name = process.ProcessName,
                StartTime = start,
                KernelTime = kernel,
                UserTime = user,
                Io = io,
                WorkingSet = (long)memory.WorkingSetSize,
                PrivateBytes = (long)memory.PrivateUsage,
                PageFaults = memory.PageFaultCount,
                Handles = handles,
                Threads = SafeThreads(process),
                Gdi = (int)PInvoke.GetGuiResources(handle, GET_GUI_RESOURCES_FLAGS.GR_GDIOBJECTS),
                User = (int)PInvoke.GetGuiResources(handle, GET_GUI_RESOURCES_FLAGS.GR_USEROBJECTS),
                Priority = SafePriority(process),
                EcoQos = ReadEcoQos(handle)
            };
        }

        public ProcessMetricSample Delta(Snapshot before, double elapsedSec)
        {
            double cpuTicks = (KernelTime - before.KernelTime) + (UserTime - before.UserTime);
            double cpuPercent = elapsedSec <= 0 ? 0 : cpuTicks / TimeSpan.TicksPerSecond / elapsedSec / Environment.ProcessorCount * 100.0;
            return new ProcessMetricSample(
                Pid,
                Name,
                StartTime,
                Math.Max(0, cpuPercent),
                Rate(Io.ReadTransferCount, before.Io.ReadTransferCount, elapsedSec),
                Rate(Io.WriteTransferCount, before.Io.WriteTransferCount, elapsedSec),
                Rate(Io.OtherTransferCount, before.Io.OtherTransferCount, elapsedSec),
                (long)(Io.ReadOperationCount - before.Io.ReadOperationCount),
                (long)(Io.WriteOperationCount - before.Io.WriteOperationCount),
                (long)(Io.OtherOperationCount - before.Io.OtherOperationCount),
                WorkingSet,
                PrivateBytes,
                PageFaults - before.PageFaults,
                (int)Handles,
                Threads,
                Gdi,
                User,
                Priority,
                EcoQos,
                Error);
        }

        private static double Rate(ulong after, ulong before, double elapsedSec) =>
            elapsedSec <= 0 || after < before ? 0 : (after - before) / elapsedSec;
    }

    private static SafeFileHandle? Open(int pid)
    {
        HANDLE handle = PInvoke.OpenProcess(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            (uint)pid);
        if (handle.IsNull)
        {
            return null;
        }

        return new SafeFileHandle(handle, true);
    }

    private static void TryTimes(SafeFileHandle handle, out long kernel, out long user)
    {
        if (!ProcessStartTime.TryReadTimes(handle, out kernel, out user))
        {
            kernel = 0;
            user = 0;
        }
    }

    private static int SafeThreads(Process process)
    {
        try
        {
            return process.Threads.Count;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return 0;
        }
    }

    private static string? SafePriority(Process process)
    {
        try
        {
            return process.PriorityClass.ToString();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool? ReadEcoQos(SafeFileHandle handle)
    {
        PROCESS_POWER_THROTTLING_STATE state = default;
        state.Version = PInvoke.PROCESS_POWER_THROTTLING_CURRENT_VERSION;
        byte[] buffer = new byte[Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()];
        MemoryMarshal.Write(buffer, in state);
        if (!PInvoke.GetProcessInformation(
                handle,
                PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
                buffer))
        {
            return null;
        }

        state = MemoryMarshal.Read<PROCESS_POWER_THROTTLING_STATE>(buffer);
        return (state.StateMask & PInvoke.PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0;
    }
}
