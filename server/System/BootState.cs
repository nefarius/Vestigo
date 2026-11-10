namespace Vestigo.SystemInfo;

/// <summary>Facts <c>get_boot_state</c> can report without claiming how the machine last started.</summary>
/// <param name="LastBootUpTimeUtc">UTC now minus the tick count. A clock change moves it. Not a boot identity.</param>
/// <param name="UptimeSeconds">Whole seconds from <c>GetTickCount64</c>.</param>
/// <param name="FastStartupEnabled"><c>HiberbootEnabled</c>. Null when the value is absent or not a DWORD. This is configuration, not proof the previous boot restored a hiberfile.</param>
/// <param name="SafeMode"><c>normal</c>, <c>safe</c>, or <c>network</c> from <c>SM_CLEANBOOT</c>. Null when the value is unrecognized.</param>
/// <param name="PendingFileRenameCount">Source/destination pairs in the two Session Manager rename values. Null when a value cannot be read. Paths are not included.</param>
public sealed record BootStateResult(
    DateTimeOffset? LastBootUpTimeUtc,
    long? UptimeSeconds,
    bool? FastStartupEnabled,
    string? SafeMode,
    int? PendingFileRenameCount);

/// <summary>One sample from Windows. Null fields were not available.</summary>
/// <param name="UptimeMilliseconds">Milliseconds since the system started.</param>
/// <param name="FastStartupEnabled">Mapped <c>HiberbootEnabled</c>.</param>
/// <param name="CleanBoot">Raw <c>SM_CLEANBOOT</c>.</param>
/// <param name="PendingFileRenameCount">Pair count, or null when a queue value could not be read.</param>
public readonly record struct BootStateSample(
    ulong? UptimeMilliseconds,
    bool? FastStartupEnabled,
    int? CleanBoot,
    int? PendingFileRenameCount);

/// <summary>Reads uptime, Fast Startup configuration, safe mode, and the pending-rename count.</summary>
public interface IBootStatePlatform
{
    BootStateSample Read();
}

/// <summary>Pure mapping for boot-state fields. Callers supply the clock and the raw Windows values.</summary>
public static class BootState
{
    public const string SessionManagerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager";

    public const string FastStartupKey = SessionManagerKey + @"\Power";

    public const string FastStartupValue = "HiberbootEnabled";

    public static readonly string[] PendingRenameValueNames = ["PendingFileRenameOperations", "PendingFileRenameOperations2"];

    public const string Normal = "normal";

    public const string Safe = "safe";

    public const string Network = "network";

    /// <summary>Null when the value is absent or not a DWORD. Zero is false. Any other DWORD is true.</summary>
    public static bool? FastStartup(object? value) => value switch
    {
        null => null,
        int dword => dword != 0,
        _ => null
    };

    /// <summary>Pairs in one <c>REG_MULTI_SZ</c>, including a trailing unpaired source.</summary>
    public static int PairCount(int valueCount) => valueCount <= 0 ? 0 : (valueCount + 1) / 2;

    /// <summary>Whole seconds, or null when the counter was not read or does not fit.</summary>
    public static long? UptimeSeconds(ulong? uptimeMilliseconds)
    {
        if (uptimeMilliseconds is not ulong milliseconds)
        {
            return null;
        }

        ulong seconds = milliseconds / 1000;
        return seconds > long.MaxValue ? null : (long)seconds;
    }

    /// <summary>UTC <paramref name="utcNow" /> minus the tick count. Null when the span does not fit in a <see cref="TimeSpan" />.</summary>
    public static DateTimeOffset? ApproximateBootTime(ulong? uptimeMilliseconds, DateTimeOffset utcNow)
    {
        if (uptimeMilliseconds is not ulong milliseconds)
        {
            return null;
        }

        if (milliseconds > (ulong)(long.MaxValue / TimeSpan.TicksPerMillisecond))
        {
            return null;
        }

        return utcNow - TimeSpan.FromTicks((long)milliseconds * TimeSpan.TicksPerMillisecond);
    }

    /// <summary><c>SM_CLEANBOOT</c>: 0 normal, 1 safe, 2 safe with networking. Anything else is null.</summary>
    public static string? SafeMode(int? cleanBoot) => cleanBoot switch
    {
        0 => Normal,
        1 => Safe,
        2 => Network,
        _ => null
    };
}
