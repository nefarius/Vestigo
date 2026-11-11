using Vestigo.Abstractions;

namespace Vestigo.Diagnostics;

/// <summary>Reads configured and running Driver Verifier state. It does not enable, change, or reset verifier.</summary>
public sealed class VerifierStateService(IVerifierQuery query)
{
    public async Task<OperationResult<DriverVerifierState>> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            VerifierCommandResult running = await query.QueryAsync(cancellationToken).ConfigureAwait(false);
            if (running.TimedOut)
            {
                return OperationResult<DriverVerifierState>.Fail("verifier_timeout", "verifier /query did not finish within 15 seconds.");
            }

            VerifierCommandResult configured = await query.QuerySettingsAsync(cancellationToken).ConfigureAwait(false);
            if (configured.TimedOut)
            {
                return OperationResult<DriverVerifierState>.Fail("verifier_timeout", "verifier /querysettings did not finish within 15 seconds.");
            }

            return OperationResult<DriverVerifierState>.Ok(new DriverVerifierState(
                running.ExitCode,
                VerifierStateParser.ParseRunning(running.Text),
                configured.ExitCode,
                VerifierStateParser.ParseSettings(configured.Text),
                running.Truncated || configured.Truncated));
        }
        catch (VerifierQueryException ex)
        {
            return OperationResult<DriverVerifierState>.Fail(ex.Code, ex.Message);
        }
    }
}

/// <summary>The fixed verifier command could not be started.</summary>
public sealed class VerifierQueryException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
