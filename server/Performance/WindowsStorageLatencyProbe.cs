using System.Runtime.InteropServices;

using Windows.Win32.Storage.FileSystem;

namespace Vestigo.Performance;

/// <summary>Writes a temporary probe file and compares buffered, unbuffered, flush, and metadata latency.</summary>
public sealed class WindowsStorageLatencyProbe : IStorageLatencyProbe
{
    private const int Block = 4096;

    public async Task<OperationResult<StorageProbeResult>> ProbeAsync(
        string directory,
        long fileBytes,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<StorageProbeResult>.Fail("windows_required", "Storage latency probes require Windows.");
        }

        string path = Path.Combine(directory, $"vestigo-probe-{Guid.NewGuid():N}.tmp");
        bool cleaned = false;
        try
        {
            long size = Math.Max(Block, fileBytes / Block * Block);
            List<StorageProbeModeResult> modes = [];
            modes.Add(await RunModeAsync("buffered", path, size, FileOptions.None, false, duration, cancellationToken).ConfigureAwait(false));
            modes.Add(await RunModeAsync("unbuffered-write-through", path, size, FileOptions.WriteThrough | (FileOptions)0x20000000, false, duration, cancellationToken).ConfigureAwait(false));
            modes.Add(await RunFlushAsync(path, size, duration, cancellationToken).ConfigureAwait(false));
            modes.Add(await RunMetadataAsync(directory, duration, cancellationToken).ConfigureAwait(false));
            cleaned = Delete(path);
            return OperationResult<StorageProbeResult>.Ok(new StorageProbeResult(directory, cleaned, modes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            cleaned = Delete(path);
            return OperationResult<StorageProbeResult>.Fail("storage_probe_failed", ex.Message);
        }
        finally
        {
            if (!cleaned)
            {
                Delete(path);
            }
        }
    }

    private static async Task<StorageProbeModeResult> RunModeAsync(
        string mode,
        string path,
        long size,
        FileOptions options,
        bool flush,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        byte[] buffer = GC.AllocateArray<byte>(Block, true);
        List<double> samples = [];
        DateTimeOffset deadline = DateTimeOffset.UtcNow + duration;
        await using FileStream stream = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, Block, options);
        stream.SetLength(size);
        long ops = 0;
        long bytes = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = ops * Block % Math.Max(size, Block);
            long start = TimeProvider.System.GetTimestamp();
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (flush)
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            samples.Add(TimeProvider.System.GetElapsedTime(start).TotalMilliseconds);
            ops++;
            bytes += Block;
        }

        double seconds = Math.Max(duration.TotalSeconds, 0.001);
        return new StorageProbeModeResult(mode, (int)ops, ops / seconds, bytes / 1024.0 / 1024.0 / seconds, PercentileStats.From(samples));
    }

    private static async Task<StorageProbeModeResult> RunFlushAsync(string path, long size, TimeSpan duration, CancellationToken cancellationToken)
    {
        byte[] buffer = GC.AllocateArray<byte>(Block, true);
        List<double> samples = [];
        DateTimeOffset deadline = DateTimeOffset.UtcNow + duration;
        await using FileStream stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, Block, FileOptions.None);
        stream.SetLength(size);
        long ops = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            long start = TimeProvider.System.GetTimestamp();
            stream.Flush(true);
            samples.Add(TimeProvider.System.GetElapsedTime(start).TotalMilliseconds);
            ops++;
        }

        double seconds = Math.Max(duration.TotalSeconds, 0.001);
        return new StorageProbeModeResult("flush", (int)ops, ops / seconds, 0, PercentileStats.From(samples));
    }

    private static async Task<StorageProbeModeResult> RunMetadataAsync(string directory, TimeSpan duration, CancellationToken cancellationToken)
    {
        List<double> samples = [];
        DateTimeOffset deadline = DateTimeOffset.UtcNow + duration;
        int ops = 0;
        byte[] payload = "vestigo"u8.ToArray();
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(directory, $"vestigo-meta-{Guid.NewGuid():N}.tmp");
            long start = TimeProvider.System.GetTimestamp();
            await File.WriteAllBytesAsync(path, payload, cancellationToken).ConfigureAwait(false);
            File.Delete(path);
            samples.Add(TimeProvider.System.GetElapsedTime(start).TotalMilliseconds);
            ops++;
        }

        double seconds = Math.Max(duration.TotalSeconds, 0.001);
        return new StorageProbeModeResult("create-close", ops, ops / seconds, 0, PercentileStats.From(samples));
    }

    private static bool Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
