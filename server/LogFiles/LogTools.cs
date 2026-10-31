using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.LogFiles;

/// <summary>MCP tools that read the end of text log files inside configured roots.</summary>
[McpServerToolType]
public sealed class LogTools(VestigoOptions options, RootPolicy roots)
{
    [McpServerTool(Name = "tail_text_file")]
    [Description("Returns the last lines of a text file under a root with Read access. The file is opened with shared access so active logs can be read. Encoding is detected from a UTF-8 or UTF-16 byte order mark, otherwise UTF-8. Returns a cursor for read_text_file_since.")]
    public OperationResult<TextSlice> TailTextFile(
        [Description("File path: root:relative\\path or an absolute path inside a root.")] string path,
        [Description("Number of lines to return. Clamped to MaxTailLines.")] int lines = 100,
        [Description("Maximum bytes to examine from the end of the file. Clamped to MaxTailBytes.")] int? maxBytes = null) =>
        Run(path, (stream, identity) => TextTailer.Tail(
            stream,
            identity,
            Math.Clamp(lines, 1, options.Logs.MaxTailLines),
            ClampBytes(maxBytes)));

    [McpServerTool(Name = "read_text_file_since")]
    [Description("Returns complete lines after a cursor from tail_text_file or an earlier call. If the file shrank or was replaced, reading restarts at the beginning and rotated is true. An unterminated last line is held back until it is completed. Continue while hasMore is true.")]
    public OperationResult<TextSlice> ReadTextFileSince(
        [Description("File path: root:relative\\path or an absolute path inside a root.")] string path,
        [Description("Cursor offset returned by the previous call. Use 0 to start at the beginning.")] long offset,
        [Description("Cursor identity returned by the previous call. Omit or 0 to skip the replacement check.")] long identity = 0,
        [Description("Maximum lines to return. Clamped to MaxTailLines.")] int? maxLines = null,
        [Description("Maximum bytes to examine. Clamped to MaxTailBytes.")] int? maxBytes = null) =>
        Run(path, (stream, currentIdentity) => TextTailer.ReadSince(
            stream,
            currentIdentity,
            new TextCursor(offset, 0, identity),
            Math.Clamp(maxLines ?? options.Logs.MaxTailLines, 1, options.Logs.MaxTailLines),
            ClampBytes(maxBytes)));

    private int ClampBytes(int? requested) => Math.Clamp(requested ?? options.Logs.MaxTailBytes, 16, options.Logs.MaxTailBytes);

    private OperationResult<TextSlice> Run(string path, Func<FileStream, long, TextSlice> read)
    {
        PathCheck check = roots.ResolveExisting(path, RootAccess.Read);
        if (!check.Success)
        {
            return OperationResult<TextSlice>.Fail(check.ErrorCode!, check.Error!);
        }

        if (!File.Exists(check.FullPath))
        {
            return OperationResult<TextSlice>.Fail("not_a_file", "Path is not a file.");
        }

        try
        {
            using FileStream stream = new(
                check.FullPath!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            long identity = File.GetCreationTimeUtc(check.FullPath!).Ticks;
            return OperationResult<TextSlice>.Ok(read(stream, identity));
        }
        catch (FileNotFoundException)
        {
            return OperationResult<TextSlice>.Fail("not_found", "File disappeared while reading.");
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult<TextSlice>.Fail("access_denied", "Windows denied access to the file.");
        }
        catch (IOException exception)
        {
            return OperationResult<TextSlice>.Fail("io_error", exception.Message);
        }
    }
}
