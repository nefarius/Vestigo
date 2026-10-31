namespace Vestigo.Services;

/// <summary>Which service classes a listing covers.</summary>
public enum ServiceKind
{
    All,
    Win32,
    Driver
}

/// <summary>Service start types as stored in the registry <c>Start</c> value.</summary>
public enum ServiceStartType
{
    Boot = 0,
    System = 1,
    Auto = 2,
    Demand = 3,
    Disabled = 4
}

/// <summary>Stable lowercase state names used in tool results and filters.</summary>
public static class ServiceStates
{
    public const string Stopped = "stopped";
    public const string StartPending = "start_pending";
    public const string StopPending = "stop_pending";
    public const string Running = "running";
    public const string ContinuePending = "continue_pending";
    public const string PausePending = "pause_pending";
    public const string Paused = "paused";

    public static readonly IReadOnlyList<string> All =
        [Stopped, StartPending, StopPending, Running, ContinuePending, PausePending, Paused];
}

/// <summary>
///     One Win32 service or kernel/file-system driver. Detail fields are null in listings or when the
///     value is not available.
/// </summary>
/// <param name="Kind"><c>win32</c> or <c>driver</c>.</param>
/// <param name="ServiceType">Names such as <c>KERNEL_DRIVER</c>, <c>FILE_SYSTEM_DRIVER</c>, <c>WIN32_OWN_PROCESS</c>.</param>
/// <param name="State">One of <see cref="ServiceStates" />.</param>
/// <param name="StartType"><c>boot</c>, <c>system</c>, <c>auto</c>, <c>demand</c>, <c>disabled</c>.</param>
/// <param name="ErrorControl"><c>ignore</c>, <c>normal</c>, <c>severe</c>, <c>critical</c>.</param>
public sealed record ServiceInfo(
    string Name,
    string DisplayName,
    string Kind,
    string ServiceType,
    string State,
    string? StartType,
    string? ImagePath,
    string? Account,
    IReadOnlyList<string> Dependencies,
    string? ErrorControl = null,
    uint? ProcessId = null,
    uint? Win32ExitCode = null,
    uint? ServiceExitCode = null);

/// <summary>A page of services from <c>list_services</c>.</summary>
public sealed record ServiceList(int Total, int Returned, bool Truncated, IReadOnlyList<ServiceInfo> Services);

/// <summary>Result of a driver service creation or removal through nefcon.</summary>
public sealed record ServiceChangeResult(string Name, bool RebootRequired, string Output, ServiceInfo? Service);

/// <summary>An SCM failure with a stable error code the tools return to clients.</summary>
public sealed class ServiceControlException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}
