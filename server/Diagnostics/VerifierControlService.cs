using Vestigo.Abstractions;
using Vestigo.Configuration;

namespace Vestigo.Diagnostics;

/// <summary>Changes Driver Verifier only through the planner, then reads the saved settings back.</summary>
public sealed class VerifierControlService(IVerifierControl control, VerifierStateService state, VestigoOptions options)
{
    public const string RebootNote = "A reboot is required before this change runs. Vestigo does not reboot. reset_driver_verifier clears the saved settings and also waits for a reboot.";

    public Task<OperationResult<DriverVerifierChange>> EnableAsync(
        IReadOnlyList<string>? drivers,
        IReadOnlyList<string>? flags,
        bool acknowledged,
        CancellationToken cancellationToken)
    {
        if (!options.Diagnostics.AllowDriverVerifier)
        {
            return Task.FromResult(OperationResult<DriverVerifierChange>.Fail(
                "driver_verifier_disabled",
                "Driver Verifier changes are disabled. Set Vestigo:Diagnostics:AllowDriverVerifier to true. reset_driver_verifier stays available."));
        }

        OperationResult<IReadOnlyList<string>> arguments = VerifierControlPlanner.Enable(drivers, flags, acknowledged);
        if (!arguments.Success)
        {
            return Task.FromResult(arguments.FailAs<DriverVerifierChange>());
        }

        return ChangeAsync("enable", arguments.Value!, cancellationToken);
    }

    public Task<OperationResult<DriverVerifierChange>> ResetAsync(CancellationToken cancellationToken) =>
        ChangeAsync("reset", VerifierControlPlanner.Reset(), cancellationToken);

    private async Task<OperationResult<DriverVerifierChange>> ChangeAsync(
        string action,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            VerifierCommandResult command = await control.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
            if (command.TimedOut)
            {
                return OperationResult<DriverVerifierChange>.Fail("verifier_timeout", "verifier.exe did not finish within 15 seconds.");
            }

            if (command.ExitCode != 0)
            {
                return OperationResult<DriverVerifierChange>.Fail("verifier_failed", "verifier.exe exited " + command.ExitCode + ".");
            }

            OperationResult<DriverVerifierState> readBack = await state.ReadAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult<DriverVerifierChange>.Ok(new DriverVerifierChange(
                action,
                arguments,
                command.ExitCode,
                command.Text,
                readBack.Success ? readBack.Value : null,
                RebootNote));
        }
        catch (VerifierQueryException ex)
        {
            return OperationResult<DriverVerifierChange>.Fail(ex.Code, ex.Message);
        }
    }
}
