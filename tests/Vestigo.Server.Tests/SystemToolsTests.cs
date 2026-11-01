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
        public bool IsSecureBootEnabled { get; set; }
        public bool TestSigningConfigured { get; set; }
        public bool TestSigningActive { get; set; }
        public bool WhqlDeveloperTestMode { get; set; }

        public Exception? WhqlReadError { get; set; }
        public Exception? SecureBootReadError { get; set; }
        public Exception? SetTestSigningError { get; set; }
        public int SetTestSigningCalls { get; private set; }
        public int SetWhqlCalls { get; private set; }

        bool ISystemPlatform.IsSecureBootEnabled
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
    }

    private static (SystemService Service, FakePlatform Platform) Create(bool allowWrites = true)
    {
        FakePlatform platform = new();
        VestigoOptions options = new() { System = { AllowBootConfigurationChanges = allowWrites } };
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

        Assert.False(change.After);
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
    public void SetTestSigning_ReportsRebootRequiredWhenActiveStateDiffers()
    {
        (SystemService service, FakePlatform platform) = Create();
        platform.TestSigningActive = false;

        BootConfigurationChange change = service.SetTestSigning(true).Value!;

        Assert.True(change.After);
        Assert.True(change.RebootRequired);
        Assert.Equal("testSigning", change.Setting);
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
}
