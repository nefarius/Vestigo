using WmiLight;

namespace Vestigo.Performance;

/// <summary>Reads storage topology through WmiLight from ROOT\Microsoft\Windows\Storage and ROOT\CIMV2.</summary>
public sealed class WindowsStorageTopology : IStorageTopologySource
{
    private static readonly string[] BusNames =
    [
        "Unknown", "SCSI", "ATAPI", "ATA", "1394", "SSA", "FibreChannel", "USB", "RAID", "iSCSI", "SAS", "SATA", "SD",
        "MMC", "Virtual", "FileBackedVirtual", "StorageSpaces", "NVMe", "MicrosoftReserved", "SCM", "UFS"
    ];

    public OperationResult<StorageTopology> Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<StorageTopology>.Fail("windows_required", "Storage topology requires Windows.");
        }

        try
        {
            IReadOnlyList<WmiObject> physical = WmiQuery.Query(
                @"\\.\ROOT\Microsoft\Windows\Storage",
                "SELECT ObjectId, DeviceId, FriendlyName, SerialNumber, MediaType, BusType, HealthStatus, OperationalStatus, Size, LogicalSectorSize, PhysicalSectorSize FROM MSFT_PhysicalDisk");
            IReadOnlyList<WmiObject> reliability = WmiQuery.Query(
                @"\\.\ROOT\Microsoft\Windows\Storage",
                "SELECT DeviceId, ReadErrorsUncorrected, WriteErrorsUncorrected, Temperature FROM MSFT_StorageReliabilityCounter");
            IReadOnlyList<WmiObject> disks = WmiQuery.Query(
                @"\\.\ROOT\Microsoft\Windows\Storage",
                "SELECT ObjectId, Number, PartitionStyle, IsBoot, IsSystem, FriendlyName, Path FROM MSFT_Disk");
            IReadOnlyList<WmiObject> partitions = WmiQuery.Query(
                @"\\.\ROOT\Microsoft\Windows\Storage",
                "SELECT DiskNumber, ObjectId, DriveLetter, AccessPaths FROM MSFT_Partition");
            IReadOnlyList<WmiObject> volumes = WmiQuery.Query(
                @"\\.\ROOT\Microsoft\Windows\Storage",
                "SELECT ObjectId, Path, FileSystem, Size, SizeRemaining, HealthStatus FROM MSFT_Volume");

            Dictionary<string, StorageReliability> reliabilityById = new(StringComparer.OrdinalIgnoreCase);
            foreach (WmiObject row in reliability)
            {
                string? id = WmiQuery.String(row, "DeviceId");
                if (id is null)
                {
                    continue;
                }

                reliabilityById[id] = new StorageReliability(
                    WmiQuery.Int64(row, "ReadErrorsUncorrected"),
                    WmiQuery.Int64(row, "WriteErrorsUncorrected"),
                    null,
                    WmiQuery.Int64(row, "Temperature"));
            }

            List<PhysicalDiskInfo> physicalDisks = [];
            foreach (WmiObject row in physical)
            {
                int? logical = WmiQuery.Int32(row, "LogicalSectorSize");
                int? physicalSector = WmiQuery.Int32(row, "PhysicalSectorSize");
                string? objectId = WmiQuery.String(row, "ObjectId");
                string? deviceId = WmiQuery.String(row, "DeviceId");
                reliabilityById.TryGetValue(deviceId ?? objectId ?? string.Empty, out StorageReliability? rel);
                physicalDisks.Add(new PhysicalDiskInfo(
                    objectId,
                    WmiQuery.String(row, "FriendlyName"),
                    WmiQuery.String(row, "SerialNumber"),
                    MediaName(WmiQuery.Int32(row, "MediaType")),
                    BusName(WmiQuery.Int32(row, "BusType")),
                    StatusName(WmiQuery.Int32(row, "HealthStatus")),
                    WmiQuery.String(row, "OperationalStatus"),
                    WmiQuery.Int64(row, "Size"),
                    logical,
                    physicalSector,
                    physicalSector == 4096 && logical == 4096,
                    physicalSector == 4096 && logical == 512,
                    rel));
            }

            List<DiskVolumeInfo> volumeList = volumes.Select(row => new DiskVolumeInfo(
                WmiQuery.String(row, "ObjectId"),
                WmiQuery.String(row, "Path"),
                WmiQuery.String(row, "FileSystem"),
                WmiQuery.Int64(row, "Size"),
                WmiQuery.Int64(row, "SizeRemaining"),
                StatusName(WmiQuery.Int32(row, "HealthStatus")))).ToList();
            Dictionary<int, List<DiskVolumeInfo>> volumesByDisk = new();
            foreach (WmiObject partition in partitions)
            {
                int? diskNumber = WmiQuery.Int32(partition, "DiskNumber");
                if (diskNumber is null)
                {
                    continue;
                }

                if (!volumesByDisk.TryGetValue(diskNumber.Value, out List<DiskVolumeInfo>? list))
                {
                    list = [];
                    volumesByDisk[diskNumber.Value] = list;
                }

                DiskVolumeInfo? match = MatchVolume(partition, volumeList);
                if (match is not null && list.All(item => item.ObjectId != match.ObjectId || item.Path != match.Path))
                {
                    list.Add(match);
                }
            }

            List<DiskInfo> diskInfos = [];
            foreach (WmiObject row in disks)
            {
                int? number = WmiQuery.Int32(row, "Number");
                volumesByDisk.TryGetValue(number ?? -1, out List<DiskVolumeInfo>? attached);
                diskInfos.Add(new DiskInfo(
                    WmiQuery.String(row, "ObjectId"),
                    number,
                    PartitionName(WmiQuery.Int32(row, "PartitionStyle")),
                    WmiQuery.Boolean(row, "IsBoot"),
                    WmiQuery.Boolean(row, "IsSystem"),
                    WmiQuery.String(row, "FriendlyName"),
                    null,
                    attached ?? []));
            }

            return OperationResult<StorageTopology>.Ok(new StorageTopology(physicalDisks, diskInfos, null));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OperationResult<StorageTopology>.Fail("storage_query_failed", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static DiskVolumeInfo? MatchVolume(WmiObject partition, IReadOnlyList<DiskVolumeInfo> volumes)
    {
        string? letter = WmiQuery.String(partition, "DriveLetter");
        string? access = WmiQuery.String(partition, "AccessPaths");
        foreach (DiskVolumeInfo volume in volumes)
        {
            if (!string.IsNullOrWhiteSpace(volume.Path)
                && access is not null
                && access.Contains(volume.Path, StringComparison.OrdinalIgnoreCase))
            {
                return volume;
            }

            if (!string.IsNullOrWhiteSpace(letter)
                && volume.Path is not null
                && volume.Path.StartsWith(letter, StringComparison.OrdinalIgnoreCase))
            {
                return volume;
            }
        }

        return null;
    }

    private static string? BusName(int? value) =>
        value is { } number && number >= 0 && number < BusNames.Length ? BusNames[number] : value?.ToString();

    private static string? MediaName(int? value) => value switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => value?.ToString()
    };

    private static string? StatusName(int? value) => value switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => value?.ToString()
    };

    private static string? PartitionName(int? value) => value switch
    {
        1 => "MBR",
        2 => "GPT",
        _ => value?.ToString()
    };
}
