using PeNet;
using PeNet.FileParser;
using PeNet.Header.Pe;
using PeNet.Header.Resource;

namespace Vestigo.Diagnostics;

/// <summary>PE facts read through PeNet. PDB paths are reduced to a file name.</summary>
internal static class PeImage
{
    public static OperationResult<BinaryInspection> Inspect(Stream stream, bool computeSha256)
    {
        if (!PeFile.IsPeFile(stream))
        {
            return OperationResult<BinaryInspection>.Fail("not_pe", "The file is not a PE image.");
        }

        if (!PeFile.TryParse(stream, out PeFile? pe) || pe is null || pe.ImageNtHeaders?.OptionalHeader is null)
        {
            return OperationResult<BinaryInspection>.Fail("malformed_pe", "The PE headers are malformed.");
        }

        ImageDebugDirectory? codeView = pe.ImageDebugDirectory?.FirstOrDefault(entry => entry.CvInfoPdb70 is not null);
        string? pdbPath = codeView?.CvInfoPdb70?.PdbFileName;
        string? pdbName = string.IsNullOrWhiteSpace(pdbPath) ? null : Path.GetFileName(pdbPath.Replace('\\', '/'));
        (string? fileVersion, string? productVersion) = Versions(pe);
        string? hash = computeSha256 ? pe.Sha256?.ToUpperInvariant() : null;

        return OperationResult<BinaryInspection>.Ok(new BinaryInspection(
            Architecture(pe),
            pe.Is64Bit ? "PE32+" : "PE32",
            SubsystemName(pe.ImageNtHeaders.OptionalHeader.Subsystem),
            Characteristics(pe.ImageNtHeaders.OptionalHeader.DllCharacteristics),
            Timestamp(pe.ImageNtHeaders.FileHeader.TimeDateStamp),
            pe.IsDotNet,
            pdbName,
            codeView?.CvInfoPdb70?.Signature.ToString("D"),
            codeView?.CvInfoPdb70 is { } cv ? (int)cv.Age : null,
            fileVersion,
            productVersion,
            pe.FileSize,
            hash,
            DateTimeOffset.UtcNow));
    }

    public static (string? Architecture, string? FileVersion, string? ProductVersion) TryMetadata(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!PeFile.TryParse(stream, out PeFile? pe) || pe is null)
            {
                return (null, null, null);
            }

            (string? fileVersion, string? productVersion) = Versions(pe);
            return (Architecture(pe), fileVersion, productVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null, null);
        }
    }

    private static string Architecture(PeFile pe)
    {
        bool hybrid = HasHybridMetadata(pe);
        return (pe.ImageNtHeaders?.FileHeader.Machine, hybrid) switch
        {
            (MachineType.I386, _) => "x86",
            (MachineType.Amd64, false) => "x64",
            (MachineType.Amd64, true) => "arm64ec",
            (MachineType.Arm64, false) => "arm64",
            (MachineType.Arm64, true) => "arm64x",
            (MachineType.ArmNt, _) => "armnt",
            _ => "unknown"
        };
    }

    private static bool HasHybridMetadata(PeFile pe)
    {
        ImageDataDirectory[]? directories = pe.ImageNtHeaders?.OptionalHeader.DataDirectory;
        if (!pe.Is64Bit || directories is null || directories.Length <= (int)DataDirectoryType.LoadConfig)
        {
            return false;
        }

        ImageDataDirectory directory = directories[(int)DataDirectoryType.LoadConfig];
        if (directory.VirtualAddress == 0 || directory.Size < 208 || !TryFileOffset(pe, directory.VirtualAddress, out long offset))
        {
            return false;
        }

        try
        {
            return pe.RawFile.ReadULong(offset + 200) != 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static bool TryFileOffset(PeFile pe, uint rva, out long offset)
    {
        offset = 0;
        if (pe.ImageSectionHeaders is null)
        {
            return false;
        }

        foreach (ImageSectionHeader section in pe.ImageSectionHeaders)
        {
            uint size = Math.Max(section.VirtualSize, section.SizeOfRawData);
            if (rva < section.VirtualAddress || rva >= section.VirtualAddress + size || section.PointerToRawData == 0)
            {
                continue;
            }

            offset = section.PointerToRawData + (rva - section.VirtualAddress);
            return true;
        }

        return false;
    }

    private static (string? FileVersion, string? ProductVersion) Versions(PeFile pe)
    {
        try
        {
            // VsFixedFileInfo reads SzKey until a UTF-16 NUL and does not stop at the resource size.
            if (!VersionResourceFits(pe))
            {
                return (null, null);
            }

            var info = pe.Resources?.VsVersionInfo?.VsFixedFileInfo;
            if (info is null || info.DwSignature != 0xFEEF04BD)
            {
                return (null, null);
            }

            return (Quad(info.DwFileVersionMS, info.DwFileVersionLS), Quad(info.DwProductVersionMS, info.DwProductVersionLS));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or NullReferenceException or IOException)
        {
            return (null, null);
        }
    }

    private static bool VersionResourceFits(PeFile pe)
    {
        const int minimumBytes = 92;
        ImageResourceDirectory? root = pe.ImageResourceDirectory;
        ImageSectionHeader[]? sections = pe.ImageSectionHeaders;
        if (root?.DirectoryEntries is null || sections is null)
        {
            return false;
        }

        foreach (ImageResourceDirectoryEntry? typeEntry in root.DirectoryEntries)
        {
            if (typeEntry is null || typeEntry.ID != (uint)ResourceGroupIdType.Version)
            {
                continue;
            }

            foreach (ImageResourceDirectoryEntry? nameEntry in typeEntry.ResourceDirectory?.DirectoryEntries ?? [])
            {
                if (nameEntry?.ResourceDirectory?.DirectoryEntries is not { } languages)
                {
                    continue;
                }

                foreach (ImageResourceDirectoryEntry? languageEntry in languages)
                {
                    ImageResourceDataEntry? data = languageEntry?.ResourceDataEntry;
                    if (data is null || !data.OffsetToData.TryRvaToOffset(sections, out uint offset))
                    {
                        continue;
                    }

                    return ResourceFits(pe.RawFile, offset, data.Size1, minimumBytes);
                }
            }
        }

        return false;
    }

    private static bool ResourceFits(IRawFile file, uint offset, uint size, int minimumBytes)
    {
        if (size < minimumBytes || (ulong)offset + size > (ulong)file.Length)
        {
            return false;
        }

        ushort length = file.ReadUShort(offset);
        ushort valueLength = file.ReadUShort(offset + 2);
        if (length < minimumBytes || length > size || valueLength < 52 || valueLength > length)
        {
            return false;
        }

        int scan = Math.Min(64, length - 6);
        long key = offset + 6;
        if (scan < 2 || key > file.Length - scan)
        {
            return false;
        }

        for (int index = 0; index + 1 < scan; index += 2)
        {
            if (file.ReadUShort(key + index) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string Quad(uint high, uint low) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{high >> 16}.{(ushort)high}.{low >> 16}.{(ushort)low}");

    private static DateTimeOffset? Timestamp(uint stamp) =>
        stamp == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(stamp);

    private static string? SubsystemName(SubsystemType subsystem) => subsystem switch
    {
        SubsystemType.Unknown => null,
        SubsystemType.Native => "native",
        SubsystemType.WindowsGui => "windowsGui",
        SubsystemType.WindowsCui => "windowsCui",
        SubsystemType.WindowsCeGui => "windowsCeGui",
        SubsystemType.EfiApplication => "efiApplication",
        SubsystemType.EfiBootServiceDriver => "efiBoot",
        SubsystemType.EfiRuntimeDriver => "efiRuntime",
        SubsystemType.EfiRom => "efiRom",
        SubsystemType.Xbox => "xbox",
        SubsystemType.WindowsBootApplication => "windowsBootApplication",
        _ => ((ushort)subsystem).ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    private static IReadOnlyList<string> Characteristics(DllCharacteristicsType flags)
    {
        List<string> names = [];
        if (flags.HasFlag(DllCharacteristicsType.DynamicBase))
        {
            names.Add("dynamicBase");
        }

        if (flags.HasFlag(DllCharacteristicsType.NxCompat))
        {
            names.Add("nx");
        }

        if (flags.HasFlag(DllCharacteristicsType.GuardCF))
        {
            names.Add("controlFlowGuard");
        }

        if (flags.HasFlag(DllCharacteristicsType.HighEntropyVA))
        {
            names.Add("highEntropyVa");
        }

        return names;
    }
}
