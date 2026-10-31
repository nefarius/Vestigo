using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Vestigo.Software;

/// <summary>
///     Decides whether an uninstaller may be run as the service account. A file or directory that a
///     lower-privileged principal can modify, replace, or re-ACL lets that principal run code as SYSTEM, so the
///     file, its directory, and every ancestor must be controlled only by SYSTEM, Administrators, or
///     TrustedInstaller. Anything unreadable fails closed.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ExecutableTrust
{
    private static readonly HashSet<string> TrustedSids = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18", // SYSTEM
        "S-1-5-32-544", // Administrators
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" // TrustedInstaller
    };

    private const FileSystemRights Control =
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.Delete;

    private const FileSystemRights FileWrite = FileSystemRights.WriteData | FileSystemRights.AppendData | Control;

    // Adding files next to the uninstaller allows DLL planting; deleting children allows replacing them.
    private const FileSystemRights DirectoryWrite = FileSystemRights.WriteData | FileSystemRights.DeleteSubdirectoriesAndFiles | Control;

    private const FileSystemRights AncestorWrite = FileSystemRights.DeleteSubdirectoriesAndFiles | Control;

    /// <summary>Returns null when the file is trusted, otherwise the reason it is not.</summary>
    public static string? Check(string executable)
    {
        if (!InstallerDetector.IsSafeLocalPath(executable))
        {
            return "the path is not a plain location on a fixed local drive";
        }

        try
        {
            string full = Path.GetFullPath(executable);
            FileInfo file = new(full);
            if (!file.Exists)
            {
                return "the file does not exist";
            }

            if (Untrusted(file.GetAccessControl(), FileWrite) is { } fileReason)
            {
                return $"the file {fileReason}";
            }

            DirectoryInfo? directory = file.Directory;
            bool immediate = true;
            while (directory is not null)
            {
                if (Untrusted(directory.GetAccessControl(), immediate ? DirectoryWrite : AncestorWrite) is { } reason)
                {
                    return $"directory '{directory.FullName}' {reason}";
                }

                immediate = false;
                directory = directory.Parent;
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return "its permissions could not be read (" + ex.GetType().Name + ")";
        }
    }

    private static string? Untrusted(FileSystemSecurity security, FileSystemRights dangerous)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !TrustedSids.Contains(owner.Value))
        {
            return "is owned by an untrusted principal";
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow
                || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)
                || (rule.FileSystemRights & dangerous) == 0
                || rule.IdentityReference is not SecurityIdentifier sid)
            {
                continue;
            }

            if (!TrustedSids.Contains(sid.Value) && sid.Value != "S-1-3-0")
            {
                return $"is writable by {sid.Value}";
            }
        }

        return null;
    }
}
