namespace Vestigo.Desktop;

/// <summary>Names and decisions for managed test desktops. Callers never choose a desktop name.</summary>
public static class DesktopSessionPolicy
{
    public const string Prefix = "Vestigo-";

    public const string TerminateOwned = "terminate-owned";

    public const string FailIfRunning = "fail-if-running";

    public const string WindowStation = "WinSta0";

    public const string DefaultDesktop = "default";

    public const string DefaultPath = @"winsta0\default";

    /// <summary>The lock screen and the UAC secure desktop both use this name on <c>WinSta0</c>.</summary>
    public const string SecureDesktop = "Winlogon";

    public static bool IsSecure(string? name) =>
        string.Equals(name, SecureDesktop, StringComparison.OrdinalIgnoreCase);

    public static bool IsOwnedName(string? name)
    {
        if (name is null || !name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string suffix = name[Prefix.Length..];
        return suffix.Length == 12 && suffix.All(IsHex);
    }

    /// <summary><c>owned</c>, <c>secure</c>, or <c>user</c>. A <c>Vestigo-</c> name is owned even when this process did not create it.</summary>
    public static string Classify(string name) =>
        IsSecure(name) ? "secure" : IsOwnedName(name) ? "owned" : "user";

    public static string? NormalizeCleanup(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy) || string.Equals(policy, TerminateOwned, StringComparison.OrdinalIgnoreCase))
        {
            return TerminateOwned;
        }

        return string.Equals(policy, FailIfRunning, StringComparison.OrdinalIgnoreCase) ? FailIfRunning : null;
    }

    public static string DesktopPath(string desktopName) => WindowStation + "\\" + desktopName;

    /// <summary>Interactive launch may target the console default desktop or a desktop this service created.</summary>
    public static bool IsLaunchPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (string.Equals(path, DefaultPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!path.StartsWith(WindowStation + "\\" + Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsOwnedName(path[(WindowStation.Length + 1)..]);
    }

    public static bool TrySplitPath(string path, out string station, out string desktop)
    {
        int slash = path.LastIndexOf('\\');
        if (slash <= 0 || slash == path.Length - 1)
        {
            station = string.Empty;
            desktop = string.Empty;
            return false;
        }

        station = path[..slash];
        desktop = path[(slash + 1)..];
        return true;
    }

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}

public static class DesktopHostOperations
{
    public const string Create = "create";

    public const string Activate = "activate";

    public const string Restore = "restore";

    public const string Close = "close";

    public const string Enumerate = "enumerate";

    public const string Shutdown = "shutdown";

    public const string Ready = "ready";

    public const string RestorePhase = "restore";

    public const string ClosePhase = "close";
}

/// <summary>One command written to the desktop host, or one recovery request.</summary>
public sealed record DesktopCommand(
    string RequestId,
    string Op,
    string? SessionId = null,
    string? DesktopName = null,
    string? CleanupPolicy = null,
    string? OriginalInputDesktop = null);

/// <summary>A desktop visible on the interactive window station. <see cref="Kind" /> is <c>owned</c>, <c>user</c>, or <c>secure</c>.</summary>
public sealed record EnumeratedDesktop(string Name, string Kind, bool HandleHeld);

/// <summary>Reply from the desktop host or a one-shot recovery helper.</summary>
public sealed record DesktopCommandResult(
    string RequestId,
    bool Success,
    string? ErrorCode,
    string? Error,
    int? NativeError,
    string? SessionId,
    string? DesktopName,
    string? WindowStation,
    int? WindowsSessionId,
    string? OriginalInputDesktop,
    string? InputDesktop,
    bool InputRestored,
    bool HandleReleased,
    bool DesktopRetained,
    bool Activated,
    int? HostProcessId,
    IReadOnlyList<EnumeratedDesktop>? Desktops)
{
    public static DesktopCommandResult Fail(string requestId, string code, string error, int? nativeError = null) =>
        new(requestId, false, code, error, nativeError, null, null, null, null, null, null, false, false, false, false, null, null);

    public static DesktopCommandResult Ready(int hostProcessId) =>
        new(DesktopHostOperations.Ready, true, null, null, null, null, null, DesktopSessionPolicy.WindowStation, null, null, null, false, false, false, false, hostProcessId, null);
}

/// <summary>A process Vestigo started on a managed desktop.</summary>
public sealed record OwnedDesktopProcess(string JobId, int ProcessId, bool Elevated);

/// <summary>
///     One managed desktop. Closing the host handle does not by itself end processes or destroy a desktop that
///     another handle or thread still uses; <see cref="DesktopRetained" /> reports that case.
/// </summary>
public sealed record ManagedDesktopSession(
    string Id,
    string DesktopName,
    string WindowStation,
    int? WindowsSessionId,
    string? OriginalInputDesktop,
    string State,
    string CleanupPolicy,
    DateTimeOffset CreatedAt,
    DateTimeOffset LeaseExpiresAt,
    bool InputSwitched,
    bool HandleHeld,
    bool DesktopRetained,
    IReadOnlyList<OwnedDesktopProcess> Processes,
    string? LastError);

/// <summary>Held across process creation so a close cannot miss the process id.</summary>
public sealed class ManagedDesktopLease : IDisposable
{
    private readonly Action<OwnedDesktopProcess>? _attach;
    private readonly Action? _release;
    private bool _released;

    public ManagedDesktopLease(string sessionId, string desktopPath, Action<OwnedDesktopProcess>? attach = null, Action? release = null)
    {
        SessionId = sessionId;
        DesktopPath = desktopPath;
        _attach = attach;
        _release = release;
    }

    public string SessionId { get; }

    public string DesktopPath { get; }

    public void Attach(OwnedDesktopProcess process) => _attach?.Invoke(process);

    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _release?.Invoke();
    }
}

/// <summary>Resolves a managed session for <c>run_process</c> without accepting an arbitrary desktop name.</summary>
public interface IManagedDesktopDirectory
{
    Task<OperationResult<ManagedDesktopLease>> LeaseForLaunchAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>Used when the Desktop module is off. It cannot see or create test desktops.</summary>
public sealed class DisabledDesktopDirectory : IManagedDesktopDirectory
{
    public Task<OperationResult<ManagedDesktopLease>> LeaseForLaunchAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult<ManagedDesktopLease>.Fail(
            "desktop_sessions_disabled",
            "Managed desktop sessions require the Desktop module and Vestigo:Desktop:AllowDesktopSessions."));
}

/// <summary>Talks to the interactive desktop host and to the one-shot recovery helper.</summary>
public interface IDesktopHostClient
{
    Task<OperationResult<DesktopCommandResult>> SendAsync(DesktopCommand command, CancellationToken cancellationToken);

    Task<OperationResult<DesktopCommandResult>> RecoverAsync(DesktopCommand command, CancellationToken cancellationToken);

    Task ShutdownAsync(CancellationToken cancellationToken);
}

/// <summary>Stops processes that a managed desktop session started.</summary>
public interface IOwnedProcessCleanup
{
    IReadOnlyList<int> StillRunning(IReadOnlyList<OwnedDesktopProcess> processes);

    Task TerminateAsync(IReadOnlyList<OwnedDesktopProcess> processes, CancellationToken cancellationToken);
}

/// <summary>No process module is loaded, so there is nothing to track.</summary>
public sealed class NoOwnedProcessCleanup : IOwnedProcessCleanup
{
    public IReadOnlyList<int> StillRunning(IReadOnlyList<OwnedDesktopProcess> processes) => [];

    public Task TerminateAsync(IReadOnlyList<OwnedDesktopProcess> processes, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
