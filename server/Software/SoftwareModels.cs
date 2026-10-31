using Vestigo.Jobs;

namespace Vestigo.Software;

/// <summary>Installer technologies the detector can tell apart.</summary>
public static class InstallerTypes
{
    public const string Msi = "msi";
    public const string Burn = "burn";
    public const string Inno = "inno";
    public const string Nsis = "nsis";
    public const string InstallShield = "installshield";
    public const string Unknown = "unknown";
}

/// <summary>Which inventory scopes a listing covers.</summary>
public enum SoftwareScope
{
    All,
    Machine,
    User
}

/// <summary>One entry of the Windows Uninstall registry keys.</summary>
/// <param name="Id">Stable identifier such as <c>machine-x64:{KeyName}</c> or <c>user:&lt;SID&gt;:{KeyName}</c>.</param>
/// <param name="Scope"><c>machine</c> or <c>user</c>.</param>
/// <param name="Architecture"><c>x64</c> or <c>x86</c> (registry view); user hives report <c>x64</c>.</param>
/// <param name="InstallerType">One of <see cref="InstallerTypes" />.</param>
/// <param name="ParentKeyName">Set for updates and patches that belong to another product.</param>
public sealed record InstalledSoftware(
    string Id,
    string KeyName,
    string Name,
    string? Version,
    string? Publisher,
    string? InstallDate,
    string? InstallLocation,
    string? InstallSource,
    string? UninstallString,
    string? QuietUninstallString,
    string? ModifyPath,
    long? EstimatedSizeKb,
    string Scope,
    string Architecture,
    string? UserSid,
    bool IsMsi,
    string? ProductCode,
    string InstallerType,
    bool SystemComponent,
    bool NoRemove,
    string? ParentKeyName);

/// <summary>A page of entries from <c>list_installed_software</c>.</summary>
public sealed record SoftwareList(int Total, int Returned, bool Truncated, IReadOnlyList<InstalledSoftware> Software);

/// <summary>A resolved silent uninstall command. <see cref="Executable" /> is an absolute path.</summary>
public sealed record UninstallPlan(
    string Executable,
    IReadOnlyList<string> Arguments,
    string InstallerType,
    string Source,
    string? LogPath);

/// <summary>Entry details plus the silent command Vestigo would run, or why none is available.</summary>
public sealed record InstalledSoftwareDetails(
    InstalledSoftware Software,
    UninstallPlan? Plan,
    string? PlanErrorCode,
    string? PlanError);

/// <summary>Outcome of <c>uninstall_software</c>. Fields after <see cref="Job" /> are filled once the job ended.</summary>
public sealed record UninstallResult(
    UninstallPlan Plan,
    bool DryRun,
    JobInfo? Job,
    int? ExitCode,
    bool? RebootRequired,
    bool? StillInstalled,
    string? OutputTail);
