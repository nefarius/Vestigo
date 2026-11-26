using System.Text;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.InstallableFileSystems;

namespace Vestigo.Performance;

/// <summary>Enumerates filesystem minifilters through CsWin32 fltlib.dll APIs.</summary>
public sealed class WindowsMinifilterSource : IMinifilterSource
{
    private const int FilterHeader = 14;
    private const int InstanceHeader = 20;
    private const int HresultNoMoreItems = unchecked((int)0x80070103);
    private const int HresultPathNotFound = unchecked((int)0x80070003);
    private const int HresultNoMoreFiles = unchecked((int)0x80070012);

    public OperationResult<MinifilterList> List()
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<MinifilterList>.Fail("windows_required", "Filesystem minifilter inventory requires Windows.");
        }

        try
        {
            List<MinifilterInfo> filters = [];
            foreach (FilterRecord filter in EnumerateFilters())
            {
                List<MinifilterInstance> instances = [];
                foreach (InstanceRecord instance in EnumerateInstances(filter.Name))
                {
                    instances.Add(new MinifilterInstance(instance.Name, instance.Volume, instance.Altitude));
                }

                string? altitude = instances.Count == 0 ? null : instances[0].Altitude;
                filters.Add(new MinifilterInfo(filter.Name, altitude, filter.FrameId, instances));
            }

            return OperationResult<MinifilterList>.Ok(new MinifilterList(filters));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OperationResult<MinifilterList>.Fail("minifilter_query_failed", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IEnumerable<FilterRecord> EnumerateFilters()
    {
        uint needed = 1024;
        byte[] buffer = new byte[needed];
        HRESULT first = PInvoke.FilterFindFirst(
            FILTER_INFORMATION_CLASS.FilterFullInformation,
            buffer,
            out needed,
            out FilterFindCloseSafeHandle find);
        if (IsEmpty(first))
        {
            find.Dispose();
            yield break;
        }

        if (first.Failed && needed > buffer.Length)
        {
            find.Dispose();
            buffer = new byte[needed];
            first = PInvoke.FilterFindFirst(
                FILTER_INFORMATION_CLASS.FilterFullInformation,
                buffer,
                out needed,
                out find);
        }

        using (find)
        {
            if (IsEmpty(first))
            {
                yield break;
            }

            if (first.Failed)
            {
                throw new InvalidOperationException($"FilterFindFirst failed 0x{first.Value:X8}.");
            }

            foreach (FilterRecord record in ReadFilters(buffer, needed))
            {
                yield return record;
            }

            while (true)
            {
                buffer = new byte[Math.Max(needed, buffer.Length)];
                HRESULT next = PInvoke.FilterFindNext(
                    find,
                    FILTER_INFORMATION_CLASS.FilterFullInformation,
                    buffer,
                    out needed);
                if (IsEmpty(next))
                {
                    yield break;
                }

                if (next.Failed)
                {
                    throw new InvalidOperationException($"FilterFindNext failed 0x{next.Value:X8}.");
                }

                foreach (FilterRecord record in ReadFilters(buffer, needed))
                {
                    yield return record;
                }
            }
        }
    }

    private static IEnumerable<InstanceRecord> EnumerateInstances(string filterName)
    {
        uint needed = 1024;
        byte[] buffer = new byte[needed];
        HRESULT first = PInvoke.FilterInstanceFindFirst(
            filterName,
            INSTANCE_INFORMATION_CLASS.InstanceFullInformation,
            buffer,
            out needed,
            out FilterInstanceFindCloseSafeHandle find);
        if (first.Failed && needed > buffer.Length)
        {
            find.Dispose();
            buffer = new byte[needed];
            first = PInvoke.FilterInstanceFindFirst(
                filterName,
                INSTANCE_INFORMATION_CLASS.InstanceFullInformation,
                buffer,
                out needed,
                out find);
        }

        using (find)
        {
            if (IsEmpty(first) || first.Failed)
            {
                yield break;
            }

            foreach (InstanceRecord record in ReadInstances(buffer, needed))
            {
                yield return record;
            }

            while (true)
            {
                buffer = new byte[Math.Max(needed, buffer.Length)];
                HRESULT next = PInvoke.FilterInstanceFindNext(
                    find,
                    INSTANCE_INFORMATION_CLASS.InstanceFullInformation,
                    buffer,
                    out needed);
                if (IsEmpty(next) || next.Failed)
                {
                    yield break;
                }

                foreach (InstanceRecord record in ReadInstances(buffer, needed))
                {
                    yield return record;
                }
            }
        }
    }

    private static bool IsEmpty(HRESULT status) =>
        status.Value is HresultPathNotFound or HresultNoMoreFiles or HresultNoMoreItems or 259;

    private static IEnumerable<FilterRecord> ReadFilters(byte[] buffer, uint length)
    {
        int offset = 0;
        while (offset + FilterHeader <= length && offset < buffer.Length)
        {
            uint next = BitConverter.ToUInt32(buffer, offset);
            uint frame = BitConverter.ToUInt32(buffer, offset + 4);
            ushort nameBytes = BitConverter.ToUInt16(buffer, offset + 12);
            int nameOffset = offset + 14;
            string name = nameBytes == 0 || nameOffset + nameBytes > buffer.Length
                ? string.Empty
                : Encoding.Unicode.GetString(buffer, nameOffset, nameBytes).TrimEnd('\0');
            yield return new FilterRecord(name, frame);
            if (next == 0)
            {
                yield break;
            }

            offset += (int)next;
        }
    }

    private static IEnumerable<InstanceRecord> ReadInstances(byte[] buffer, uint length)
    {
        int offset = 0;
        while (offset + InstanceHeader <= length && offset < buffer.Length)
        {
            uint next = BitConverter.ToUInt32(buffer, offset);
            string name = ReadAt(buffer, offset, offset + 4, offset + 6, length);
            string altitude = ReadAt(buffer, offset, offset + 8, offset + 10, length);
            string volume = ReadAt(buffer, offset, offset + 12, offset + 14, length);
            yield return new InstanceRecord(name, volume, altitude);
            if (next == 0)
            {
                yield break;
            }

            offset += (int)next;
        }
    }

    private static string ReadAt(byte[] buffer, int record, int lengthOffset, int pointerOffset, uint length)
    {
        ushort bytes = BitConverter.ToUInt16(buffer, lengthOffset);
        ushort relative = BitConverter.ToUInt16(buffer, pointerOffset);
        int start = record + relative;
        if (bytes == 0 || start < record || start + bytes > buffer.Length || start + bytes > length)
        {
            return string.Empty;
        }

        return Encoding.Unicode.GetString(buffer, start, bytes).TrimEnd('\0');
    }

    private readonly record struct FilterRecord(string Name, uint FrameId);

    private readonly record struct InstanceRecord(string Name, string Volume, string Altitude);
}
