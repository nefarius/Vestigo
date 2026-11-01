using Nefarius.Utilities.WindowsVersion.Exceptions;
using Nefarius.Utilities.WindowsVersion.Util;

namespace Vestigo.SystemInfo;

/// <summary>Reads and writes host state through <c>Nefarius.Utilities.WindowsVersion</c>.</summary>
public sealed class WindowsVersionPlatform : ISystemPlatform
{
    public string MachineName => Environment.MachineName;

    public string Name => OsVersionInfo.Name;

    public string Edition => OsVersionInfo.Edition;

    public string DisplayVersion => OsVersionInfo.DisplayVersion;

    public string VersionString => OsVersionInfo.VersionString;

    public int MajorVersion => OsVersionInfo.MajorVersion;

    public int MinorVersion => OsVersionInfo.MinorVersion;

    public int? BuildVersion => OsVersionInfo.BuildVersion;

    public int RevisionVersion => OsVersionInfo.RevisionVersion;

    public bool IsWindows11 => OsVersionInfo.IsWindows11;

    public bool IsWindowsServer => OsVersionInfo.IsWindowsServer;

    public bool IsGrandfathered => OsUpgradeDetection.IsGrandfathered;

    public bool IsUacDisabled => OsVersionInfo.IsUacDisabled;

    public string OsBits => FormatSoftwareArchitecture(ArchitectureInfo.OsBits);

    public string ProcessorBits => FormatProcessorArchitecture(ArchitectureInfo.ProcessorBits);

    public bool IsArm64 => ArchitectureInfo.IsArm64;

    public bool IsRunningInUefiMode => UefiHelper.IsRunningInUefiMode;

    public bool IsSecureBootEnabled => UefiHelper.IsSecureBootEnabled;

    public bool TestSigningConfigured => BcdHelper.AllowPrereleaseSignatures;

    public bool TestSigningActive => CodeIntegrityHelper.IsTestSignEnabled;

    public bool WhqlDeveloperTestMode => CodeIntegrityPolicyHelper.WhqlDeveloperTestMode;

    public void SetTestSigning(bool enabled)
    {
        try
        {
            BcdHelper.AllowPrereleaseSignatures = enabled;
        }
        catch (BcdAlterAllowPrereleaseSignaturesFailedException exception)
        {
            throw new BcdWriteFailedException(exception.Message, exception);
        }
    }

    public void SetWhqlDeveloperTestMode(bool enabled) =>
        CodeIntegrityPolicyHelper.WhqlDeveloperTestMode = enabled;

    internal static string FormatSoftwareArchitecture(SoftwareArchitecture bits) => bits switch
    {
        SoftwareArchitecture.Bit32 => "32",
        SoftwareArchitecture.Bit64 => "64",
        _ => "unknown"
    };

    internal static string FormatProcessorArchitecture(ProcessorArchitecture bits) => bits switch
    {
        ProcessorArchitecture.Bit32 => "32",
        ProcessorArchitecture.Bit64 => "64",
        ProcessorArchitecture.Itanium64 => "itanium64",
        ProcessorArchitecture.Arm64 => "arm64",
        _ => "unknown"
    };
}
