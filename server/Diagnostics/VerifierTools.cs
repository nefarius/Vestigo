using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Abstractions;

namespace Vestigo.Diagnostics;

/// <summary>Driver Verifier query, gated enable, and always-available reset. Vestigo does not reboot.</summary>
[McpServerToolType]
public sealed class VerifierTools(VerifierStateService verifier, VerifierControlService control)
{
    [McpServerTool(Name = "get_driver_verifier_state", ReadOnly = true)]
    [Description("Reads Driver Verifier with the fixed commands verifier /query and verifier /querysettings. running is the current query. configured is the saved settings, which can differ until a reboot. An active statistics dump stays text and is not turned into flags. Unknown settings lines are kept. This does not enable, change, or reset verifier.")]
    public Task<OperationResult<DriverVerifierState>> GetDriverVerifierState(CancellationToken cancellationToken = default) =>
        verifier.ReadAsync(cancellationToken);

    [McpServerTool(Name = "set_driver_verifier", Destructive = true)]
    [Description("Saves standard Driver Verifier flags for 1 to 8 named .sys drivers. Requires Vestigo:Diagnostics:AllowDriverVerifier and iUnderstandVerifierCanBugcheck. Omitting flags uses /standard. There is no /all mode, and kernel images are refused. The saved settings are read back. A reboot is required and is not performed.")]
    public Task<OperationResult<DriverVerifierChange>> SetDriverVerifier(
        [Description("Driver image names, such as contoso.sys. 1 to 8. No paths or wildcards.")] List<string> drivers,
        [Description("Standard flag names. Omit them to use verifier /standard.")] List<string>? flags = null,
        [Description("Must be true. Verifier can bugcheck after the next boot.")] bool iUnderstandVerifierCanBugcheck = false,
        CancellationToken cancellationToken = default) =>
        control.EnableAsync(drivers, flags, iUnderstandVerifierCanBugcheck, cancellationToken);

    [McpServerTool(Name = "reset_driver_verifier", Destructive = true)]
    [Description("Runs verifier /reset and reads the saved settings back. This stays available when AllowDriverVerifier is false. A reboot is required before the clear takes effect. Vestigo does not reboot.")]
    public Task<OperationResult<DriverVerifierChange>> ResetDriverVerifier(CancellationToken cancellationToken = default) =>
        control.ResetAsync(cancellationToken);
}
