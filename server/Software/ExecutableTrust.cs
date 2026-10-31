using System.Runtime.Versioning;
using System.Security.AccessControl;

using Vestigo.Security;

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
    private const FileSystemRights Control = AclTrust.Control;

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

            if (AclTrust.Untrusted(file.GetAccessControl(), FileWrite) is { } fileReason)
            {
                return $"the file {fileReason}";
            }

            DirectoryInfo? directory = file.Directory;
            bool immediate = true;
            while (directory is not null)
            {
                if (AclTrust.Untrusted(directory.GetAccessControl(), immediate ? DirectoryWrite : AncestorWrite) is { } reason)
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
}
