namespace Vestigo.Devices;

/// <summary>
///     Outcome of one nefcon invocation.
/// </summary>
/// <param name="ExitCode">Process exit code, or -1 when the run timed out.</param>
/// <param name="Output">Tail of the combined stdout and stderr text.</param>
/// <param name="TimedOut">True when nefcon was killed after the configured timeout.</param>
public sealed record NefconResult(int ExitCode, string Output, bool TimedOut = false)
{
    /// <summary>nefcon returns <c>ERROR_SUCCESS_REBOOT_REQUIRED</c> (3010) when the operation worked but needs a restart.</summary>
    public const int RebootRequiredExitCode = 3010;

    public bool RebootRequired => !TimedOut && ExitCode == RebootRequiredExitCode;

    /// <summary>Exit code 0, or 3010 (success with reboot required).</summary>
    public bool Succeeded => !TimedOut && (ExitCode == 0 || ExitCode == RebootRequiredExitCode);
}