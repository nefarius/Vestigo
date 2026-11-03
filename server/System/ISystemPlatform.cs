namespace Vestigo.SystemInfo;

/// <summary>Registry values under <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System</c>.</summary>
public sealed record UacSettings(
    bool? EnableLua,
    int? ConsentPromptBehaviorAdmin,
    int? ConsentPromptBehaviorUser,
    int? PromptOnSecureDesktop);

/// <summary>
///     Host OS and boot-security probes. Tests replace the Windows implementation.
/// </summary>
public interface ISystemPlatform
{
    string MachineName { get; }

    string Name { get; }

    string Edition { get; }

    string DisplayVersion { get; }

    string VersionString { get; }

    int MajorVersion { get; }

    int MinorVersion { get; }

    int? BuildVersion { get; }

    int RevisionVersion { get; }

    bool IsWindows11 { get; }

    bool IsWindowsServer { get; }

    bool IsGrandfathered { get; }

    bool IsUacDisabled { get; }

    string OsBits { get; }

    string ProcessorBits { get; }

    bool IsArm64 { get; }

    bool IsRunningInUefiMode { get; }

    bool? IsSecureBootEnabled { get; }

    bool TestSigningConfigured { get; }

    bool TestSigningActive { get; }

    bool WhqlDeveloperTestMode { get; }

    void SetTestSigning(bool enabled);

    void SetWhqlDeveloperTestMode(bool enabled);

    UacSettings ReadUac();

    void WriteUac(UacSettings settings);
}
