using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Vestigo.Processes;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Debug;
using Windows.Win32.System.Threading;

namespace Vestigo.Performance;

/// <summary>Inspects threads of one process with Wait Chain Traversal and kernel wait reasons.</summary>
public unsafe sealed class WindowsThreadInspector : IThreadInspector
{
    private const uint MaxNodes = 16;
    private const WAIT_CHAIN_THREAD_OPTIONS GetInfoAll =
        WAIT_CHAIN_THREAD_OPTIONS.WCT_OUT_OF_PROC_FLAG
        | WAIT_CHAIN_THREAD_OPTIONS.WCT_OUT_OF_PROC_COM_FLAG
        | WAIT_CHAIN_THREAD_OPTIONS.WCT_OUT_OF_PROC_CS_FLAG;

    public OperationResult<ThreadInspection> Inspect(int pid, DateTimeOffset? expectedStartTime)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<ThreadInspection>.Fail("windows_required", "Thread inspection requires Windows.");
        }

        if (ProcessGuards.CheckKillTarget(pid, Environment.ProcessId) is { } refusal)
        {
            return OperationResult<ThreadInspection>.Fail("protected_process", refusal);
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return OperationResult<ThreadInspection>.Fail("not_found", $"No process with id {pid}.");
        }

        using (process)
        {
            DateTimeOffset? start = null;
            try
            {
                start = process.StartTime;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
            }

            if (expectedStartTime is { } expected && start is { } actual
                && Math.Abs((actual - expected).TotalSeconds) > 1)
            {
                return OperationResult<ThreadInspection>.Fail("process_identity_mismatch", "The process start time does not match expectedStartTime.");
            }

            Dictionary<int, string?> waitReasons = ReadWaitReasons(pid);
            List<ThreadSample> threads = [];
            void* session = PInvoke.OpenThreadWaitChainSession(default, null);
            try
            {
                ProcessThreadCollection collection;
                try
                {
                    collection = process.Threads;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    return OperationResult<ThreadInspection>.Fail("access_denied", "The thread list could not be read.");
                }

                foreach (ProcessThread thread in collection)
                {
                    using (thread)
                    {
                        waitReasons.TryGetValue(thread.Id, out string? reason);
                        threads.Add(ReadThread(thread, session, reason));
                    }
                }
            }
            finally
            {
                if (session is not null)
                {
                    PInvoke.CloseThreadWaitChainSession(session);
                }
            }

            return OperationResult<ThreadInspection>.Ok(new ThreadInspection(pid, process.ProcessName, threads));
        }
    }

    private static ThreadSample ReadThread(ProcessThread thread, void* session, string? waitReason)
    {
        List<WaitChainNode> chain = [];
        bool deadlock = false;
        string? error = null;
        if (session is not null)
        {
            WAITCHAIN_NODE_INFO[] nodes = new WAITCHAIN_NODE_INFO[MaxNodes];
            uint count = MaxNodes;
            if (PInvoke.GetThreadWaitChain(
                    session,
                    0,
                    GetInfoAll,
                    (uint)thread.Id,
                    ref count,
                    nodes,
                    out BOOL cycle))
            {
                deadlock = cycle;
                for (int i = 0; i < count && i < nodes.Length; i++)
                {
                    chain.Add(MapNode(nodes[i]));
                }
            }
            else
            {
                error = $"wait_chain 0x{Marshal.GetLastWin32Error():X8}";
            }
        }

        double cpu = 0;
        try
        {
            cpu = thread.TotalProcessorTime.TotalMilliseconds;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
        }

        int priority = 0;
        try
        {
            priority = thread.CurrentPriority;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
        }

        string? state = null;
        try
        {
            state = thread.ThreadState.ToString();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
        }

        return new ThreadSample(thread.Id, cpu, priority, waitReason, state, deadlock, chain, error);
    }

    private static WaitChainNode MapNode(WAITCHAIN_NODE_INFO node)
    {
        string objectType = node.ObjectType.ToString();
        string? status = node.ObjectStatus.ToString();
        string? name = null;
        int? pid = null;
        int? tid = null;
        if (node.ObjectType == WCT_OBJECT_TYPE.WctThreadType)
        {
            pid = (int)node.Anonymous.ThreadObject.ProcessId;
            tid = (int)node.Anonymous.ThreadObject.ThreadId;
        }
        else
        {
            name = node.Anonymous.LockObject.ObjectName.ToString();
        }

        return new WaitChainNode(objectType, string.IsNullOrWhiteSpace(name) ? null : name, pid, tid, status);
    }

    private static Dictionary<int, string?> ReadWaitReasons(int pid)
    {
        Dictionary<int, string?> result = [];
        int length = 256 * 1024;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            nint buffer = Marshal.AllocHGlobal(length);
            try
            {
                int status = NativeNtQuery.NtQuerySystemInformation(
                    NativeNtQuery.SystemProcessInformation,
                    buffer,
                    length,
                    out int returned);
                if (status == NativeNtQuery.StatusInfoLengthMismatch)
                {
                    length = Math.Max(length * 2, returned + 4096);
                    continue;
                }

                if (status != NativeNtQuery.StatusSuccess)
                {
                    return result;
                }

                ParseProcessInformation(buffer, returned, pid, result);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return result;
    }

    private static void ParseProcessInformation(nint buffer, int length, int pid, Dictionary<int, string?> result)
    {
        int offset = 0;
        while (offset + 8 < length)
        {
            int next = Marshal.ReadInt32(buffer, offset);
            int threadCount = Marshal.ReadInt32(buffer, offset + 4);
            int uniquePid = (int)Marshal.ReadIntPtr(buffer, offset + 80);
            if (uniquePid == pid)
            {
                int threadOffset = offset + 336;
                for (int i = 0; i < threadCount && threadOffset + 80 < length; i++)
                {
                    int waitReason = Marshal.ReadByte(buffer, threadOffset + 73);
                    int tid = (int)Marshal.ReadIntPtr(buffer, threadOffset + 80);
                    result[tid] = WaitReasonName(waitReason);
                    threadOffset += 80;
                }

                return;
            }

            if (next == 0)
            {
                return;
            }

            offset += next;
        }
    }

    private static string WaitReasonName(int value) => value switch
    {
        0 => "Executive",
        1 => "FreePage",
        2 => "PageIn",
        3 => "PoolAllocation",
        5 => "Suspended",
        6 => "UserRequest",
        7 => "WrExecutive",
        8 => "WrFreePage",
        9 => "WrPageIn",
        12 => "WrSuspended",
        13 => "WrUserRequest",
        14 => "WrEventPair",
        15 => "WrQueue",
        16 => "WrLpcReceive",
        17 => "WrLpcReply",
        18 => "WrVirtualMemory",
        19 => "WrPageOut",
        24 => "WrResource",
        33 => "WrDispatchInt",
        36 => "WrFastMutex",
        37 => "WrGuardedMutex",
        38 => "WrRundown",
        39 => "WrAlertByThreadId",
        40 => "WrDeferredPreempt",
        41 => "WrPhysicalFault",
        42 => "WrIoRing",
        43 => "WrMdl",
        44 => "WrTimeout",
        45 => "WrFilterContext",
        _ => $"WaitReason({value})"
    };
}
