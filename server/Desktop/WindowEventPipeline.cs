using System.Text.Json;
using System.Text.Json.Serialization;

using Vestigo.Processes;

using Windows.Win32.Foundation;

namespace Vestigo.Desktop;

/// <summary>What a window-event capture follows. Child processes are never included.</summary>
public sealed record WindowEventStart(
    string? Handle,
    int? ProcessId,
    DateTimeOffset? ExpectedStartTime,
    TimeSpan Duration,
    int MaxEvents,
    bool Elevated = false)
{
    /// <summary>True only for a process filter with no window filter. Those windows may be created after the capture starts.</summary>
    public bool IncludesNewWindows => ProcessId is not null && string.IsNullOrWhiteSpace(Handle);

    public bool IncludesChildProcesses => false;
}

/// <summary>
///     One capture record. <see cref="Origin" /> is <c>observed</c> for a hook or process-handle notification,
///     <c>sampled</c> for a value read because another event arrived, and <c>inferred</c> is not used: this capture
///     does not infer an exit cause. <c>ExitCode</c> is the Win32 code when it was read, not a cause.
/// </summary>
public sealed record WindowEventRecord(
    long Sequence,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ObservedAt,
    string Kind,
    string? Event,
    string? Origin,
    string? Explanation,
    string? Handle,
    int? ProcessId,
    DateTimeOffset? ProcessStartTime,
    string? ClassName,
    string? Title,
    string? CoordinateSpace,
    DesktopRect? RawBounds,
    DesktopRect? ClientBounds,
    int? Dpi,
    IReadOnlyList<string>? Errors,
    long? Dropped,
    bool? Truncated,
    string? StopReason,
    int? ExitCode,
    bool? NativeDropsReported);

public sealed record WindowEventMessage(
    string Kind,
    DateTimeOffset? ObservedAt,
    string? Event,
    string? Handle,
    int? ProcessId,
    DateTimeOffset? ProcessStartTime,
    bool? StartTimeReadable,
    string? ClassName,
    string? Title,
    string? CoordinateSpace,
    DesktopRect? RawBounds,
    DesktopRect? ClientBounds,
    int? Dpi,
    List<string>? Errors,
    int? ExitCode,
    long? HookDrops,
    string? Reason,
    string? ErrorCode,
    string? Error,
    int? HelperPid,
    DateTimeOffset? HelperStartTime,
    bool? IncludesNewWindows,
    bool? IncludesChildProcesses);

public sealed record WindowEventCaptureStatus(
    string Id,
    string State,
    long EventCount,
    long ByteCount,
    long Dropped,
    long CollectionErrors,
    bool Truncated,
    bool IncludesNewWindows,
    bool IncludesChildProcesses,
    DateTimeOffset StartedAt,
    DateTimeOffset? StoppedAt,
    string? StopReason,
    string? Error);

public sealed record WindowEventPage(
    string Id,
    long NextSequence,
    IReadOnlyList<WindowEventRecord> Events,
    string State,
    bool Completed,
    long Dropped,
    bool Truncated);

/// <summary>Turns untrusted helper lines into ordered capture records.</summary>
public sealed class WindowEventPipeline(WindowEventStart start)
{
    private readonly Dictionary<string, int> _dpi = new(StringComparer.OrdinalIgnoreCase);
    private int? _processId = start.ProcessId;
    private DateTimeOffset? _startTime = start.ExpectedStartTime;
    private string? _handle = string.IsNullOrWhiteSpace(start.Handle) ? null : start.Handle;
    private long _sequence;
    private long _hookDrops;
    private bool _ready;

    public long Dropped => _hookDrops;

    public long CollectionErrors { get; private set; }

    public string? StartErrorCode { get; private set; }

    public string? StartError { get; private set; }

    public bool SawCompletion { get; private set; }

    public string? CompletionReason { get; private set; }

    public int? HelperPid { get; private set; }

    public DateTimeOffset? HelperStartTime { get; private set; }

    public IReadOnlyList<WindowEventRecord> Accept(string line, DateTimeOffset receivedAt)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 16_384 || !line.StartsWith('{'))
        {
            return [Collection(receivedAt)];
        }

        WindowEventMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<WindowEventMessage>(line, SessionHelper.Json);
        }
        catch (JsonException)
        {
            return [Collection(receivedAt)];
        }

        if (message is null || string.IsNullOrWhiteSpace(message.Kind))
        {
            return [Collection(receivedAt)];
        }

        return message.Kind switch
        {
            "ready" => [Ready(message, receivedAt)],
            "observation" => Observations(message, receivedAt),
            "hook-dropped" => [NoteDropped(message.HookDrops ?? 0, receivedAt)],
            "completed" => [Completed(message.Reason ?? "completed", false, receivedAt)],
            "error" => [Failed(message, receivedAt)],
            _ => [Collection(receivedAt)]
        };
    }

    public WindowEventRecord Completed(string reason, bool truncated, DateTimeOffset receivedAt)
    {
        SawCompletion = true;
        CompletionReason ??= reason;
        return Next(receivedAt, null, "completed", null, null, null, null, null, null, null, null, null, null, null, null, null, _hookDrops, truncated, reason, null, false);
    }

    private WindowEventRecord Ready(WindowEventMessage message, DateTimeOffset receivedAt)
    {
        if (message.IncludesChildProcesses == true)
        {
            StartErrorCode = "invalid_request";
            StartError = "Window-event capture does not include child processes.";
            return Failed(message, receivedAt);
        }

        if (start.ProcessId is int requested && message.ProcessId != requested)
        {
            StartErrorCode = "window_identity_mismatch";
            StartError = "The capture process resolved a different process id.";
            return Failed(message, receivedAt);
        }

        if (start.ExpectedStartTime is DateTimeOffset expected
            && message.ProcessStartTime?.UtcDateTime != expected.UtcDateTime)
        {
            StartErrorCode = message.StartTimeReadable == false ? "process_identity_unreadable" : "process_identity_mismatch";
            StartError = "The process start time does not match expectedStartTime.";
            return Failed(message, receivedAt);
        }

        if (_handle is not null && !string.Equals(_handle, message.Handle, StringComparison.OrdinalIgnoreCase))
        {
            StartErrorCode = "window_identity_mismatch";
            StartError = "The capture process resolved a different window.";
            return Failed(message, receivedAt);
        }

        _processId ??= message.ProcessId;
        _startTime ??= message.ProcessStartTime;
        _handle ??= message.Handle;
        HelperPid = message.HelperPid;
        HelperStartTime = message.HelperStartTime;
        _ready = StartErrorCode is null;
        return Next(
            receivedAt,
            message.ObservedAt,
            "ready",
            null,
            "observed",
            start.IncludesNewWindows
                ? "Windows created later by this process are included. Child processes are not."
                : "Only the selected window is included. Windows created later are not.",
            message.Handle,
            message.ProcessId,
            message.ProcessStartTime,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false);
    }

    private WindowEventRecord[] Observations(WindowEventMessage message, DateTimeOffset receivedAt)
    {
        if (!_ready)
        {
            return [Collection(receivedAt)];
        }

        if (message.Event == "process-exited")
        {
            if (_processId is int pid && message.ProcessId != pid)
            {
                return [];
            }

            return
            [
                Next(receivedAt, message.ObservedAt, "event", "process-exited", "observed",
                    "The process handle was signaled or its exit code changed from STILL_ACTIVE. No exit cause was inferred.",
                    message.Handle, message.ProcessId, message.ProcessStartTime, null, null, null, null, null, null, null,
                    _hookDrops, null, null, message.ExitCode, false)
            ];
        }

        if (_handle is not null && !string.Equals(_handle, message.Handle, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (_processId is int expectedPid && message.ProcessId != expectedPid)
        {
            return [];
        }

        if (_startTime is DateTimeOffset expectedStart
            && message.StartTimeReadable == true
            && message.ProcessStartTime?.UtcDateTime != expectedStart.UtcDateTime)
        {
            return
            [
                Next(receivedAt, message.ObservedAt, "event", "identity-mismatch", "observed",
                    "The live process start time does not match the capture. The pid or window may have been reused.",
                    message.Handle, message.ProcessId, message.ProcessStartTime, null, null, null, null, null, null,
                    ["process_identity_mismatch"], null, null, null, null, false)
            ];
        }

        (string? space, DesktopRect? raw, DesktopRect? client, List<string> errors) = Geometry(message);
        WindowEventRecord observed = Next(
            receivedAt, message.ObservedAt, "event", message.Event, "observed", null, message.Handle, message.ProcessId,
            message.ProcessStartTime, UntrustedText.Trim(message.ClassName, 128), UntrustedText.Trim(message.Title, 256),
            space, raw, client, message.Dpi, errors, null, null, null, null, false);
        if (message.Handle is null || message.Dpi is not int dpi)
        {
            return [observed];
        }

        if (_dpi.TryGetValue(message.Handle, out int previous) && previous != dpi)
        {
            _dpi[message.Handle] = dpi;
            WindowEventRecord sampled = Next(
                receivedAt, message.ObservedAt, "event", "dpi-sampled", "sampled",
                "Successive GetDpiForWindow reads differed. This is not evidence that WM_DPICHANGED was received.",
                message.Handle, message.ProcessId, message.ProcessStartTime, null, null, space, null, null, dpi, null,
                null, null, null, null, false);
            return [observed, sampled];
        }

        _dpi[message.Handle] = dpi;
        return [observed];
    }

    private static (string? Space, DesktopRect? Raw, DesktopRect? Client, List<string> Errors) Geometry(WindowEventMessage message)
    {
        List<string> errors = message.Errors ?? [];
        bool physical = string.Equals(message.CoordinateSpace, DesktopCoordinates.PhysicalScreenPixels, StringComparison.Ordinal);
        if ((message.RawBounds is not null || message.ClientBounds is not null) && !physical)
        {
            errors.Add("coordinate_space_rejected");
            return (null, null, null, errors);
        }

        return (physical ? message.CoordinateSpace : null, message.RawBounds, message.ClientBounds, errors);
    }

    private WindowEventRecord NoteDropped(long count, DateTimeOffset receivedAt)
    {
        if (count > _hookDrops)
        {
            _hookDrops = count;
        }

        return Next(receivedAt, null, "status", "dropped", "observed",
            "The helper queue discarded events. WinEvent does not report its own lost events.",
            null, null, null, null, null, null, null, null, null, null, _hookDrops, null, null, null, false);
    }

    private WindowEventRecord Failed(WindowEventMessage message, DateTimeOffset receivedAt)
    {
        StartErrorCode ??= message.ErrorCode ?? "capture_failed";
        StartError ??= message.Error ?? "The window-event capture failed.";
        SawCompletion = true;
        CompletionReason ??= "failed";
        return Next(receivedAt, message.ObservedAt, "failed", null, "observed", null, null, null, null, null, null, null,
            null, null, null, [StartErrorCode], null, null, CompletionReason, null, false);
    }

    private WindowEventRecord Collection(DateTimeOffset receivedAt)
    {
        CollectionErrors++;
        return Next(receivedAt, null, "collection-error", null, "observed",
            "A capture line was rejected. Its contents were not kept.",
            null, null, null, null, null, null, null, null, null, ["untrusted_event"], null, null, null, null, false);
    }

    private WindowEventRecord Next(
        DateTimeOffset receivedAt,
        DateTimeOffset? observedAt,
        string kind,
        string? ev,
        string? origin,
        string? explanation,
        string? handle,
        int? processId,
        DateTimeOffset? processStart,
        string? className,
        string? title,
        string? coordinateSpace,
        DesktopRect? raw,
        DesktopRect? client,
        int? dpi,
        IReadOnlyList<string>? errors,
        long? dropped,
        bool? truncated,
        string? stopReason,
        int? exitCode,
        bool? nativeDropsReported) => new(
        ++_sequence,
        receivedAt,
        observedAt,
        kind,
        ev,
        origin,
        explanation,
        handle,
        processId,
        processStart,
        string.IsNullOrEmpty(className) ? null : className,
        string.IsNullOrEmpty(title) ? null : title,
        coordinateSpace,
        raw,
        client,
        dpi,
        errors is { Count: > 0 } ? errors : null,
        dropped,
        truncated,
        stopReason,
        exitCode,
        nativeDropsReported);
}

/// <summary>JSON for one helper line. Unknown fields are ignored.</summary>
public static class WindowEventLines
{
    public static string Observation(WindowEventMessage message) =>
        JsonSerializer.Serialize(message, SessionHelper.Json);

    public static bool TryParseHandle(string? text, out string handle)
    {
        handle = string.Empty;
        if (!WindowInspection.TryParseHandle(text, out HWND window))
        {
            return false;
        }

        handle = WindowInspection.FormatHandle(window);
        return true;
    }
}
