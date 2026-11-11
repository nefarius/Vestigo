using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Etws;

namespace Vestigo.Tools;

/// <summary>Read-only ETW observation of how a process is presenting.</summary>
[McpServerToolType]
public sealed class PresentationTools(PresentationObserver observer)
{
    [McpServerTool(Name = "observe_presentation")]
    [Description(
        "Observes DXGI, DWM, and DxgKrnl events for one process for a bounded interval and reports composed, independent-flip, or exclusive only when an explicit event supports that mode. "
        + "A DXGI present by itself, window styles, and monitor-sized bounds are not evidence. "
        + "The result is unknown when any provider fails to enable, an event cannot be decoded, ETW reports lost events or buffers, events do not correlate, events were truncated, the evidence disagrees, or the call is cancelled. "
        + "Creates a short Vestigo- session and stops that session when the call ends. Duration is at most 30 seconds and cannot exceed Vestigo:MaxCaptureDuration.")]
    public Task<OperationResult<PresentationObservation>> ObservePresentation(
        [Description("Process id to correlate. Must be positive.")] int processId,
        [Description("Optional window handle. When set, only events that carry the same handle correlate.")] long? windowHandle = null,
        [Description("Observation length in milliseconds. Default 5000. Minimum 1. Maximum 30000, and also capped by Vestigo:MaxCaptureDuration.")] int durationMilliseconds = 5000,
        CancellationToken cancellationToken = default) =>
        observer.ObserveAsync(
            processId,
            windowHandle,
            TimeSpan.FromMilliseconds(durationMilliseconds),
            cancellationToken);
}
