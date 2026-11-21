using Microsoft.Win32;

namespace Vestigo.SystemInfo;

/// <summary>
///     Staging versus live Credential Provider paths. MSI owns only the staging file; LogonUI loads the hashed live copy.
/// </summary>
public static class CredentialProviderFiles
{
    public static bool StagingExists(string installDir) =>
        File.Exists(CredentialProviderIdentity.StagingPath(installDir));

    public static string? ReadRegisteredPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using RegistryKey? key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(CredentialProviderIdentity.InprocServer32Key, writable: false);
        object? value = key?.GetValue(null);
        return value is string path && !string.IsNullOrWhiteSpace(path) ? path : null;
    }

    public static bool IsRegistered(string installDir)
    {
        string? path = ReadRegisteredPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        string live = CredentialProviderIdentity.LiveDirectory(installDir);
        return path.StartsWith(live, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path, CredentialProviderIdentity.StagingPath(installDir), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Deletes unlocked hashed live DLLs that are not the active InprocServer32 target.
    ///     Never touches the registered file, even when LogonUI still has an older copy mapped.
    /// </summary>
    public static IReadOnlyList<string> SweepUnlockedOrphans(string installDir, string? activePath)
    {
        string live = CredentialProviderIdentity.LiveDirectory(installDir);
        if (!Directory.Exists(live))
        {
            return [];
        }

        List<string> removed = [];
        foreach (string path in Directory.EnumerateFiles(live, "Vestigo.CredentialProvider.*.dll"))
        {
            if (!string.IsNullOrWhiteSpace(activePath) &&
                string.Equals(path, activePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(path);
                removed.Add(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }
}
