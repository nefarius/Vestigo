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
}
