using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Plugins;

/// <summary>Read-only validation. Never creates or repairs an executable-code directory.</summary>
internal static class PluginDeployment
{
    internal static void RequireSafePath(string path)
    {
        if (StateDirectories.FindReparsePoint(path) is { } link)
            throw new PluginValidationException($"Deployment path '{link}' is a reparse point.");
    }

    internal static IReadOnlyList<string> ValidateTree(string directory)
    {
        RequireSafePath(directory);
        List<string> files = [];
        Visit(new DirectoryInfo(directory));
        return files.Order(StringComparer.Ordinal).ToArray();

        void Visit(DirectoryInfo current)
        {
            RequireSafePath(current.FullName);
            RequireAcl(current, directory: true);
            foreach (FileSystemInfo child in current.EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new PluginValidationException($"Deployment path '{child.FullName}' is a reparse point.");
                if (child is DirectoryInfo subdirectory) Visit(subdirectory);
                else
                {
                    RequireAcl(child, directory: false);
                    files.Add(child.FullName);
                }
            }
        }
    }

    internal static void ValidateRoot(string root)
    {
        RequireSafePath(root);
        RequireAcl(new DirectoryInfo(root), directory: true);
        if (!OperatingSystem.IsWindows()) return;
        // An administrator must also control replacement of every path component.
        for (DirectoryInfo? parent = Directory.GetParent(root); parent is not null; parent = parent.Parent)
        {
            string? reason = AclTrust.Untrusted(parent.GetAccessControl(),
                FileSystemRights.DeleteSubdirectoriesAndFiles | AclTrust.Control, DevelopmentSid());
            if (reason is not null)
                throw new PluginValidationException($"Plugin ancestor '{parent.FullName}' {reason}.");
            // Match StateDirectories' explicit development relocation policy: the protected
            // settings root is the trust anchor, rather than the machine's temporary-directory ACL.
            if (VestigoPaths.IsSettingsDirectoryOverridden &&
                string.Equals(parent.FullName, Path.TrimEndingDirectorySeparator(VestigoPaths.SettingsDirectory), StringComparison.OrdinalIgnoreCase))
                break;
        }
    }

    private static void RequireAcl(FileSystemInfo entry, bool directory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("External plugin deployment ACL validation requires Windows.");
        FileSystemSecurity security = directory
            ? ((DirectoryInfo)entry).GetAccessControl()
            : ((FileInfo)entry).GetAccessControl();
        FileSystemRights dangerous = FileSystemRights.Write | AclTrust.Control |
                                     FileSystemRights.DeleteSubdirectoriesAndFiles;
        if (AclTrust.Untrusted(security, dangerous, DevelopmentSid()) is { } reason)
            throw new PluginValidationException($"Plugin deployment '{entry.FullName}' {reason}.");
    }

    private static string? DevelopmentSid() => VestigoPaths.IsSettingsDirectoryOverridden
        ? WindowsIdentity.GetCurrent().User?.Value : null;

    /// <summary>SHA-256 over sorted UTF-8 relative path + NUL + uppercase file SHA-256 + LF.</summary>
    internal static string ComputeHash(string directory, IReadOnlyList<string> files)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in files.OrderBy(file => Path.GetRelativePath(directory, file).Replace('\\', '/'), StringComparer.Ordinal))
        {
            using FileStream stream = File.OpenRead(file);
            string line = Path.GetRelativePath(directory, file).Replace('\\', '/') + "\0" +
                          Convert.ToHexString(SHA256.HashData(stream)) + "\n";
            hash.AppendData(Encoding.UTF8.GetBytes(line));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
