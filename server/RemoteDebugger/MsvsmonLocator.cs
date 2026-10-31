using System.Diagnostics;

using Microsoft.Win32;

namespace Vestigo.RemoteDebugger;

/// <summary>A discovered <c>msvsmon.exe</c>.</summary>
public sealed record MsvsmonInstall(string Path, string Source);

/// <summary>Side-effect boundary for discovery so it can be tested with fake results.</summary>
public interface IMsvsmonProbe
{
    bool FileExists(string path);

    /// <summary>Full paths of the child directories, or empty when the directory does not exist.</summary>
    IReadOnlyList<string> ChildDirectories(string path);

    /// <summary>Installation roots reported by vswhere for a major version (17 or 18), including prerelease.</summary>
    IReadOnlyList<string> VswhereInstallationPaths(int major);

    /// <summary>Install locations from HKLM uninstall entries that look like Remote Tools for Visual Studio.</summary>
    IReadOnlyList<string> UninstallInstallLocations(DebuggerVersion version);

    /// <summary>Major file version of an executable, or null.</summary>
    int? FileMajorVersion(string path);
}

/// <summary>Finds <c>msvsmon.exe</c> for a version and architecture by probing, never by trusting a single path.</summary>
public sealed class MsvsmonLocator(IMsvsmonProbe probe, IReadOnlyList<string> programFilesRoots, IReadOnlyList<string> extraSearchPaths)
{
    /// <summary>Candidate paths in priority order with their source label (no existence check).</summary>
    public IEnumerable<MsvsmonInstall> Candidates(DebuggerVersion version, DebuggerArch arch)
    {
        string arc = RemoteDebuggerPlanner.ArchFolder(arch);
        int major = (int)version;

        foreach (string root in programFilesRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return new MsvsmonInstall(
                System.IO.Path.Combine(root, $"Microsoft Visual Studio {major}.0", "Common7", "IDE", "Remote Debugger", arc, "msvsmon.exe"),
                "standalone");
        }

        foreach (string root in programFilesRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string edition in probe.ChildDirectories(System.IO.Path.Combine(root, "Microsoft Visual Studio", major.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            {
                yield return new MsvsmonInstall(IdePath(edition, arc), "ide");
            }
        }

        foreach (string install in probe.VswhereInstallationPaths(major))
        {
            yield return new MsvsmonInstall(IdePath(install, arc), "vswhere");
        }

        foreach (string location in probe.UninstallInstallLocations(version))
        {
            yield return new MsvsmonInstall(System.IO.Path.Combine(location, arc, "msvsmon.exe"), "registry");
            yield return new MsvsmonInstall(System.IO.Path.Combine(location, "Remote Debugger", arc, "msvsmon.exe"), "registry");
            yield return new MsvsmonInstall(IdePath(location, arc), "registry");
        }

        foreach (string extra in extraSearchPaths)
        {
            yield return new MsvsmonInstall(System.IO.Path.Combine(extra, arc, "msvsmon.exe"), "extra");
            if (string.Equals(System.IO.Path.GetFileName(extra.TrimEnd('\\', '/')), arc, StringComparison.OrdinalIgnoreCase))
            {
                yield return new MsvsmonInstall(System.IO.Path.Combine(extra, "msvsmon.exe"), "extra");
            }
        }
    }

    /// <summary>First existing candidate. Extra-path hits count only when the file's major version matches.</summary>
    public MsvsmonInstall? Find(DebuggerVersion version, DebuggerArch arch)
    {
        foreach (MsvsmonInstall candidate in Candidates(version, arch))
        {
            if (!probe.FileExists(candidate.Path))
            {
                continue;
            }

            if (candidate.Source == "extra" && probe.FileMajorVersion(candidate.Path) != (int)version)
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static string IdePath(string installRoot, string arc) =>
        System.IO.Path.Combine(installRoot, "Common7", "IDE", "Remote Debugger", arc, "msvsmon.exe");
}

/// <summary>Real file system, vswhere and registry probe.</summary>
public sealed class SystemMsvsmonProbe : IMsvsmonProbe
{
    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<string> ChildDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> VswhereInstallationPaths(int major)
    {
        string vswhere = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return [];
        }

        try
        {
            ProcessStartInfo info = new(vswhere)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            foreach (string argument in new[] { "-all", "-prerelease", "-version", $"[{major}.0,{major + 1}.0)", "-property", "installationPath", "-utf8" })
            {
                info.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(info);
            if (process is null)
            {
                return [];
            }

            Task<string> reader = process.StandardOutput.ReadToEndAsync();
            if (!reader.Wait(TimeSpan.FromSeconds(10)))
            {
                process.Kill(entireProcessTree: true);
                return [];
            }

            string output = reader.Result;
            process.WaitForExit(2_000);

            return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => System.IO.Path.IsPathRooted(line)).ToList();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> UninstallInstallLocations(DebuggerVersion version)
    {
        List<string> result = [];
        string year = RemoteDebuggerPlanner.VersionLabel(version);
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null)
                {
                    continue;
                }

                foreach (string name in uninstall.GetSubKeyNames())
                {
                    using RegistryKey? entry = uninstall.OpenSubKey(name);
                    string? display = entry?.GetValue("DisplayName") as string;
                    if (display is not null
                        && display.Contains("Remote Tools", StringComparison.OrdinalIgnoreCase)
                        && display.Contains(year, StringComparison.Ordinal)
                        && entry!.GetValue("InstallLocation") is string location
                        && System.IO.Path.IsPathRooted(location))
                    {
                        result.Add(location);
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Registry fallback is best effort.
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public int? FileMajorVersion(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileMajorPart;
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            return null;
        }
    }
}