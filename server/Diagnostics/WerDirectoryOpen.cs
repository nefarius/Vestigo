using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace Vestigo.Diagnostics;

internal static partial class WerDirectory
{
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    private const int StatusObjectPathNotFound = unchecked((int)0xC000003A);
    private const uint FileOpen = 1;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00200000;

    internal static OpenStatus Open(string path, out OpenedDirectory opened)
    {
        opened = default;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return OpenStatus.Failed;
        }

        string? volume = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(volume))
        {
            return OpenStatus.Failed;
        }

        OpenStatus status = OpenVolume(volume, out SafeFileHandle current);
        if (status != OpenStatus.Opened)
        {
            return status;
        }

        string[] parts = full.Length <= volume.Length
            ? []
            : full[volume.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            if (part is "." or "..")
            {
                current.Dispose();
                return OpenStatus.Failed;
            }

            status = OpenRelative(current, part, out SafeFileHandle next);
            current.Dispose();
            current = next;
            if (status != OpenStatus.Opened)
            {
                if (!current.IsInvalid)
                {
                    current.Dispose();
                }

                return status;
            }

            bool keep = false;
            try
            {
                if (IsReparse(current))
                {
                    return OpenStatus.Reparse;
                }

                if ((Attributes(current) & FileAttributeDirectory) == 0)
                {
                    return OpenStatus.Failed;
                }

                keep = true;
            }
            finally
            {
                if (!keep)
                {
                    current.Dispose();
                }
            }
        }

        return Finish(current, out opened);
    }

    private static OpenStatus OpenVolume(string volume, out SafeFileHandle handle)
    {
        uint access = (uint)FILE_ACCESS_RIGHTS.FILE_LIST_DIRECTORY
            | (uint)FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES
            | (uint)FILE_ACCESS_RIGHTS.FILE_TRAVERSE;
        handle = PInvoke.CreateFile(
            volume,
            access,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            NoFollow,
            null);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return error is ErrorFileNotFound or ErrorPathNotFound ? OpenStatus.Missing : OpenStatus.Failed;
        }

        bool keep = false;
        try
        {
            if (IsReparse(handle))
            {
                return OpenStatus.Reparse;
            }

            if ((Attributes(handle) & FileAttributeDirectory) == 0)
            {
                return OpenStatus.Failed;
            }

            keep = true;
            return OpenStatus.Opened;
        }
        finally
        {
            if (!keep)
            {
                handle.Dispose();
            }
        }
    }

    private static unsafe OpenStatus OpenRelative(SafeFileHandle parent, string name, out SafeFileHandle child)
    {
        child = new SafeFileHandle(IntPtr.Zero, false);
        fixed (char* namePointer = name)
        {
            UnicodeString unicode = new()
            {
                Length = (ushort)(name.Length * sizeof(char)),
                MaximumLength = (ushort)(name.Length * sizeof(char)),
                Buffer = (IntPtr)namePointer
            };
            ObjectAttributes attributes = new()
            {
                Length = (uint)sizeof(ObjectAttributes),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = (IntPtr)(&unicode),
                Attributes = 0x40
            };
            IoStatusBlock statusBlock;
            uint access = (uint)FILE_ACCESS_RIGHTS.FILE_LIST_DIRECTORY
                | (uint)FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES
                | (uint)FILE_ACCESS_RIGHTS.FILE_TRAVERSE
                | (uint)FILE_ACCESS_RIGHTS.SYNCHRONIZE;
            int status = NtCreateFile(
                out IntPtr created,
                access,
                &attributes,
                &statusBlock,
                IntPtr.Zero,
                0,
                (uint)(FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE),
                FileOpen,
                FileDirectoryFile | FileOpenReparsePoint | FileSynchronousIoNonAlert,
                IntPtr.Zero,
                0);
            GC.KeepAlive(parent);
            if (status < 0)
            {
                return status is StatusObjectNameNotFound or StatusObjectPathNotFound ? OpenStatus.Missing : OpenStatus.Failed;
            }

            child = new SafeFileHandle(created, ownsHandle: true);
            return OpenStatus.Opened;
        }
    }

    private static OpenStatus Finish(SafeFileHandle handle, out OpenedDirectory opened)
    {
        opened = default;
        bool owned = true;
        try
        {
            if (IsReparse(handle))
            {
                return OpenStatus.Reparse;
            }

            if ((Attributes(handle) & FileAttributeDirectory) == 0
                || !TryFinalPath(handle, out string? final)
                || string.IsNullOrEmpty(final))
            {
                return OpenStatus.Failed;
            }

            opened = new OpenedDirectory(handle, final);
            owned = false;
            return OpenStatus.Opened;
        }
        finally
        {
            if (owned)
            {
                handle.Dispose();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("ntdll.dll")]
    private static extern unsafe int NtCreateFile(
        out IntPtr fileHandle,
        uint desiredAccess,
        ObjectAttributes* objectAttributes,
        IoStatusBlock* ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);
}
