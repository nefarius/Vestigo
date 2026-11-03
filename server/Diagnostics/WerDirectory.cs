using System.ComponentModel;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace Vestigo.Diagnostics;

/// <summary>Opens each path component from the volume root without following a reparse point.</summary>
internal static partial class WerDirectory
{
    internal enum OpenStatus
    {
        Opened,
        Missing,
        Failed,
        Reparse
    }

    internal readonly struct OpenedDirectory(SafeFileHandle handle, string finalPath) : IDisposable
    {
        public SafeFileHandle Handle { get; } = handle;

        public string FinalPath { get; } = finalPath;

        public void Dispose() => Handle.Dispose();
    }

    internal readonly record struct Entry(string Name, long FileId, bool Directory, bool Reparse, long EndOfFile);

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorMoreData = 234;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;

    private static readonly FILE_FLAGS_AND_ATTRIBUTES NoFollow =
        FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT;

    internal static SafeFileHandle OpenChild(SafeFileHandle directory, long fileId)
    {
        unsafe
        {
            FILE_ID_DESCRIPTOR descriptor = new()
            {
                dwSize = (uint)sizeof(FILE_ID_DESCRIPTOR),
                Type = FILE_ID_TYPE.FileIdType
            };
            descriptor.FileId = fileId;
            SafeFileHandle handle = PInvoke.OpenFileById(
                directory,
                descriptor,
                (uint)FILE_ACCESS_RIGHTS.FILE_READ_DATA | (uint)FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
                null,
                NoFollow);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new Win32Exception(error);
            }

            return handle;
        }
    }

    internal static List<Entry> Children(SafeFileHandle directory)
    {
        List<Entry> children = [];
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            bool ok = PInvoke.GetFileInformationByHandleEx(directory, FILE_INFO_BY_HANDLE_CLASS.FileIdBothDirectoryInfo, buffer);
            int error = Marshal.GetLastPInvokeError();
            if (!ok)
            {
                if (error == ErrorNoMoreFiles)
                {
                    return children;
                }

                if (error == ErrorMoreData && buffer.Length < 1024 * 1024)
                {
                    buffer = new byte[buffer.Length * 2];
                    continue;
                }

                throw new Win32Exception(error);
            }

            unsafe
            {
                fixed (byte* pointer = buffer)
                {
                    byte* entry = pointer;
                    while (true)
                    {
                        FILE_ID_BOTH_DIR_INFO* info = (FILE_ID_BOTH_DIR_INFO*)entry;
                        string name = NameOf(info);
                        if (name is not "." and not "..")
                        {
                            uint attributes = info->FileAttributes;
                            children.Add(new Entry(
                                name,
                                info->FileId,
                                (attributes & FileAttributeDirectory) != 0,
                                (attributes & FileAttributeReparsePoint) != 0,
                                info->EndOfFile));
                        }

                        if (info->NextEntryOffset == 0)
                        {
                            break;
                        }

                        entry += info->NextEntryOffset;
                    }
                }
            }
        }
    }

    internal static bool IsReparse(SafeFileHandle handle) => (Attributes(handle) & FileAttributeReparsePoint) != 0;

    /// <summary>Repeats the no-follow walk and returns the verified final path.</summary>
    internal static bool TryResolve(string path, out string finalPath)
    {
        finalPath = "";
        if (Open(path, out OpenedDirectory opened) != OpenStatus.Opened)
        {
            return false;
        }

        using (opened)
        {
            finalPath = opened.FinalPath;
            return true;
        }
    }

    internal static bool SamePath(string finalPath, string configured) =>
        string.Equals(Normalize(finalPath), Normalize(configured), StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnder(string finalPath, string storeFinal)
    {
        string child = Normalize(finalPath);
        string root = Normalize(storeFinal);
        return child.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static Stream Read(SafeFileHandle file) => new HandleReadStream(file);

    private static unsafe uint Attributes(SafeFileHandle handle)
    {
        Span<byte> bytes = stackalloc byte[sizeof(FILE_ATTRIBUTE_TAG_INFO)];
        if (!PInvoke.GetFileInformationByHandleEx(handle, FILE_INFO_BY_HANDLE_CLASS.FileAttributeTagInfo, bytes))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return MemoryMarshal.Read<FILE_ATTRIBUTE_TAG_INFO>(bytes).FileAttributes;
    }

    internal static bool TryFinalPath(SafeFileHandle handle, out string? path)
    {
        char[] buffer = new char[520];
        while (true)
        {
            int length = GetFinalPathNameByHandle(handle, buffer, buffer.Length, 0);
            if (length <= 0)
            {
                path = null;
                return false;
            }

            if (length < buffer.Length)
            {
                path = new string(buffer, 0, length);
                return true;
            }

            buffer = new char[length];
        }
    }

    private static string Normalize(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
                ? @"\\" + path[@"\\?\UNC\".Length..]
                : path[4..];
        }

        string full = Path.GetFullPath(path);
        return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    private static unsafe string NameOf(FILE_ID_BOTH_DIR_INFO* info)
    {
        int characters = (int)(info->FileNameLength / 2);
        return new string(info->FileName.AsSpan(characters));
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetFinalPathNameByHandle(SafeFileHandle handle, char[] buffer, int length, uint flags);

    private sealed class HandleReadStream(SafeFileHandle handle) : Stream
    {
        private long _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = RandomAccess.Read(handle, buffer.AsSpan(offset, count), _offset);
            _offset += read;
            return read;
        }

        public override int ReadByte()
        {
            Span<byte> one = stackalloc byte[1];
            int read = RandomAccess.Read(handle, one, _offset);
            if (read == 0)
            {
                return -1;
            }

            _offset++;
            return one[0];
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
