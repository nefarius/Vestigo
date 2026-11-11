using System.ComponentModel;

using ModelContextProtocol.Server;

namespace Vestigo.Power;

/// <summary>Read-only MCP tools for AC, battery, the active scheme, capabilities, and AC/DC sleep policy.</summary>
[McpServerToolType]
public sealed class PowerTools(PowerService service)
{
    [McpServerTool(Name = "get_power_state", ReadOnly = true)]
    [Description("Returns AC line status (online, offline, or unknown), whether a battery is present, its percent, whether it is charging (separate from AC status), remaining seconds, the active scheme GUID and name, approximate UTC last sleep and wake times, and platform capabilities. A battery that is absent leaves the battery fields null. Charging can be true while AC status is offline. Last sleep and wake are UTC now minus the interrupt-time counters, so a clock change moves them. fastStartupSupported is a capability, not the HiberbootEnabled configuration from get_boot_state. A failed probe is a null field. There is no write.")]
    public OperationResult<PowerStateResult> GetPowerState() => service.GetPowerState();

    [McpServerTool(Name = "get_sleep_configuration", ReadOnly = true)]
    [Description("Returns the active scheme's AC and DC lid-close action (doNothing, sleep, hibernate, shutdown, or unknown), standby, hibernate, and monitor timeouts in seconds (0 means never), wake-timer enablement, and hybrid sleep. A value Windows did not return is null, not doNothing and not zero. There is no write.")]
    public OperationResult<SleepConfigurationResult> GetSleepConfiguration() => service.GetSleepConfiguration();

    [McpServerTool(Name = "get_last_wake_source", ReadOnly = true)]
    [Description("Returns the newest Microsoft-Windows-Power-Troubleshooter event 1 from the System log: WakeTime, SleepTime, the WakeSourceType number, WakeSourceText, and any wake-timer owner and context. eventFound is false when that event is absent, and the other fields are then null. WakeSourceType is not translated to a name. Text fields are capped at 512 characters. This does not list power-request owners. There is no write.")]
    public OperationResult<WakeSourceResult> GetLastWakeSource() => service.GetLastWakeSource();
}
