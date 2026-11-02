using System.IO.Compression;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Etws;

/// <summary>Extracts PDB, TMF, and ETL files from a zip into the allowed trace roots.</summary>
public sealed class TraceArchiveExtractor(VestigoOptions options, PathPolicy paths)
{
    private static readonly string[] ArchiveExtensions = [".zip"];
    private static readonly string[] ExtractExtensions = [".pdb", ".tmf", ".etl"];
    private const int MaxCompressionRatio = 200;

    public OperationResult<TraceArchiveExtraction> Extract(string archivePath, string destination, bool overwrite)
    {
        PathCheck archive = paths.RequireExistingFile(archivePath, ArchiveExtensions);
        if (!archive.Success)
        {
            return OperationResult<TraceArchiveExtraction>.Fail(archive.ErrorCode!, archive.Error!);
        }

        PathCheck folder = paths.RequireWritableDirectory(destination);
        if (!folder.Success)
        {
            return OperationResult<TraceArchiveExtraction>.Fail(folder.ErrorCode!, folder.Error!);
        }

        try
        {
            using FileStream stream = new(archive.FullPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
            using ZipArchive zip = new(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (zip.Entries.Count > options.MaxArchiveEntries)
            {
                return OperationResult<TraceArchiveExtraction>.Fail(
                    "archive_too_large",
                    $"The archive has more than {options.MaxArchiveEntries} entries.");
            }

            long compressedTotal = 0;
            long uncompressedTotal = 0;
            List<string> extracted = [];
            List<string> skipped = [];
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    continue;
                }

                if (!PathPolicy.HasExtension(entry.Name, ExtractExtensions))
                {
                    skipped.Add(entry.FullName);
                    continue;
                }

                if (IsUnsafeEntry(entry.FullName))
                {
                    return OperationResult<TraceArchiveExtraction>.Fail(
                        "unsafe_entry",
                        $"Archive entry '{entry.FullName}' is not a safe relative path.");
                }

                if (entry.Length > options.MaxInputBytes)
                {
                    return OperationResult<TraceArchiveExtraction>.Fail(
                        "archive_too_large",
                        $"Archive entry '{entry.FullName}' exceeds the input size limit.");
                }

                compressedTotal += Math.Max(entry.CompressedLength, 1);
                uncompressedTotal += entry.Length;
                if (uncompressedTotal > options.MaxInputBytes)
                {
                    return OperationResult<TraceArchiveExtraction>.Fail(
                        "archive_too_large",
                        "The uncompressed archive exceeds the input size limit.");
                }

                if (uncompressedTotal / compressedTotal > MaxCompressionRatio)
                {
                    return OperationResult<TraceArchiveExtraction>.Fail(
                        "archive_too_large",
                        "The archive compression ratio exceeds 200:1.");
                }

                string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                PathCheck target = paths.RequireOutputFile(
                    Path.Combine(folder.FullPath!, relative),
                    ExtractExtensions,
                    overwrite);
                if (!target.Success)
                {
                    return OperationResult<TraceArchiveExtraction>.Fail(target.ErrorCode!, target.Error!);
                }

                if (IsLink(target.FullPath!))
                {
                    return OperationResult<TraceArchiveExtraction>.Fail(
                        "invalid_path",
                        $"Refusing to write through a link: '{target.FullPath}'.");
                }

                string? parent = Path.GetDirectoryName(target.FullPath);
                if (!string.IsNullOrEmpty(parent) && IsLink(parent))
                {
                    return OperationResult<TraceArchiveExtraction>.Fail(
                        "invalid_path",
                        $"Refusing to write through a link: '{parent}'.");
                }

                entry.ExtractToFile(target.FullPath!, overwrite: true);
                extracted.Add(target.FullPath!);
            }

            return OperationResult<TraceArchiveExtraction>.Ok(
                new TraceArchiveExtraction(folder.FullPath!, extracted, skipped));
        }
        catch (InvalidDataException ex)
        {
            return OperationResult<TraceArchiveExtraction>.Fail("archive_invalid", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return OperationResult<TraceArchiveExtraction>.Fail("archive_invalid", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static bool IsUnsafeEntry(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        string normalized = name.Replace('/', '\\');
        if (normalized.StartsWith('\\') ||
            normalized.Contains("..", StringComparison.Ordinal) ||
            normalized.Contains(':', StringComparison.Ordinal) ||
            Path.IsPathRooted(normalized))
        {
            return true;
        }

        foreach (string part in normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or ".." || part.Contains(':'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLink(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget is not null || new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
