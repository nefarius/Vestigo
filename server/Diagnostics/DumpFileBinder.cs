using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

using Windows.Win32.Storage.FileSystem;

namespace Vestigo.Diagnostics;

/// <summary>Creates and deletes a dump file relative to an already opened directory handle.</summary>
internal static class DumpFileBinder
{
    internal static unsafe SafeFileHandle Create(SafeFileHandle directory, string name, out int error)
    {
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
                RootDirectory = directory.DangerousGetHandle(),
                ObjectName = (IntPtr)(&unicode),
                Attributes = 0x40
            };
            IoStatusBlock statusBlock;
            uint access = (uint)FILE_ACCESS_RIGHTS.FILE_GENERIC_WRITE
                | (uint)FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES
                | (uint)FILE_ACCESS_RIGHTS.DELETE;
            int status = NtCreateFile(
                out IntPtr created,
                access,
                &attributes,
                &statusBlock,
                IntPtr.Zero,
                (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
                (uint)(FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_DELETE),
                2,
                0x00000040 | 0x00000020,
                IntPtr.Zero,
                0);
            GC.KeepAlive(directory);
            if (status < 0)
            {
                error = ErrorForStatus(status);
                return new SafeFileHandle(IntPtr.Zero, false);
            }

            error = 0;
            return new SafeFileHandle(created, ownsHandle: true);
        }
    }

    /// <summary><c>STATUS_OBJECT_NAME_COLLISION</c> is <c>ERROR_FILE_EXISTS</c> (80), which the dump writer reports as <c>already_exists</c>.</summary>
    internal static int ErrorForStatus(int status) =>
        status == unchecked((int)0xC0000035) ? 80 : (int)RtlNtStatusToDosError(status);

    internal static unsafe bool TryDelete(SafeFileHandle file)
    {
        byte delete = 1;
        return SetFileInformationByHandle(file, 4, &delete, 1);
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

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern unsafe bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass, byte* information, uint size);
}
