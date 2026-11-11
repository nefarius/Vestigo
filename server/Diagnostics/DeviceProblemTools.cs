using System.ComponentModel;

using ModelContextProtocol.Server;

namespace Vestigo.Diagnostics;

/// <summary>Explains one device problem code from observed evidence. It does not rank drivers.</summary>
[McpServerToolType]
public sealed class DeviceProblemTools(IDeviceProblemSource problems)
{
    [McpServerTool(Name = "explain_device_problem")]
    [Description("Explains the CM_PROB code of one device from observed evidence: device node, class filters, service state, boot security for code 52, Kernel-PnP configuration events, and the latest SetupAPI install sections. observed lists facts. explanation is the documented meaning of a covered code. hypotheses are labeled and cite that evidence. An uncovered code returns insufficientEvidence instead of a guess. This does not rank drivers.")]
    public OperationResult<DeviceProblemReport> ExplainDeviceProblem(
        [Description("Device instance ID.")] string instanceId) =>
        problems.Explain(instanceId);

    [McpServerTool(Name = "explain_driver_load_failure")]
    [Description(
        "Explains a possible driver load failure from observed evidence: the device problem code when instanceId is set, "
        + "the named service state, the service image signature (cache-only, no signer), boot security, "
        + "Code Integrity operational events whose message or file name matches the image file, "
        + "the DeviceGuard HypervisorEnforcedCodeIntegrity Enabled value, and WldpQueryWindowsLockdownMode. "
        + "hvciEnabled is null when the value is missing or neither 0 nor 1. windowsLockdownMode is the S-mode lockdown value, not a WDAC policy inventory. "
        + "hypotheses cite that evidence and do not assert a cause. insufficientEvidence is true when no load-failure signal was observed. "
        + "Pass instanceId, serviceName, or both. This does not rank drivers.")]
    public OperationResult<DriverLoadReport> ExplainDriverLoadFailure(
        [Description("Device instance ID. Optional when serviceName is set.")] string? instanceId = null,
        [Description("Service or driver name, without a path. Optional when instanceId names a device service.")] string? serviceName = null) =>
        problems.ExplainLoad(instanceId, serviceName);
}
