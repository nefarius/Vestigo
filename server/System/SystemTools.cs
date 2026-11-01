using System.ComponentModel;

using ModelContextProtocol.Server;

namespace Vestigo.SystemInfo;

/// <summary>MCP tools for host Windows version, UEFI, Secure Boot, test signing, and code-integrity settings.</summary>
[McpServerToolType]
public sealed class SystemTools(SystemService service)
{
    [McpServerTool(Name = "get_system_info")]
    [Description("Returns the host Windows name, edition, display version (for example 24H2), version string, major/minor/build/UBR, whether it is Windows 11 or Server, whether the install was in-place upgraded, OS and processor bitness, ARM64, UAC disabled, and the machine name. One failed probe becomes a null field.")]
    public OperationResult<SystemInfoResult> GetSystemInfo() => service.GetSystemInfo();

    [McpServerTool(Name = "get_boot_security")]
    [Description("Returns UEFI vs legacy BIOS, Secure Boot, BCD test-signing (configured, takes effect at next boot), the running kernel test-signing state, WHQL developer test mode, and rebootPending when the BCD value and the running state differ. Notes cover Secure Boot, a pending reboot, and that BCD changes apply only to the default boot entry.")]
    public OperationResult<BootSecurityResult> GetBootSecurity() => service.GetBootSecurity();

    [McpServerTool(Name = "set_test_signing")]
    [Description("Sets BCDE_LIBRARY_TYPE_ALLOW_PRERELEASE_SIGNATURES on the default boot entry. Requires Vestigo:System:AllowBootConfigurationChanges. Refused with secure_boot_enabled when turning test signing on while Secure Boot is enabled. Does not reboot; the running kernel changes only after a restart. Never logs secrets.")]
    public OperationResult<BootConfigurationChange> SetTestSigning(
        [Description("true to enable test signing, false to disable it.")] bool enabled) =>
        service.SetTestSigning(enabled);

    [McpServerTool(Name = "set_whql_developer_test_mode")]
    [Description("Sets the kernel WHQL developer test mode policy that allows traditional cross-signed drivers. Requires Vestigo:System:AllowBootConfigurationChanges. Availability depends on the Windows build. Does not reboot.")]
    public OperationResult<BootConfigurationChange> SetWhqlDeveloperTestMode(
        [Description("true to enable WHQL developer test mode, false to disable it.")] bool enabled) =>
        service.SetWhqlDeveloperTestMode(enabled);
}
