using Vestigo.Configuration;
using Vestigo.Modules;
using Vestigo.SystemInfo;

namespace Vestigo.Server.Tests;

public class SystemToolsTests
{
    private sealed class FakePlatform : ISystemPlatform
    {
        public string MachineName { get; set; } = "TEST-PC";
        public string Name { get; set; } = "Windows 11";
        public string Edition { get; set; } = "Professional";
        public string DisplayVersion { get; set; } = "24H2";
        public string VersionString { get; set; } = "Windows 11 Professional 24H2";
        public int MajorVersion { get; set; } = 10;
        public int MinorVersion { get; set; } = 0;
        public int? BuildVersion { get; set; } = 26100;
        public int RevisionVersion { get; set; } = 1;
        public bool IsWindows11 { get; set; } = true;
        public bool IsWindowsServer { get; set; }
        public bool IsGrandfathered { get; set; }
        public bool IsUacDisabled { get; set; }
        public string OsBits { get; set; } = "64";
        public string ProcessorBits { get; set; } = "64";
        public bool IsArm64 { get; set; }
        public bool IsRunningInUefiMode { get; set; } = true;
        public bool? IsSecureBootEnabled { get; set; } = false;
        public bool TestSigningConfigured { get; set; }
        public bool TestSigningActive { get; set; }
        public bool WhqlDeveloperTestMode { get; set; }
        public UacSettings Uac { get; set; } = new(true, 5, 5, 1);

        public Exception? WhqlReadError { get; set; }
        public Exception? SecureBootReadError { get; set; }
        public Exception? SetTestSigningError { get; set; }
        public Exception? ReadUacError { get; set; }
        public Exception? WriteUacError { get; set; }
        public int SetTestSigningCalls { get; private set; }
        public int SetWhqlCalls { get; private set; }
        public int WriteUacCalls { get; private set; }
        public UacSettings? LastWrittenUac { get; private set; }

        bool? ISystemPlatform.IsSecureBootEnabled
        {
            get
            {
                if (SecureBootReadError is not null)
                {
                    throw SecureBootReadError;
                }

                return IsSecureBootEnabled;
            }
        }

        bool ISystemPlatform.WhqlDeveloperTestMode
        {
            get
            {
                if (WhqlReadError is not null)
                {
                    throw WhqlReadError;
                }

                return WhqlDeveloperTestMode;
            }
        }

        public void SetTestSigning(bool enabled)
        {
            SetTestSigningCalls++;
            if (SetTestSigningError is not null)
            {
                throw SetTestSigningError;
            }

            TestSigningConfigured = enabled;
        }

        public void SetWhqlDeveloperTestMode(bool enabled)
        {
            SetWhqlCalls++;
            WhqlDeveloperTestMode = enabled;
        }

        public UacSettings ReadUac()
        {
            if (ReadUacError is not null)
            {
                throw ReadUacError;
            }

            return Uac;
        }

        public void WriteUac(UacSettings settings)
        {
            WriteUacCalls++;
            LastWrittenUac = settings;
            if (WriteUacError is not null)
            {
                throw WriteUacError;
            }

            Uac = new UacSettings(
                settings.EnableLua ?? Uac.EnableLua,
                settings.ConsentPromptBehaviorAdmin ?? Uac.ConsentPromptBehaviorAdmin,
                settings.ConsentPromptBehaviorUser ?? Uac.ConsentPromptBehaviorUser,
                settings.PromptOnSecureDesktop ?? Uac.PromptOnSecureDesktop);
        }
    }

    private static (SystemService Service, FakePlatform Platform) Create(
        bool allowWrites = true,
        bool allowUac = true)
    {
        FakePlatform platform = new();
        VestigoOptions options = new()
        {
            System =
            {
                AllowBootConfigurationChanges = allowWrites,
                AllowUacChanges = allowUac
            }
        };
        return (new SystemService(platform, options), platform);
    }

    [Fact]
    public void SetTestSigning_RefusesWhenGateIsOff_WithoutTouchingPlatform()
    {
        (SystemService service, FakePlatform platform) = Create(allowWrites: false);

        OperationResult<BootConfigurationChange> result = service.SetTestSigning(true);

        Assert.False(result.Success);
        Assert.Equal("boot_changes_disabled", result.ErrorCode);
        Assert.Equal(0, platform.SetTestSigningCalls);
    }

    [Fact]
    public void SetWhqlDeveloperTestMode_RefusesWhenGateIsOff()
    {
        (SystemService service, FakePlatform platform) = Create(allowWrites: false);

        OperationResult<BootConfigurationChange> result = service.SetWhqlDeveloperTestMode(true);

        Assert.False(result.Success);
        Assert.Equal("boot_changes_disabled", result.ErrorCode);
        Assert.Equal(0, platform.SetWhqlCalls);
    }

    [Fact]
    public void SetTestSigning_RefusesWhenSecureBootIsOn()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.IsSecureBootEnabled = true;

        OperationResult<BootConfigurationChange> result = service.SetTestSigning(true);

        Assert.False(result.Success);
        Assert.Equal("secure_boot_enabled", result.ErrorCode);
        Assert.Equal(0, platform.SetTestSigningCalls);
    }

    [Fact]
    public void SetTestSigning_RefusesWhenSecureBootIsNull()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.IsSecureBootEnabled = null;

        OperationResult<BootConfigurationChange> result = service.SetTestSigning(true);

        Assert.False(result.Success);
        Assert.Equal("secure_boot_enabled", result.ErrorCode);
        Assert.Equal(0, platform.SetTestSigningCalls);
    }

    [Fact]
    public void SetTestSigning_RefusesWhenSecureBootIsUnknown()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.SecureBootReadError = new InvalidOperationException("unavailable");

        OperationResult<BootConfigurationChange> result = service.SetTestSigning(true);

        Assert.False(result.Success);
        Assert.Equal("secure_boot_enabled", result.ErrorCode);
        Assert.Equal(0, platform.SetTestSigningCalls);
    }

    [Fact]
    public void SetTestSigning_StillDisablesWhenSecureBootIsUnknown()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.TestSigningConfigured = true;
        platform.SecureBootReadError = new InvalidOperationException("unavailable");

        BootConfigurationChange change = service.SetTestSigning(false).Value!;

        Assert.False(change.After!.Value);
        Assert.Equal(1, platform.SetTestSigningCalls);
    }

    [Fact]
    public void SetTestSigning_MapsBcdWriteFailure()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.SetTestSigningError = new BcdWriteFailedException("Couldn't change TESTSIGNING state");

        OperationResult<BootConfigurationChange> result = service.SetTestSigning(true);

        Assert.False(result.Success);
        Assert.Equal("bcd_write_failed", result.ErrorCode);
        Assert.Equal(1, platform.SetTestSigningCalls);
    }

    [Fact]
    public void SetTestSigning_MapsAccessDenied()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.SetTestSigningError = new UnauthorizedAccessException();

        OperationResult<BootConfigurationChange> result = service.SetTestSigning(false);

        Assert.False(result.Success);
        Assert.Equal("access_denied", result.ErrorCode);
    }

    [Fact]
    public void GetBootSecurity_ReportsRebootPendingWhenConfiguredAndActiveDiffer()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.TestSigningConfigured = true;
        platform.TestSigningActive = false;

        BootSecurityResult value = service.GetBootSecurity().Value!;

        Assert.True(value.RebootPending);
        Assert.Contains(value.Notes, note => note.Contains("reboot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetBootSecurity_NullsAFailedProbe()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.WhqlReadError = new InvalidOperationException("missing");

        BootSecurityResult value = service.GetBootSecurity().Value!;

        Assert.Null(value.WhqlDeveloperTestMode);
        Assert.True(value.Uefi);
        Assert.Contains(value.Notes, note => note.Contains("WHQL", StringComparison.Ordinal));
    }

    [Fact]
    public void GetBootSecurity_PreservesUnknownSecureBoot()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.IsSecureBootEnabled = null;

        BootSecurityResult value = service.GetBootSecurity().Value!;

        Assert.Null(value.SecureBoot);
    }

    [Fact]
    public void SetTestSigning_ReportsRebootRequiredWhenActiveStateDiffers()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.TestSigningActive = false;

        BootConfigurationChange change = service.SetTestSigning(true).Value!;

        Assert.True(change.After!.Value);
        Assert.True(change.RebootRequired);
        Assert.Equal("testSigning", change.Setting);
    }

    [Fact]
    public void SetWhqlDeveloperTestMode_ReportsRebootRequiredWhenProbeIsUnknown()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.WhqlReadError = new InvalidOperationException("missing");

        BootConfigurationChange change = service.SetWhqlDeveloperTestMode(true).Value!;

        Assert.Null(change.Before);
        Assert.Null(change.After);
        Assert.True(change.RebootRequired);
        Assert.Equal(1, platform.SetWhqlCalls);
    }

    [Fact]
    public void GetSystemInfo_ReturnsOsFields()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.IsGrandfathered = true;
        platform.IsArm64 = true;
        platform.ProcessorBits = "arm64";

        SystemInfoResult value = service.GetSystemInfo().Value!;

        Assert.Equal("TEST-PC", value.MachineName);
        Assert.Equal("Windows 11", value.Name);
        Assert.Equal("24H2", value.DisplayVersion);
        Assert.Equal(26100, value.Build);
        Assert.True(value.Windows11);
        Assert.True(value.Grandfathered);
        Assert.True(value.Arm64);
        Assert.Equal("arm64", value.ProcessorBits);
    }

    [Fact]
    public void PrivilegedWarning_NamesBootConfigurationChangesWithoutTokens()
    {
        VestigoOptions open = new() { System = { AllowBootConfigurationChanges = true } };
        VestigoOptions tokens = new() { ApiTokens = ["t"], System = { AllowBootConfigurationChanges = true } };

        ModuleRegistry.ValidateConfiguration(open);
        Assert.Contains("Boot configuration changes", ModuleRegistry.GetPrivilegedModuleWarning(open));
        ModuleRegistry.ValidateConfiguration(tokens);
        Assert.Null(ModuleRegistry.GetPrivilegedModuleWarning(tokens));
    }

    [Fact]
    public void PrivilegedWarning_NamesUacChangesWithoutTokens()
    {
        VestigoOptions open = new() { System = { AllowUacChanges = true } };
        VestigoOptions tokens = new() { ApiTokens = ["t"], System = { AllowUacChanges = true } };

        ModuleRegistry.ValidateConfiguration(open);
        Assert.Contains("UAC changes", ModuleRegistry.GetPrivilegedModuleWarning(open));
        ModuleRegistry.ValidateConfiguration(tokens);
        Assert.Null(ModuleRegistry.GetPrivilegedModuleWarning(tokens));
    }

    [Theory]
    [InlineData(true, 2, 1, "always")]
    [InlineData(true, 5, 1, "default")]
    [InlineData(true, 5, 0, "noDim")]
    [InlineData(true, 0, 0, "never")]
    [InlineData(false, 5, 1, "disabled")]
    [InlineData(false, 2, 1, "disabled")]
    [InlineData(true, 1, 1, "custom")]
    public void MapUacLevel_MatchesSliderAndDisabled(bool enableLua, int admin, int prompt, string expected) =>
        Assert.Equal(expected, SystemService.MapUacLevel(new UacSettings(enableLua, admin, 5, prompt)));

    [Fact]
    public void MapUacLevel_MissingValuesAreCustom()
    {
        Assert.Equal("custom", SystemService.MapUacLevel(new UacSettings(null, 5, 5, 1)));
        Assert.Equal("custom", SystemService.MapUacLevel(new UacSettings(true, null, 5, 1)));
        Assert.Equal("custom", SystemService.MapUacLevel(new UacSettings(true, 5, 5, null)));
    }

    [Fact]
    public void GetUac_ReportsDefaultLevelAndNotes()
    {
        (SystemService service, _) = Create();

        UacResult value = service.GetUac().Value!;

        Assert.True(value.Enabled);
        Assert.Equal("default", value.Level);
        Assert.Equal(5, value.ConsentPromptBehaviorAdmin);
        Assert.Equal(5, value.ConsentPromptBehaviorUser);
        Assert.True(value.PromptOnSecureDesktop);
        Assert.Contains(value.Notes, note => note.Contains("Group Policy", StringComparison.Ordinal));
        Assert.Contains(value.Notes, note => note.Contains("EnableLUA", StringComparison.Ordinal));
    }

    [Fact]
    public void GetUac_ReportsCustomWhenValuesDoNotMatch()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.Uac = new UacSettings(true, 1, 0, 1);

        UacResult value = service.GetUac().Value!;

        Assert.Equal("custom", value.Level);
        Assert.Contains(value.Notes, note => note.Contains("do not match", StringComparison.Ordinal));
    }

    [Fact]
    public void GetUac_NullsAFailedProbe()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.ReadUacError = new InvalidOperationException("missing");

        UacResult value = service.GetUac().Value!;

        Assert.Null(value.Enabled);
        Assert.Equal("custom", value.Level);
        Assert.Contains(value.Notes, note => note.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void SetUac_RefusesWhenGateIsOff_WithoutTouchingPlatform()
    {
        (SystemService service, FakePlatform platform) = Create(allowUac: false);

        OperationResult<UacChange> result = service.SetUac("never");

        Assert.False(result.Success);
        Assert.Equal("uac_changes_disabled", result.ErrorCode);
        Assert.Equal(0, platform.WriteUacCalls);
    }

    [Theory]
    [InlineData("custom")]
    [InlineData("")]
    [InlineData("loud")]
    public void SetUac_RejectsUnknownLevel(string level)
    {
        (SystemService service, FakePlatform platform) = Create();

        OperationResult<UacChange> result = service.SetUac(level);

        Assert.False(result.Success);
        Assert.Equal("invalid_level", result.ErrorCode);
        Assert.Equal(0, platform.WriteUacCalls);
    }

    [Fact]
    public void SetUac_WritesSliderWithoutRebootWhenEnableLuaStaysOn()
    {
        (SystemService service, FakePlatform platform) = Create();

        UacChange change = service.SetUac("never").Value!;

        Assert.Equal("never", change.Level);
        Assert.Equal("default", change.Before);
        Assert.Equal("never", change.After);
        Assert.False(change.RebootRequired);
        Assert.Equal(new UacSettings(true, 0, null, 0), platform.LastWrittenUac);
        Assert.Equal(5, platform.Uac.ConsentPromptBehaviorUser);
    }

    [Fact]
    public void SetUac_ReportsRebootRequiredOnlyWhenEnableLuaFlips()
    {
        (SystemService service, FakePlatform platform) = Create();

        UacChange disable = service.SetUac("disabled").Value!;

        Assert.Equal("default", disable.Before);
        Assert.Equal("disabled", disable.After);
        Assert.True(disable.RebootRequired);
        Assert.Equal(new UacSettings(false, null, null, null), platform.LastWrittenUac);
        Assert.Equal(5, platform.Uac.ConsentPromptBehaviorAdmin);

        UacChange enable = service.SetUac("default").Value!;

        Assert.Equal("disabled", enable.Before);
        Assert.Equal("default", enable.After);
        Assert.True(enable.RebootRequired);
    }

    [Fact]
    public void SetUac_ReportsRebootRequiredWhenProbeIsUnknown()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.ReadUacError = new InvalidOperationException("missing");

        UacChange change = service.SetUac("never").Value!;

        Assert.Null(change.Before);
        Assert.Null(change.After);
        Assert.True(change.RebootRequired);
        Assert.Equal(1, platform.WriteUacCalls);
    }

    [Fact]
    public void SetUac_ReportsRebootRequiredWhenEnableLuaIsUnknown()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.Uac = new UacSettings(null, 5, 5, 1);

        UacChange change = service.SetUac("never").Value!;

        Assert.True(change.RebootRequired);
        Assert.True(platform.Uac.EnableLua);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void UacRebootRequired_ComparesKnownEnableLua(bool before, bool after, bool expected) =>
        Assert.Equal(
            expected,
            SystemService.UacRebootRequired(
                new UacSettings(before, 5, 5, 1),
                new UacSettings(after, 5, 5, 1)));

    [Fact]
    public void UacRebootRequired_UnknownOrMissingIsConservative()
    {
        UacSettings known = new(true, 5, 5, 1);
        UacSettings unknown = new(null, 5, 5, 1);

        Assert.True(SystemService.UacRebootRequired(null, known));
        Assert.True(SystemService.UacRebootRequired(known, null));
        Assert.True(SystemService.UacRebootRequired(null, null));
        Assert.True(SystemService.UacRebootRequired(unknown, known));
        Assert.True(SystemService.UacRebootRequired(known, unknown));
        Assert.True(SystemService.UacRebootRequired(unknown, unknown));
    }

    [Fact]
    public void SetUac_AcceptsCaseInsensitiveLevel()
    {
        (SystemService service, _) = Create();

        UacChange change = service.SetUac("NODIM").Value!;

        Assert.Equal("noDim", change.Level);
        Assert.Equal("noDim", change.After);
    }

    [Fact]
    public void SetUac_MapsAccessDenied()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.WriteUacError = new UnauthorizedAccessException();

        OperationResult<UacChange> result = service.SetUac("always");

        Assert.False(result.Success);
        Assert.Equal("access_denied", result.ErrorCode);
        Assert.Equal(1, platform.WriteUacCalls);
    }

    [Fact]
    public void SetUac_MapsWriteFailure()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.WriteUacError = new InvalidOperationException("registry locked");

        OperationResult<UacChange> result = service.SetUac("always");

        Assert.False(result.Success);
        Assert.Equal("uac_write_failed", result.ErrorCode);
        Assert.Equal("registry locked", result.Error);
    }

    [Fact]
    public void ReadSecureBoot_MissingValueIsUnknown() =>
        Assert.Null(WindowsVersionPlatform.ReadSecureBoot(null, uefi: true));

    [Fact]
    public void ReadSecureBoot_LegacyFirmwareIsUnknown() =>
        Assert.Null(WindowsVersionPlatform.ReadSecureBoot(0, uefi: false));

    [Fact]
    public void ReadSecureBoot_ZeroOnUefiIsDisabled() =>
        Assert.False(WindowsVersionPlatform.ReadSecureBoot(0, uefi: true));

    [Fact]
    public void ReadSecureBoot_PositiveOnUefiIsEnabled() =>
        Assert.True(WindowsVersionPlatform.ReadSecureBoot(1, uefi: true));

    [Fact]
    public void ReadEnableLua_MissingValueIsUnknown() =>
        Assert.Null(WindowsVersionPlatform.ReadEnableLua(null));

    [Fact]
    public void ReadEnableLua_ZeroIsDisabled() =>
        Assert.False(WindowsVersionPlatform.ReadEnableLua(0));

    [Fact]
    public void ReadEnableLua_NonzeroIsEnabled() =>
        Assert.True(WindowsVersionPlatform.ReadEnableLua(1));

    [Fact]
    public void ReadEnableLua_NegativeIsEnabled() =>
        Assert.True(WindowsVersionPlatform.ReadEnableLua(-1));

    [Fact]
    public void ReadEnableLua_NonNumericIsUnknown() =>
        Assert.Null(WindowsVersionPlatform.ReadEnableLua("abc"));

    [Fact]
    public void ReadEnableLua_ByteArrayIsUnknown() =>
        Assert.Null(WindowsVersionPlatform.ReadEnableLua(new byte[] { 1 }));

    [Fact]
    public void ReadDword_MissingValueIsUnknown() =>
        Assert.Null(WindowsVersionPlatform.ReadDword(null));

    [Fact]
    public void ReadDword_ZeroIsZero() =>
        Assert.Equal(0, WindowsVersionPlatform.ReadDword(0));

    [Fact]
    public void ReadDword_NonzeroIsPreserved() =>
        Assert.Equal(5, WindowsVersionPlatform.ReadDword(5));

    [Fact]
    public void ReadDword_NegativeIsPreserved() =>
        Assert.Equal(-1, WindowsVersionPlatform.ReadDword(-1));

    [Fact]
    public void ReadDword_NonNumericIsUnknown() =>
        Assert.Null(WindowsVersionPlatform.ReadDword("abc"));

    [Fact]
    public void ReadDword_ByteArrayIsUnknown() =>
        Assert.Null(WindowsVersionPlatform.ReadDword(new byte[] { 1, 0, 0, 0 }));
}
