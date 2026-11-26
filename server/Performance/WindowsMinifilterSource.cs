using System.Text;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.InstallableFileSystems;

namespace Vestigo.Performance;

/// <summary>Enumerates filesystem minifilters through CsWin32 fltlib.dll APIs.</summary>
public sealed class WindowsMinifilterSource : IMinifilterSource
{
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

                filters.Add(new MinifilterInfo(filter.Name, filter.Altitude, filter.FrameId, instances));
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
            if (first.Failed)
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
                if (next.Failed)
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
        status.Value is unchecked((int)0x80070003) or unchecked((int)0x80070012) or 259;

    private static IEnumerable<FilterRecord> ReadFilters(byte[] buffer, uint length)
    {
        int offset = 0;
        while (offset + 16 <= length && offset < buffer.Length)
        {
            uint next = BitConverter.ToUInt32(buffer, offset);
            uint frame = BitConverter.ToUInt32(buffer, offset + 4);
            ushort nameBytes = BitConverter.ToUInt16(buffer, offset + 10);
            int nameOffset = offset + 12;
            string name = nameBytes == 0 || nameOffset + nameBytes > buffer.Length
                ? string.Empty
                : Encoding.Unicode.GetString(buffer, nameOffset, nameBytes).TrimEnd('\0');
            uint altitude = ParseAltitude(name);
            yield return new FilterRecord(name, altitude, frame);
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
        while (offset + 20 <= length && offset < buffer.Length)
        {
            uint next = BitConverter.ToUInt32(buffer, offset);
            ushort nameBytes = BitConverter.ToUInt16(buffer, offset + 8);
            ushort volumeBytes = BitConverter.ToUInt16(buffer, offset + 12);
            ushort altitudeBytes = BitConverter.ToUInt16(buffer, offset + 16);
            int cursor = offset + 20;
            string name = ReadUnicode(buffer, ref cursor, nameBytes);
            string volume = ReadUnicode(buffer, ref cursor, volumeBytes);
            string altitudeText = ReadUnicode(buffer, ref cursor, altitudeBytes);
            uint.TryParse(altitudeText, out uint altitude);
            yield return new InstanceRecord(name, volume, altitude);
            if (next == 0)
            {
                yield break;
            }

            offset += (int)next;
        }
    }

    private static string ReadUnicode(byte[] buffer, ref int cursor, int bytes)
    {
        if (bytes <= 0 || cursor + bytes > buffer.Length)
        {
            return string.Empty;
        }

        string text = Encoding.Unicode.GetString(buffer, cursor, bytes).TrimEnd('\0');
        cursor += bytes;
        return text;
    }

    private static uint ParseAltitude(string name)
    {
        int dash = name.LastIndexOf('_');
        if (dash > 0 && uint.TryParse(name[(dash + 1)..], out uint altitude))
        {
            return altitude;
        }

        return 0;
    }

    private readonly record struct FilterRecord(string Name, uint Altitude, uint FrameId);

    private readonly record struct InstanceRecord(string Name, string Volume, uint Altitude);
}
