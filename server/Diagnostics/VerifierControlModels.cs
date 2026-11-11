namespace Vestigo.Diagnostics;

/// <summary>One verifier change and the settings read back afterwards. Vestigo does not reboot.</summary>
public sealed record DriverVerifierChange(
    string Action,
    IReadOnlyList<string> Arguments,
    int? ExitCode,
    string Output,
    DriverVerifierState? ReadBack,
    string Reboot);

/// <summary>Runs a planner-built verifier command. The Windows implementation rejects every other list.</summary>
public interface IVerifierControl
{
    Task<VerifierCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}
