using System.Globalization;

using Microsoft.Win32;

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

    public bool? IsSecureBootEnabled
    {
        get
        {
            object? value = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State",
                "UEFISecureBootEnabled",
                null);
            return ReadSecureBoot(value, UefiHelper.IsRunningInUefiMode);
        }
    }

    /// <summary>
    ///     Secure Boot is unknown on legacy firmware or when the firmware state value is missing.
    ///     The library's <c>UefiHelper.IsSecureBootEnabled</c> treats a missing value as <c>false</c>.
    /// </summary>
    internal static bool? ReadSecureBoot(object? registryValue, bool uefi)
    {
        if (!uefi || registryValue is null)
        {
            return null;
        }

        return Convert.ToInt32(registryValue, CultureInfo.InvariantCulture) > 0;
    }

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

    public UacSettings ReadUac()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        if (key is null)
        {
            return new UacSettings(null, null, null, null);
        }

        return new UacSettings(
            ReadEnableLua(key.GetValue("EnableLUA")),
            ReadDword(key.GetValue("ConsentPromptBehaviorAdmin")),
            ReadDword(key.GetValue("ConsentPromptBehaviorUser")),
            ReadDword(key.GetValue("PromptOnSecureDesktop")));
    }

    public void WriteUac(UacSettings settings)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
            writable: true);
        if (key is null)
        {
            throw new InvalidOperationException("The UAC policy registry key could not be opened.");
        }

        WriteDword(key, "EnableLUA", settings.EnableLua is { } lua ? lua ? 1 : 0 : null);
        WriteDword(key, "ConsentPromptBehaviorAdmin", settings.ConsentPromptBehaviorAdmin);
        WriteDword(key, "ConsentPromptBehaviorUser", settings.ConsentPromptBehaviorUser);
        WriteDword(key, "PromptOnSecureDesktop", settings.PromptOnSecureDesktop);
    }

    internal static bool? ReadEnableLua(object? value)
    {
        int? dword = ReadDword(value);
        return dword is { } flag ? flag != 0 : null;
    }

    internal static int? ReadDword(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WriteDword(RegistryKey key, string name, int? value)
    {
        if (value is { } dword)
        {
            key.SetValue(name, dword, RegistryValueKind.DWord);
        }
    }

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
