using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.Win32.SafeHandles;

using Vestigo.Configuration;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Security.Authorization;
using Windows.Win32.Storage.FileSystem;

namespace Vestigo.Security;

/// <summary>
///     Creates and verifies the machine-wide state tree (settings, logs, spool, symbol cache). On Windows the tree
///     gets a protected DACL that only SYSTEM and Administrators (plus the current account when it is not SYSTEM)
///     can change, which also stops a lower-privileged user from swapping a path component for a link. Reparse
///     points and untrusted owners fail closed.
/// </summary>
public static class StateDirectories
{
    private const FileSystemRights DirectoryDangerous =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.DeleteSubdirectoriesAndFiles | AclTrust.Control;

    private const FileSystemRights AncestorDangerous = FileSystemRights.DeleteSubdirectoriesAndFiles | AclTrust.Control;

    /// <summary>
    ///     Creates or verifies the state tree. Returns one message per repair so the caller can log them once logging
    ///     is available. Throws <see cref="InvalidOperationException" /> when the tree cannot be trusted.
    /// </summary>
    public static IReadOnlyList<string> EnsureSecure()
    {
        List<string> repairs = [];
        string root = Path.GetFullPath(VestigoPaths.SettingsDirectory);
        if (FindReparsePoint(root) is { } link)
        {
            throw Unsafe($"'{link}' is a reparse point (link or junction), which Vestigo does not follow");
        }

        string[] directories =
        [
            root,
            Path.GetFullPath(VestigoPaths.LogDirectory),
            Path.GetFullPath(VestigoPaths.SpoolDirectory),
            Path.GetFullPath(VestigoPaths.DefaultSymbolCache)
        ];

        if (OperatingSystem.IsWindows())
        {
            string? extra = CurrentUserSid();
            if (!VestigoPaths.IsSettingsDirectoryOverridden)
            {
                // An explicit VESTIGO_SETTINGS_DIR is an operator or test choice outside ProgramData.
                CheckAncestors(root, extra);
            }

            foreach (string directory in directories)
            {
                SecureWindowsDirectory(directory, extra, repairs);
            }
        }
        else
        {
            foreach (string directory in directories)
            {
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                if (FindReparsePoint(directory) is { } found)
                {
                    throw Unsafe($"'{found}' is a reparse point (link or junction), which Vestigo does not follow");
                }
            }
        }

        return repairs;
    }

    /// <summary>
    ///     Creates a directory inside the settings tree, refusing paths outside it and any link along the path.
    ///     Returns the full path.
    /// </summary>
    public static string EnsureSubdirectory(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(VestigoPaths.SettingsDirectory));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string trimmed = Path.TrimEndingDirectorySeparator(full);
        if (!trimmed.Equals(root, comparison) && !full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw Unsafe($"'{full}' is outside the Vestigo state directory");
        }

        if (FindReparsePoint(full) is { } before)
        {
            throw Unsafe($"'{before}' is a reparse point (link or junction), which Vestigo does not follow");
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(full);
        }
        else
        {
            Directory.CreateDirectory(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        if (FindReparsePoint(full) is { } after)
        {
            throw Unsafe($"'{after}' is a reparse point (link or junction), which Vestigo does not follow");
        }

        if (OperatingSystem.IsWindows())
        {
            RequireTrustedOwners(root, full, CurrentUserSid());
        }

        return full;
    }

    /// <summary>Returns the first existing component (from the root down) that is a reparse point, or null.</summary>
    internal static string? FindReparsePoint(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        string current = root;
        foreach (string part in full[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return current;
            }
        }

        return null;
    }

    private static InvalidOperationException Unsafe(string reason) =>
        new($"Refusing to use the Vestigo state directory: {reason}. Remove the link or repair the directory permissions, then restart the service.");

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? CurrentUserSid()
    {
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        return sid == "S-1-5-18" ? null : sid;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CheckAncestors(string root, string? extra)
    {
        DirectoryInfo? ancestor = new DirectoryInfo(root).Parent;
        while (ancestor is not null)
        {
            if (ancestor.Exists)
            {
                string? reason;
                try
                {
                    reason = AclTrust.Untrusted(ancestor.GetAccessControl(), AncestorDangerous, extra);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    throw Unsafe($"permissions of '{ancestor.FullName}' could not be read ({ex.GetType().Name})");
                }

                if (reason is not null)
                {
                    throw Unsafe($"parent directory '{ancestor.FullName}' {reason}");
                }
            }

            ancestor = ancestor.Parent;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static DirectorySecurity ProtectedSecurity(string? extra)
    {
        DirectorySecurity security = new();
        security.SetAccessRuleProtection(true, false);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        if (extra is not null)
        {
            AddFullControl(security, new SecurityIdentifier(extra));
        }

        return security;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddFullControl(DirectorySecurity security, SecurityIdentifier sid) =>
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

    [System.Runtime.Versioning.SupportedOSPlatform("windows6.0.6000")]
    private static void SecureWindowsDirectory(string path, string? extra, List<string> repairs)
    {
        DirectoryInfo info = new(path);
        if (!info.Exists)
        {
            info.Create(ProtectedSecurity(extra));
            return;
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Unsafe($"'{path}' is a reparse point (link or junction), which Vestigo does not follow");
        }

        DirectorySecurity security = info.GetAccessControl();
        if (!AclTrust.IsOwnerTrusted(security, extra))
        {
            throw Unsafe($"'{path}' is owned by an untrusted principal");
        }

        string? writer = AclTrust.FindUntrustedWriter(security, DirectoryDangerous, extra);
        if (security.AreAccessRulesProtected && writer is null)
        {
            return;
        }

        info.SetAccessControl(ProtectedSecurity(extra));
        ResetChildren(info, extra);
        repairs.Add($"Reset permissions of '{path}' to SYSTEM and Administrators only" +
                    (writer is null ? " (inheritance was enabled)." : $" (it was writable by {writer})."));
    }

    /// <summary>Requires a trusted owner for the root and every component down to <paramref name="full" />.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RequireTrustedOwners(string root, string full, string? extra)
    {
        string current = root;
        foreach (string part in full[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Prepend(string.Empty))
        {
            current = part.Length == 0 ? current : Path.Combine(current, part);
            DirectoryInfo info = new(current);
            if (!info.Exists)
            {
                continue;
            }

            bool trusted;
            try
            {
                trusted = AclTrust.IsOwnerTrusted(info.GetAccessControl(), extra);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                throw Unsafe($"permissions of '{current}' could not be read ({ex.GetType().Name})");
            }

            if (!trusted)
            {
                throw Unsafe($"'{current}' is owned by an untrusted principal");
            }
        }
    }

    /// <summary>
    ///     Resets descendants through handles opened with backup semantics and without following links, so a reparse
    ///     point swapped in after the name was listed cannot redirect the ACL write. Children are opened by file id
    ///     from the parent handle.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows6.0.6000")]
    private static void ResetChildren(DirectoryInfo directory, string? extra)
    {
        SafeFileHandle handle;
        try
        {
            handle = OpenNoFollow(directory.FullName, asDirectory: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw Unsafe($"permissions of '{directory.FullName}' could not be reset ({ex.GetType().Name})");
        }

        using (handle)
        {
            if (IsReparsePoint(handle))
            {
                throw Unsafe($"'{directory.FullName}' is a reparse point (link or junction), which Vestigo does not follow");
            }

            ResetOpened(handle, directory.FullName, extra);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows6.0.6000")]
    private static void ResetOpened(SafeFileHandle directory, string displayPath, string? extra)
    {
        foreach ((string name, long fileId) in ListChildren(directory))
        {
            string childPath = Path.Combine(displayPath, name);
            string? rejection = null;
            try
            {
                using SafeFileHandle child = OpenById(directory, fileId);
                FileAttributes attributes = AttributesOf(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    rejection = $"'{childPath}' is a reparse point (link or junction), which Vestigo does not follow";
                }
                else if (!OwnerTrusted(child, extra))
                {
                    rejection = $"'{childPath}' is owned by an untrusted principal";
                }
                else
                {
                    ApplyInheritedDacl(child);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        ResetOpened(child, childPath, extra);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                throw Unsafe($"permissions of '{childPath}' could not be reset ({ex.GetType().Name})");
            }

            if (rejection is not null)
            {
                throw Unsafe(rejection);
            }
        }
    }

    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorMoreData = 234;

    private static readonly FILE_FLAGS_AND_ATTRIBUTES NoFollow =
        FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT;

    [System.Runtime.Versioning.SupportedOSPlatform("windows6.0.6000")]
    private static unsafe SafeFileHandle OpenNoFollow(string path, bool asDirectory)
    {
        uint access = (uint)FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES | ReadControl | WriteDac;
        if (asDirectory)
        {
            access |= (uint)FILE_ACCESS_RIGHTS.FILE_LIST_DIRECTORY;
        }

        SafeFileHandle handle = PInvoke.CreateFile(
            path,
            access,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            lpSecurityAttributes: null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            NoFollow,
            hTemplateFile: null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return handle;
    }

    /// <summary>Opens the file id on the same volume as <paramref name="directory" /> without following a link.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows6.0.6000")]
    private static unsafe SafeFileHandle OpenById(SafeFileHandle directory, long fileId)
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
            (uint)FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES | (uint)FILE_ACCESS_RIGHTS.FILE_LIST_DIRECTORY | ReadControl | WriteDac,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            lpSecurityAttributes: null,
            NoFollow);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return handle;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows6.0.6000")]
    private static unsafe FileAttributes AttributesOf(SafeFileHandle handle)
    {
        Span<byte> bytes = stackalloc byte[sizeof(FILE_ATTRIBUTE_TAG_INFO)];
        if (!PInvoke.GetFileInformationByHandleEx(handle, FILE_INFO_BY_HANDLE_CLASS.FileAttributeTagInfo, bytes))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return (FileAttributes)MemoryMarshal.Read<FILE_ATTRIBUTE_TAG_INFO>(bytes).FileAttributes;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows6.0.6000")]
    private static unsafe bool IsReparsePoint(SafeFileHandle handle) =>
        (AttributesOf(handle) & FileAttributes.ReparsePoint) != 0;

    [System.Runtime.Versioning.SupportedOSPlatform("windows6.0.6000")]
    private static unsafe bool OwnerTrusted(SafeFileHandle handle, string? extra)
    {
        WIN32_ERROR error = PInvoke.GetSecurityInfo(
            handle,
            SE_OBJECT_TYPE.SE_FILE_OBJECT,
            OBJECT_SECURITY_INFORMATION.OWNER_SECURITY_INFORMATION,
            out PSID owner,
            out _,
            out _,
            out _,
            out PSECURITY_DESCRIPTOR descriptor);
        if (error != WIN32_ERROR.ERROR_SUCCESS)
        {
            throw new Win32Exception((int)error);
        }

        try
        {
            return AclTrust.IsTrusted(new SecurityIdentifier((IntPtr)owner.Value).Value, extra);
        }
        finally
        {
            PInvoke.LocalFree(new HLOCAL(descriptor.Value));
        }
    }

    /// <summary>Replaces the DACL with an unprotected empty one so the entry inherits the parent.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows6.0.6000")]
    private static unsafe void ApplyInheritedDacl(SafeFileHandle handle)
    {
        FileSecurity security = new();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        byte[] binary = security.GetSecurityDescriptorBinaryForm();
        fixed (byte* sd = binary)
        {
            if (!PInvoke.GetSecurityDescriptorDacl(new PSECURITY_DESCRIPTOR(sd), out BOOL present, out ACL* dacl, out _) || !present || dacl is null)
            {
                throw new IOException("Could not build an inheriting DACL.");
            }

            // The SafeHandle overload copies ACL by value and would drop the ACEs that follow the header.
            bool referenced = false;
            handle.DangerousAddRef(ref referenced);
            try
            {
                WIN32_ERROR error = PInvoke.SetSecurityInfo(
                    new HANDLE(handle.DangerousGetHandle()),
                    SE_OBJECT_TYPE.SE_FILE_OBJECT,
                    OBJECT_SECURITY_INFORMATION.DACL_SECURITY_INFORMATION | OBJECT_SECURITY_INFORMATION.UNPROTECTED_DACL_SECURITY_INFORMATION,
                    PSID.Null,
                    PSID.Null,
                    dacl,
                    null);
                if (error != WIN32_ERROR.ERROR_SUCCESS)
                {
                    throw new Win32Exception((int)error);
                }
            }
            finally
            {
                if (referenced)
                {
                    handle.DangerousRelease();
                }
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows6.0.6000")]
    private static unsafe List<(string Name, long FileId)> ListChildren(SafeFileHandle directory)
    {
        List<(string Name, long FileId)> children = [];
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            bool ok = PInvoke.GetFileInformationByHandleEx(
                directory,
                FILE_INFO_BY_HANDLE_CLASS.FileIdBothDirectoryInfo,
                buffer);
            int error = Marshal.GetLastPInvokeError();

            if (!ok)
            {
                if (error == ErrorNoMoreFiles)
                {
                    break;
                }

                if (error == ErrorMoreData && buffer.Length < 1024 * 1024)
                {
                    buffer = new byte[buffer.Length * 2];
                    continue;
                }

                throw new Win32Exception(error);
            }

            fixed (byte* pointer = buffer)
            {
                byte* entry = pointer;
                while (true)
                {
                    FILE_ID_BOTH_DIR_INFO* info = (FILE_ID_BOTH_DIR_INFO*)entry;
                    string name = NameOf(info);
                    if (name is not "." and not "..")
                    {
                        children.Add((name, info->FileId));
                    }

                    if (info->NextEntryOffset == 0)
                    {
                        break;
                    }

                    entry += info->NextEntryOffset;
                }
            }
        }

        return children;
    }

    private static unsafe string NameOf(FILE_ID_BOTH_DIR_INFO* info)
    {
        int characters = (int)(info->FileNameLength / 2);
        return new string(info->FileName.AsSpan(characters));
    }

}
