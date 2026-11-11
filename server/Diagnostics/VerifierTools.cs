using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Abstractions;

namespace Vestigo.Diagnostics;

/// <summary>Read-only Driver Verifier query. It cannot enable or reset verification.</summary>
[McpServerToolType]
public sealed class VerifierTools(VerifierStateService verifier)
{
    [McpServerTool(Name = "get_driver_verifier_state", ReadOnly = true)]
    [Description("Reads Driver Verifier with the fixed commands verifier /query and verifier /querysettings. running is the current query. configured is the saved settings, which can differ until a reboot. An active statistics dump stays text and is not turned into flags. Unknown settings lines are kept. This does not enable, change, or reset verifier.")]
    public Task<OperationResult<DriverVerifierState>> GetDriverVerifierState(CancellationToken cancellationToken = default) =>
        verifier.ReadAsync(cancellationToken);
}
