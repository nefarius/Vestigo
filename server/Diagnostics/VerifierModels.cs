namespace Vestigo.Diagnostics;

/// <summary>One <c>verifier.exe</c> invocation. The argument is chosen by the method, not by the caller.</summary>
public sealed record VerifierCommandResult(int? ExitCode, string Text, bool Truncated, bool TimedOut);

/// <summary>The running <c>verifier /query</c> result. An unrecognized statistics dump stays text.</summary>
public sealed record DriverVerifierQuery(bool? DriversVerified, bool Recognized, string Text);

/// <summary>One flag line from <c>verifier /querysettings</c>. <paramref name="Name" /> is the printed text.</summary>
public sealed record VerifierFlag(uint Value, string Name, bool Enabled);

/// <summary>
///     Configured verifier settings. <paramref name="Unparsed" /> keeps lines that are not a known section.
///     An active <c>/query</c> statistics dump is not parsed into these flags.
/// </summary>
public sealed record DriverVerifierSettings(
    uint? Flags,
    IReadOnlyList<VerifierFlag> StandardFlags,
    IReadOnlyList<VerifierFlag> AdditionalFlags,
    IReadOnlyList<string> RuleClasses,
    string? BootMode,
    IReadOnlyList<string> Rules,
    IReadOnlyList<string> Drivers,
    IReadOnlyList<string> Unparsed,
    bool Recognized);

/// <summary>Running query and configured settings. They are not the same state.</summary>
public sealed record DriverVerifierState(
    int? RunningExitCode,
    DriverVerifierQuery Running,
    int? ConfiguredExitCode,
    DriverVerifierSettings Configured,
    bool OutputTruncated);

/// <summary>Runs the two fixed verifier queries. Callers cannot supply arguments.</summary>
public interface IVerifierQuery
{
    Task<VerifierCommandResult> QueryAsync(CancellationToken cancellationToken);

    Task<VerifierCommandResult> QuerySettingsAsync(CancellationToken cancellationToken);
}
