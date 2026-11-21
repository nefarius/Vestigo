namespace Vestigo.SystemInfo;

/// <summary>
///     Settings for the System module, bound from <c>Vestigo:System</c>.
/// </summary>
public sealed class SystemOptions
{
    public const string SectionName = "System";

    /// <summary>
    ///     Enables <c>set_test_signing</c> and <c>set_whql_developer_test_mode</c>. Reads work regardless.
    /// </summary>
    public bool AllowBootConfigurationChanges { get; set; }

    /// <summary>
    ///     Enables <c>set_uac</c>. Reads work regardless.
    /// </summary>
    public bool AllowUacChanges { get; set; }

    /// <summary>
    ///     Enables <c>reboot_machine</c> and <c>cancel_reboot</c>. Reads work regardless.
    /// </summary>
    public bool AllowReboot { get; set; }

    /// <summary>
    ///     Enables <c>request_autologon_enable</c> and <c>disable_autologon</c>. Reads work regardless.
    /// </summary>
    public bool AllowAutoLogonChanges { get; set; }

    /// <summary>
    ///     Enables <c>request_interactive_logon</c>, <c>lock_workstation</c>, and
    ///     <c>request_unlock_workstation</c>, and starts the Credential Provider named-pipe host.
    ///     The MSI Interactive logon feature must also install the provider DLL.
    /// </summary>
    public bool AllowInteractiveLogon { get; set; }

    /// <summary>
    ///     Lifetime of a one-time consent link used to collect a Windows password outside MCP.
    /// </summary>
    public TimeSpan LogonConsentLifetime { get; set; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        if (LogonConsentLifetime <= TimeSpan.Zero || LogonConsentLifetime > TimeSpan.FromMinutes(30))
        {
            throw new InvalidOperationException(
                "Vestigo:System:LogonConsentLifetime must be positive and at most 00:30:00.");
        }
    }
}
