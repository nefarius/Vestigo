using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Performance;

/// <summary>Gated 4K storage latency probe inside a Write root.</summary>
[McpServerToolType]
public sealed class PerformanceStorageProbeTools(
    IStorageLatencyProbe probe,
    RootPolicy roots,
    VestigoOptions options)
{
    [McpServerTool(Name = "probe_storage_latency")]
    [Description(
        "Creates a temporary probe file in a directory with Write access and compares buffered, unbuffered write-through, "
        + "FlushFileBuffers, and create/close metadata latency. Requires Vestigo:Performance:AllowStorageProbes. "
        + "The probe file is deleted afterwards.")]
    public Task<OperationResult<StorageProbeResult>> ProbeStorageLatency(
        [Description("Existing directory in a root with Write access.")] string directory,
        [Description("Temporary file size in bytes.")] long fileBytes = 4L * 1024 * 1024,
        [Description("Per-mode window in seconds.")] int durationSeconds = 3,
        CancellationToken cancellationToken = default)
    {
        if (!options.Performance.AllowStorageProbes)
        {
            return Task.FromResult(OperationResult<StorageProbeResult>.Fail(
                "storage_probes_disabled",
                "Storage latency probes are disabled. Set Vestigo:Performance:AllowStorageProbes to true."));
        }

        if (fileBytes < 4096 || fileBytes > options.Performance.MaxStorageProbeBytes)
        {
            return Task.FromResult(OperationResult<StorageProbeResult>.Fail(
                "invalid_request",
                $"fileBytes must be between 4096 and {options.Performance.MaxStorageProbeBytes}."));
        }

        TimeSpan duration = TimeSpan.FromSeconds(durationSeconds);
        if (durationSeconds < 1 || duration > options.Performance.MaxStorageProbeDuration)
        {
            return Task.FromResult(OperationResult<StorageProbeResult>.Fail(
                "invalid_request",
                $"durationSeconds must be at least 1 and no more than {(int)options.Performance.MaxStorageProbeDuration.TotalSeconds}."));
        }

        PathCheck located = roots.ResolveExisting(directory, RootAccess.Write);
        if (!located.Success)
        {
            return Task.FromResult(OperationResult<StorageProbeResult>.Fail(located.ErrorCode!, located.Error!));
        }

        if (!Directory.Exists(located.FullPath))
        {
            return Task.FromResult(OperationResult<StorageProbeResult>.Fail("invalid_path", "directory must be an existing directory in a root with Write access."));
        }

        return probe.ProbeAsync(located.FullPath!, fileBytes, duration, cancellationToken);
    }
}
