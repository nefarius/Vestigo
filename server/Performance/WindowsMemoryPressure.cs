using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.System.SystemInformation;

using WmiLight;

namespace Vestigo.Performance;

/// <summary>Reads commit charge, pagefiles, kernel pools, and optional pool-tag consumers.</summary>
public sealed class WindowsMemoryPressure : IMemoryPressureSource
{
    public OperationResult<MemoryPressure> Read(bool includePoolTags, int poolTagLimit)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<MemoryPressure>.Fail("windows_required", "Memory pressure inspection requires Windows.");
        }

        try
        {
            MEMORYSTATUSEX status = default;
            status.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
            if (!PInvoke.GlobalMemoryStatusEx(ref status))
            {
                return OperationResult<MemoryPressure>.Fail("memory_query_failed", "GlobalMemoryStatusEx failed.");
            }

            NativePsapi.PerformanceInformation performance = default;
            performance.cb = (uint)Marshal.SizeOf<NativePsapi.PerformanceInformation>();
            _ = NativePsapi.GetPerformanceInfo(ref performance, performance.cb);

            List<PagefileInfo> pagefiles = [];
            foreach (WmiObject row in WmiQuery.Query(@"root\CIMV2", "SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage"))
            {
                pagefiles.Add(new PagefileInfo(
                    WmiQuery.String(row, "Name"),
                    (WmiQuery.Int64(row, "AllocatedBaseSize") ?? 0) * 1024 * 1024,
                    (WmiQuery.Int64(row, "CurrentUsage") ?? 0) * 1024 * 1024,
                    (WmiQuery.Int64(row, "PeakUsage") ?? 0) * 1024 * 1024));
            }

            long? compression = null;
            foreach (WmiObject row in WmiQuery.Query(@"root\CIMV2", "SELECT WorkingSetSize FROM Win32_Process WHERE Name = 'Memory Compression'"))
            {
                compression = WmiQuery.Int64(row, "WorkingSetSize");
            }

            long? balloon = null;
            foreach (WmiObject row in WmiQuery.Query(@"root\CIMV2", "SELECT WorkingSetSize, Name FROM Win32_Process"))
            {
                string? name = WmiQuery.String(row, "Name");
                if (name is not null
                    && (name.Contains("blnsvr", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("balloon", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("vmtoolsd", StringComparison.OrdinalIgnoreCase)))
                {
                    balloon = (balloon ?? 0) + (WmiQuery.Int64(row, "WorkingSetSize") ?? 0);
                }
            }

            IReadOnlyList<PoolTagInfo>? tags = includePoolTags ? ReadPoolTags(poolTagLimit) : null;
            long pageSize = (long)performance.PageSize;
            return OperationResult<MemoryPressure>.Ok(new MemoryPressure(
                (long)status.ullTotalPhys,
                (long)status.ullAvailPhys,
                (long)status.ullTotalPageFile - (long)status.ullAvailPageFile,
                (long)status.ullTotalPageFile,
                (long)performance.KernelPaged * pageSize,
                (long)performance.KernelNonpaged * pageSize,
                compression,
                balloon,
                pagefiles,
                tags));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OperationResult<MemoryPressure>.Fail("memory_query_failed", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IReadOnlyList<PoolTagInfo> ReadPoolTags(int limit)
    {
        int length = 256 * 1024;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            nint buffer = Marshal.AllocHGlobal(length);
            try
            {
                int status = NativeNtQuery.NtQuerySystemInformation(
                    NativeNtQuery.SystemPoolTagInformation,
                    buffer,
                    length,
                    out int returned);
                if (status == NativeNtQuery.StatusInfoLengthMismatch)
                {
                    length = Math.Max(length * 2, returned + 4096);
                    continue;
                }

                if (status != NativeNtQuery.StatusSuccess || returned < 4)
                {
                    return [];
                }

                int count = Marshal.ReadInt32(buffer);
                List<PoolTagInfo> tags = [];
                int offset = 8;
                int stride = 40;
                for (int i = 0; i < count && offset + stride <= returned; i++)
                {
                    uint tagValue = (uint)Marshal.ReadInt32(buffer, offset);
                    char[] chars =
                    [
                        (char)(tagValue & 0xFF),
                        (char)((tagValue >> 8) & 0xFF),
                        (char)((tagValue >> 16) & 0xFF),
                        (char)((tagValue >> 24) & 0xFF)
                    ];
                    long allocs = Marshal.ReadInt32(buffer, offset + 4);
                    long paged = Marshal.ReadInt64(buffer, offset + 16);
                    long nonpaged = Marshal.ReadInt64(buffer, offset + 32);
                    tags.Add(new PoolTagInfo(new string(chars).Trim('\0', ' '), paged, nonpaged, allocs));
                    offset += stride;
                }

                return tags
                    .OrderByDescending(tag => tag.PagedBytes + tag.NonPagedBytes)
                    .Take(Math.Clamp(limit, 1, 64))
                    .ToList();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return [];
    }
}
