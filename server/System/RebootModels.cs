namespace Vestigo.SystemInfo;

/// <summary>Outcome of <c>reboot_machine</c>. <see cref="DryRun"/> means Windows was not called.</summary>
public sealed record RebootResult(
    string Mode,
    int DelaySeconds,
    bool Force,
    bool Planned,
    string? Message,
    DateTimeOffset? ScheduledAtUtc,
    bool DryRun,
    IReadOnlyList<string> Notes);

/// <summary>Outcome of <c>cancel_reboot</c>.</summary>
public sealed record CancelRebootResult(bool Cancelled, IReadOnlyList<string> Notes);

/// <summary>One Windows reboot or abort call. <see cref="Win32Error"/> is 0 on success.</summary>
public sealed record RebootPlatformOutcome(bool Success, int Win32Error, string? Stage);

/// <summary>
///     Local machine reboot through <c>InitiateShutdown</c> and <c>AbortSystemShutdown</c>.
///     Tests replace the Windows implementation.
/// </summary>
public interface IRebootPlatform
{
    RebootPlatformOutcome InitiateReboot(uint delaySeconds, bool force, bool planned, string? message);

    RebootPlatformOutcome AbortReboot();
}

/// <summary>
///     Pure mapping for reboot flags, shutdown reasons, message checks, and Win32 error codes.
///     Callers supply the delay and the raw Windows result.
/// </summary>
public static class RebootPolicy
{
    public const int MinDelaySeconds = 0;

    /// <summary>InitiateShutdown maximum grace period: ten years.</summary>
    public const int MaxDelaySeconds = 315_360_000;

    public const int MaxMessageLength = 512;

    public const string Immediate = "immediate";

    public const string Scheduled = "scheduled";

    public const uint ShutdownForceOthers = 0x00000001;

    public const uint ShutdownForceSelf = 0x00000002;

    public const uint ShutdownRestart = 0x00000004;

    public const uint ShutdownGraceOverride = 0x00000020;

    public const uint ReasonMajorApplication = 0x00040000;

    public const uint ReasonMinorMaintenance = 0x00000001;

    public const uint ReasonFlagPlanned = 0x80000000;

    public const int ErrorAccessDenied = 5;

    public const int ErrorInvalidParameter = 87;

    public const int ErrorShutdownInProgress = 1115;

    public const int ErrorNoShutdownInProgress = 1116;

    public const int ErrorShutdownIsScheduled = 1190;

    public const int ErrorMachineLocked = 1271;

    public const int ErrorNotAllAssigned = 1300;

    public const int ErrorPrivilegeNotHeld = 1314;

    public static string Mode(int delaySeconds) => delaySeconds <= 0 ? Immediate : Scheduled;

    public static uint Flags(uint delaySeconds, bool force)
    {
        uint flags = ShutdownRestart;
        if (delaySeconds == 0)
        {
            flags |= ShutdownGraceOverride;
        }

        if (force)
        {
            flags |= ShutdownForceOthers | ShutdownForceSelf;
        }

        return flags;
    }

    public static uint Reason(bool planned)
    {
        uint reason = ReasonMajorApplication | ReasonMinorMaintenance;
        if (planned)
        {
            reason |= ReasonFlagPlanned;
        }

        return reason;
    }

    public static bool TryNormalizeMessage(string? message, out string? normalized, out string error)
    {
        normalized = null;
        error = "";
        if (string.IsNullOrWhiteSpace(message))
        {
            return true;
        }

        string trimmed = message.Trim();
        if (trimmed.Length > MaxMessageLength)
        {
            error = "message must be at most 512 characters.";
            return false;
        }

        foreach (char value in trimmed)
        {
            if (char.IsControl(value))
            {
                error = "message cannot contain control characters.";
                return false;
            }
        }

        normalized = trimmed;
        return true;
    }

    public static (string Code, string Error) MapInitiateError(int win32Error) => win32Error switch
    {
        ErrorAccessDenied or ErrorNotAllAssigned or ErrorPrivilegeNotHeld =>
            ("access_denied", "Windows denied the reboot. The service needs SeShutdownPrivilege."),
        ErrorShutdownInProgress or ErrorShutdownIsScheduled =>
            ("reboot_already_in_progress", "A reboot is already in progress or scheduled."),
        ErrorNoShutdownInProgress =>
            ("no_reboot_in_progress", "There is no reboot in progress to cancel."),
        ErrorMachineLocked =>
            ("machine_locked", "Windows refused the reboot because the machine is locked."),
        ErrorInvalidParameter =>
            ("invalid_parameter", "Windows rejected the reboot parameters."),
        _ => ("reboot_failed", $"Windows could not reboot the machine (Win32 {win32Error}).")
    };

    public static (string Code, string Error) MapAbortError(int win32Error) => win32Error switch
    {
        ErrorAccessDenied or ErrorNotAllAssigned or ErrorPrivilegeNotHeld =>
            ("access_denied", "Windows denied cancelling the reboot. The service needs SeShutdownPrivilege."),
        ErrorNoShutdownInProgress =>
            ("no_reboot_in_progress", "There is no reboot in progress to cancel."),
        _ => ("cancel_reboot_failed", $"Windows could not cancel the reboot (Win32 {win32Error}).")
    };
}
