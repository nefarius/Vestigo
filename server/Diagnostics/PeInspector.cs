namespace Vestigo.Diagnostics;

/// <summary>Reads PE metadata through PeNet from one open of the file.</summary>
public sealed class PeInspector : IPeInspector
{
    public OperationResult<BinaryInspection> Inspect(string fullPath, bool computeSha256, long maxBytes)
    {
        try
        {
            using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maxBytes)
            {
                return OperationResult<BinaryInspection>.Fail("file_too_large", "The file exceeds Vestigo:Diagnostics:MaxInspectBytes.");
            }

            return PeImage.Inspect(stream, computeSha256);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult<BinaryInspection>.Fail("io_error", "The file could not be read.");
        }
    }
}
