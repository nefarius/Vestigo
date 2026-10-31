using System.Globalization;
using System.Runtime.Versioning;
using System.Security;

using Microsoft.Win32;

namespace Vestigo.Software;

/// <summary>
///     Reads the Uninstall registry keys of the machine (64-bit and 32-bit views) and of every loaded user hive.
///     Strictly read-only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistrySoftwareInventory : ISoftwareInventory
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<InstalledSoftware> List()
    {
        List<InstalledSoftware> result = [];
        foreach ((RegistryView view, string architecture) in new[] { (RegistryView.Registry64, "x64"), (RegistryView.Registry32, "x86") })
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            Read(baseKey, "machine-" + architecture + ":", "machine", architecture, null, result);
        }

        using (RegistryKey users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64))
        {
            foreach (string sid in users.GetSubKeyNames())
            {
                if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !sid.StartsWith("S-1-12-1-", StringComparison.Ordinal)
                    || sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    using RegistryKey? hive = users.OpenSubKey(sid);
                    if (hive is not null)
                    {
                        Read(hive, $"user:{sid}:", "user", "x64", sid, result);
                    }
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                {
                    // Hive not readable; skip it.
                }
            }
        }

        return result;
    }

    private static void Read(
        RegistryKey root,
        string idPrefix,
        string scope,
        string architecture,
        string? sid,
        List<InstalledSoftware> output)
    {
        using RegistryKey? uninstall = root.OpenSubKey(UninstallPath);
        if (uninstall is null)
        {
            return;
        }

        foreach (string keyName in uninstall.GetSubKeyNames())
        {
            try
            {
                using RegistryKey? key = uninstall.OpenSubKey(keyName);
                if (key is null)
                {
                    continue;
                }

                Dictionary<string, object?> values = new(StringComparer.OrdinalIgnoreCase);
                foreach (string name in key.GetValueNames())
                {
                    values[name] = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                }

                string? displayName = Text(values, "DisplayName");
                if (displayName is null)
                {
                    continue;
                }

                string? uninstallString = Text(values, "UninstallString");
                string type = InstallerDetector.Detect(keyName, values);
                output.Add(new InstalledSoftware(
                    idPrefix + keyName,
                    keyName,
                    displayName,
                    Text(values, "DisplayVersion"),
                    Text(values, "Publisher"),
                    Date(Text(values, "InstallDate")),
                    Text(values, "InstallLocation"),
                    Text(values, "InstallSource"),
                    uninstallString,
                    Text(values, "QuietUninstallString"),
                    Text(values, "ModifyPath"),
                    Number(values, "EstimatedSize"),
                    scope,
                    architecture,
                    sid,
                    type == InstallerTypes.Msi,
                    type == InstallerTypes.Msi ? InstallerDetector.ProductCode(keyName, uninstallString) : null,
                    type,
                    Number(values, "SystemComponent") == 1,
                    Number(values, "NoRemove") == 1,
                    Text(values, "ParentKeyName")));
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                // Unreadable entry; skip it.
            }
        }
    }

    private static string? Text(Dictionary<string, object?> values, string name) =>
        values.TryGetValue(name, out object? value) && value is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static long? Number(Dictionary<string, object?> values, string name) =>
        values.TryGetValue(name, out object? value) && value is int or long or uint
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : null;

    private static string? Date(string? raw) =>
        raw is not null && DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
            ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : raw;
}
