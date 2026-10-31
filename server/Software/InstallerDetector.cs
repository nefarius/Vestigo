using System.Text;
using System.Text.RegularExpressions;

namespace Vestigo.Software;

/// <summary>
///     Classifies an Uninstall registry entry by installer technology. Detection is heuristic and only
///     decides which silent switches the planner may append.
/// </summary>
public static partial class InstallerDetector
{
    private const int NsisScanBytes = 2 * 1024 * 1024;

    public static string Detect(string keyName, IReadOnlyDictionary<string, object?> values)
    {
        string? uninstall = Text(values, "UninstallString");
        if (IsMsi(keyName, values, uninstall))
        {
            return InstallerTypes.Msi;
        }

        if (values.ContainsKey("BundleCachePath") || values.ContainsKey("BundleProviderKey") || values.ContainsKey("BundleUpgradeCode"))
        {
            return InstallerTypes.Burn;
        }

        string? executable = uninstall is null ? null : UninstallPlanner.SplitCommandLine(uninstall)?.Executable;
        if (values.Keys.Any(key => key.StartsWith("Inno Setup:", StringComparison.OrdinalIgnoreCase)) || LooksLikeInno(executable))
        {
            return InstallerTypes.Inno;
        }

        if (uninstall is not null && InstallShieldMarker().IsMatch(uninstall))
        {
            return InstallerTypes.InstallShield;
        }

        if (HasNsisMarker(executable))
        {
            return InstallerTypes.Nsis;
        }

        return InstallerTypes.Unknown;
    }

    /// <summary>Extracts a <c>{GUID}</c> product code from a key name or an <c>msiexec</c> command line.</summary>
    public static string? ProductCode(string keyName, string? uninstallString)
    {
        if (GuidKey().IsMatch(keyName))
        {
            return keyName;
        }

        Match match = uninstallString is null ? Match.Empty : GuidAnywhere().Match(uninstallString);
        return match.Success ? match.Value : null;
    }

    private static bool IsMsi(string keyName, IReadOnlyDictionary<string, object?> values, string? uninstall)
    {
        if (values.TryGetValue("WindowsInstaller", out object? flag) && flag is int or long or uint
            && Convert.ToInt64(flag, System.Globalization.CultureInfo.InvariantCulture) == 1)
        {
            return true;
        }

        return uninstall is not null
            && uninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase)
            && ProductCode(keyName, uninstall) is not null;
    }

    private static bool LooksLikeInno(string? executable)
    {
        if (executable is null || !IsLocalDrivePath(executable) || !InnoUninstaller().IsMatch(Path.GetFileName(executable)))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.ChangeExtension(executable, ".dat"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool HasNsisMarker(string? executable)
    {
        if (executable is null || !IsLocalDrivePath(executable))
        {
            return false;
        }

        try
        {
            if (!File.Exists(executable))
            {
                return false;
            }

            using FileStream stream = new(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] buffer = new byte[(int)Math.Min(stream.Length, NsisScanBytes)];
            int read = stream.Read(buffer, 0, buffer.Length);
            return Encoding.Latin1.GetString(buffer, 0, read).Contains("Nullsoft Install System", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     True for <c>X:\...</c> or <c>X:/...</c>. Registry-supplied paths are probed only when local, so UNC and
    ///     device paths never cause network or device access from the service account.
    /// </summary>
    public static bool IsLocalDrivePath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    private static string? Text(IReadOnlyDictionary<string, object?> values, string name) =>
        values.TryGetValue(name, out object? value) && value is string text && !string.IsNullOrWhiteSpace(text) ? text : null;

    [GeneratedRegex(@"\A\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}\z")]
    private static partial Regex GuidKey();

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex GuidAnywhere();

    [GeneratedRegex(@"\Aunins\d{3}\.exe\z", RegexOptions.IgnoreCase)]
    private static partial Regex InnoUninstaller();

    [GeneratedRegex(@"InstallShield|IsUninst\.exe|-runfromtemp", RegexOptions.IgnoreCase)]
    private static partial Regex InstallShieldMarker();
}
