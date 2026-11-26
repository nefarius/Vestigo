using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;

namespace Vestigo.Performance;

/// <summary>Gated drop-only pktmon capture. Never returns packet payloads.</summary>
[McpServerToolType]
public sealed class PacketDropTools(IPacketDropCapture capture, VestigoOptions options)
{
    [McpServerTool(Name = "capture_packet_drops")]
    [Description(
        "Runs a bounded drop-only pktmon capture (metadata flags, no raw packets) and returns per-component and per-drop-reason counts. "
        + "TotalDropped is the ETL drop-reason count when format succeeds, including zero when that capture has no drop-reason rows; "
        + "component counters are a live snapshot immediately before stop. When format fails or the ETL is missing, TotalDropped falls back to that snapshot, Partial is true, and Error identifies the fallback. "
        + "Requires Vestigo:Performance:AllowPacketDropCapture. durationSeconds is clamped to 1..MaxPacketDropDuration. "
        + "A machine-global pktmon session is started; Vestigo stops orphan sessions it owns on startup. "
        + "ETL metadata is parsed and deleted. Packet payloads are never returned or persisted.")]
    public Task<OperationResult<PacketDropResult>> CapturePacketDrops(
        [Description("Capture window in seconds.")] int durationSeconds = 5,
        CancellationToken cancellationToken = default)
    {
        if (!options.Performance.AllowPacketDropCapture)
        {
            return Task.FromResult(OperationResult<PacketDropResult>.Fail(
                "packet_drop_disabled",
                "Packet-drop capture is disabled. Set Vestigo:Performance:AllowPacketDropCapture to true."));
        }

        int maxSeconds = (int)options.Performance.MaxPacketDropDuration.TotalSeconds;
        if (durationSeconds < 1 || durationSeconds > maxSeconds)
        {
            return Task.FromResult(OperationResult<PacketDropResult>.Fail(
                "invalid_request",
                $"durationSeconds must be between 1 and {maxSeconds}."));
        }

        return capture.CaptureAsync(TimeSpan.FromSeconds(durationSeconds), cancellationToken);
    }
}
