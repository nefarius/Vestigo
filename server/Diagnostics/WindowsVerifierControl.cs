namespace Vestigo.Diagnostics;

/// <summary>Enable and reset entry points. Both refuse any argument list the planner did not build.</summary>
internal sealed class WindowsVerifierControl : IVerifierControl
{
    public Task<VerifierCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (!VerifierControlPlanner.IsAllowed(arguments))
        {
            throw new VerifierQueryException("verifier_failed", "The verifier argument list was not a fixed enable or reset command.");
        }

        return VerifierInvocation.RunAsync(arguments, cancellationToken);
    }
}
