using System.Security;

using Vestigo.Configuration;

namespace Vestigo.SystemInfo;

public sealed record SystemInfoResult(
    string MachineName,
    string? Name,
    string? Edition,
    string? DisplayVersion,
    string? VersionString,
    int? MajorVersion,
    int? MinorVersion,
    int? Build,
    int? Revision,
    bool? Windows11,
    bool? WindowsServer,
    bool? Grandfathered,
    string? OsBits,
    string? ProcessorBits,
    bool? Arm64,
    bool? UacDisabled);

public sealed record BootSecurityResult(
    bool? Uefi,
    bool? SecureBoot,
    bool? TestSigningConfigured,
    bool? TestSigningActive,
    bool? WhqlDeveloperTestMode,
    bool? RebootPending,
    IReadOnlyList<string> Notes);

public sealed record BootConfigurationChange(
    string Setting,
    bool Enabled,
    bool? Before,
    bool? After,
    bool RebootRequired);

public sealed record UacResult(
    bool? Enabled,
    string Level,
    int? ConsentPromptBehaviorAdmin,
    int? ConsentPromptBehaviorUser,
    bool? PromptOnSecureDesktop,
    IReadOnlyList<string> Notes);

public sealed record UacChange(
    string Level,
    string? Before,
    string? After,
    bool RebootRequired);

/// <summary>Validates and maps host OS and boot-security probes. Never reboots the machine.</summary>
public sealed class SystemService(ISystemPlatform platform, VestigoOptions options)
{
    public OperationResult<SystemInfoResult> GetSystemInfo()
    {
        return OperationResult<SystemInfoResult>.Ok(new SystemInfoResult(
            ProbeText(static p => p.MachineName, platform) ?? Environment.MachineName,
            EmptyToNull(ProbeText(static p => p.Name, platform)),
            EmptyToNull(ProbeText(static p => p.Edition, platform)),
            EmptyToNull(ProbeText(static p => p.DisplayVersion, platform)),
            EmptyToNull(ProbeText(static p => p.VersionString, platform)),
            ProbeValue(static p => p.MajorVersion, platform),
            ProbeValue(static p => p.MinorVersion, platform),
            ProbeNullable(static p => p.BuildVersion, platform),
            ProbeValue(static p => p.RevisionVersion, platform),
            ProbeValue(static p => p.IsWindows11, platform),
            ProbeValue(static p => p.IsWindowsServer, platform),
            ProbeValue(static p => p.IsGrandfathered, platform),
            EmptyToNull(ProbeText(static p => p.OsBits, platform)),
            EmptyToNull(ProbeText(static p => p.ProcessorBits, platform)),
            ProbeValue(static p => p.IsArm64, platform),
            ProbeValue(static p => p.IsUacDisabled, platform)));
    }

    public OperationResult<BootSecurityResult> GetBootSecurity()
    {
        bool? uefi = ProbeValue(static p => p.IsRunningInUefiMode, platform);
        bool? secureBoot = ProbeOptional(static p => p.IsSecureBootEnabled, platform);
        bool? configured = ProbeValue(static p => p.TestSigningConfigured, platform);
        bool? active = ProbeValue(static p => p.TestSigningActive, platform);
        bool? whql = ProbeValue(static p => p.WhqlDeveloperTestMode, platform);
        bool? rebootPending = configured is { } stored && active is { } running ? stored != running : null;

        List<string> notes = [];
        if (secureBoot == true)
        {
            notes.Add("Secure Boot is enabled, so test signing is ignored by the firmware.");
        }

        if (rebootPending == true)
        {
            notes.Add("The BCD test-signing value differs from the running kernel; reboot to apply.");
        }

        if (whql is null)
        {
            notes.Add("WHQL developer test mode could not be read; availability depends on the Windows build.");
        }

        notes.Add("BCD reads and writes apply only to the default boot entry.");

        return OperationResult<BootSecurityResult>.Ok(
            new BootSecurityResult(uefi, secureBoot, configured, active, whql, rebootPending, notes));
    }

    public OperationResult<BootConfigurationChange> SetTestSigning(bool enabled)
    {
        if (!options.System.AllowBootConfigurationChanges)
        {
            return Disabled();
        }

        if (enabled)
        {
            bool? secureBoot = ProbeOptional(static p => p.IsSecureBootEnabled, platform);
            if (secureBoot != false)
            {
                return OperationResult<BootConfigurationChange>.Fail(
                    "secure_boot_enabled",
                    secureBoot == true
                        ? "Secure Boot is enabled, so test signing cannot be turned on."
                        : "Secure Boot state could not be determined, so test signing cannot be turned on.");
            }
        }

        return Write(
            "testSigning",
            enabled,
            static p => p.TestSigningConfigured,
            static (p, value) => p.SetTestSigning(value),
            rebootRequired: (before, after) =>
                after != ProbeValue(static p => p.TestSigningActive, platform));
    }

    public OperationResult<BootConfigurationChange> SetWhqlDeveloperTestMode(bool enabled)
    {
        if (!options.System.AllowBootConfigurationChanges)
        {
            return Disabled();
        }

        return Write(
            "whqlDeveloperTestMode",
            enabled,
            static p => p.WhqlDeveloperTestMode,
            static (p, value) => p.SetWhqlDeveloperTestMode(value),
            rebootRequired: (before, after) => before is null || after is null || before != after);
    }

    public OperationResult<UacResult> GetUac()
    {
        UacSettings? settings = ProbeUac(platform);
        string level = settings is null ? "custom" : MapUacLevel(settings);
        List<string> notes = [];
        if (settings is null)
        {
            notes.Add("UAC policy could not be read.");
        }
        else if (level == "custom")
        {
            notes.Add("The registry values do not match a standard UAC slider level.");
        }

        notes.Add("Group Policy can override these values.");
        notes.Add("EnableLUA changes apply after a reboot.");

        return OperationResult<UacResult>.Ok(new UacResult(
            settings?.EnableLua,
            level,
            settings?.ConsentPromptBehaviorAdmin,
            settings?.ConsentPromptBehaviorUser,
            settings?.PromptOnSecureDesktop is { } prompt ? prompt != 0 : null,
            notes));
    }

    public OperationResult<UacChange> SetUac(string level)
    {
        if (!options.System.AllowUacChanges)
        {
            return OperationResult<UacChange>.Fail(
                "uac_changes_disabled",
                "Changing UAC is disabled. Set Vestigo:System:AllowUacChanges to true.");
        }

        if (!TryCanonicalLevel(level, out string canonical))
        {
            return OperationResult<UacChange>.Fail(
                "invalid_level",
                "level must be always, default, noDim, never, or disabled.");
        }

        UacSettings? before = ProbeUac(platform);
        try
        {
            platform.WriteUac(SettingsForLevel(canonical));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
        {
            return OperationResult<UacChange>.Fail(
                "access_denied",
                "Windows denied access to the UAC policy settings.");
        }
        catch (Exception exception)
        {
            return OperationResult<UacChange>.Fail("uac_write_failed", exception.Message);
        }

        UacSettings? after = ProbeUac(platform);
        return OperationResult<UacChange>.Ok(new UacChange(
            canonical,
            before is null ? null : MapUacLevel(before),
            after is null ? null : MapUacLevel(after),
            UacRebootRequired(before, after)));
    }

    internal static bool UacRebootRequired(UacSettings? before, UacSettings? after) =>
        before?.EnableLua is not { } beforeLua ||
        after?.EnableLua is not { } afterLua ||
        beforeLua != afterLua;

    internal static string MapUacLevel(UacSettings settings)
    {
        if (settings.EnableLua == false)
        {
            return "disabled";
        }

        if (settings.EnableLua == true &&
            settings.ConsentPromptBehaviorAdmin is { } admin &&
            settings.PromptOnSecureDesktop is { } prompt)
        {
            return (admin, prompt) switch
            {
                (2, 1) => "always",
                (5, 1) => "default",
                (5, 0) => "noDim",
                (0, 0) => "never",
                _ => "custom"
            };
        }

        return "custom";
    }

    internal static UacSettings SettingsForLevel(string level) => level switch
    {
        "always" => new UacSettings(true, 2, null, 1),
        "default" => new UacSettings(true, 5, null, 1),
        "noDim" => new UacSettings(true, 5, null, 0),
        "never" => new UacSettings(true, 0, null, 0),
        "disabled" => new UacSettings(false, null, null, null),
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown UAC level.")
    };

    internal static bool TryCanonicalLevel(string? level, out string canonical)
    {
        if (!string.IsNullOrWhiteSpace(level) &&
            KnownLevels.TryGetValue(level.Trim(), out string? mapped))
        {
            canonical = mapped;
            return true;
        }

        canonical = "";
        return false;
    }

    private static readonly Dictionary<string, string> KnownLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["always"] = "always",
        ["default"] = "default",
        ["noDim"] = "noDim",
        ["never"] = "never",
        ["disabled"] = "disabled"
    };

    private static UacSettings? ProbeUac(ISystemPlatform platform)
    {
        try
        {
            return platform.ReadUac();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private OperationResult<BootConfigurationChange> Write(
        string setting,
        bool enabled,
        Func<ISystemPlatform, bool> read,
        Action<ISystemPlatform, bool> write,
        Func<bool?, bool?, bool> rebootRequired)
    {
        bool? before = ProbeValue(read, platform);
        try
        {
            write(platform, enabled);
        }
        catch (BcdWriteFailedException exception)
        {
            return OperationResult<BootConfigurationChange>.Fail("bcd_write_failed", exception.Message);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
        {
            return OperationResult<BootConfigurationChange>.Fail(
                "access_denied",
                "Windows denied access to the boot or code-integrity settings.");
        }
        catch (Exception exception)
        {
            string code = setting == "testSigning" ? "bcd_write_failed" : "ci_write_failed";
            return OperationResult<BootConfigurationChange>.Fail(code, exception.Message);
        }

        bool? after = ProbeValue(read, platform);
        return OperationResult<BootConfigurationChange>.Ok(
            new BootConfigurationChange(
                setting,
                enabled,
                before,
                after,
                rebootRequired(before, after)));
    }

    private static OperationResult<BootConfigurationChange> Disabled() =>
        OperationResult<BootConfigurationChange>.Fail(
            "boot_changes_disabled",
            "Changing boot configuration is disabled. Set Vestigo:System:AllowBootConfigurationChanges to true.");

    private static bool? ProbeOptional(Func<ISystemPlatform, bool?> read, ISystemPlatform platform)
    {
        try
        {
            return read(platform);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static T? ProbeValue<T>(Func<ISystemPlatform, T> read, ISystemPlatform platform) where T : struct
    {
        try
        {
            return read(platform);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? ProbeNullable(Func<ISystemPlatform, int?> read, ISystemPlatform platform)
    {
        try
        {
            return read(platform);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ProbeText(Func<ISystemPlatform, string> read, ISystemPlatform platform)
    {
        try
        {
            return read(platform);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
