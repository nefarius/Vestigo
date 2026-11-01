namespace Vestigo.SystemInfo;

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
}
