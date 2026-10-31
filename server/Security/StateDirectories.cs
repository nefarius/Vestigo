using System.Security.AccessControl;
using System.Security.Principal;

using Vestigo.Configuration;

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

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
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

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ResetChildren(DirectoryInfo directory, string? extra)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Unsafe($"'{entry.FullName}' is a reparse point (link or junction), which Vestigo does not follow");
            }

            bool trusted;
            try
            {
                if (entry is DirectoryInfo child)
                {
                    DirectorySecurity security = child.GetAccessControl();
                    trusted = AclTrust.IsOwnerTrusted(security, extra);
                    if (trusted)
                    {
                        Inherit(security);
                        child.SetAccessControl(security);
                    }
                }
                else
                {
                    FileSecurity security = ((FileInfo)entry).GetAccessControl();
                    trusted = AclTrust.IsOwnerTrusted(security, extra);
                    if (trusted)
                    {
                        Inherit(security);
                        ((FileInfo)entry).SetAccessControl(security);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                throw Unsafe($"permissions of '{entry.FullName}' could not be reset ({ex.GetType().Name})");
            }

            if (!trusted)
            {
                throw Unsafe($"'{entry.FullName}' is owned by an untrusted principal");
            }

            if (entry is DirectoryInfo directoryEntry)
            {
                ResetChildren(directoryEntry, extra);
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Inherit(FileSystemSecurity security)
    {
        security.SetAccessRuleProtection(false, false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRuleSpecific(rule);
        }
    }
}
