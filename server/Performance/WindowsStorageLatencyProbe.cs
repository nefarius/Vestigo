using System.Runtime.InteropServices;

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
            modes.Add(await RunModeAsync("buffered", path, size, FileOptions.None, duration, cancellationToken).ConfigureAwait(false));
            modes.Add(await RunUnbufferedAsync(path, size, duration, cancellationToken).ConfigureAwait(false));
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
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        byte[] buffer = GC.AllocateArray<byte>(Block, true);
        List<double> samples = [];
        await using FileStream stream = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, Block, options);
        stream.SetLength(size);
        long ops = 0;
        long bytes = 0;
        long started = TimeProvider.System.GetTimestamp();
        while (TimeProvider.System.GetElapsedTime(started) < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = ops * Block % Math.Max(size, Block);
            long start = TimeProvider.System.GetTimestamp();
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            samples.Add(TimeProvider.System.GetElapsedTime(start).TotalMilliseconds);
            ops++;
            bytes += Block;
        }

        double seconds = Math.Max(TimeProvider.System.GetElapsedTime(started).TotalSeconds, 0.001);
        return new StorageProbeModeResult(mode, (int)ops, ops / seconds, bytes / 1024.0 / 1024.0 / seconds, PercentileStats.From(samples));
    }

    private static unsafe Task<StorageProbeModeResult> RunUnbufferedAsync(
        string path,
        long size,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        List<double> samples = [];
        byte* native = (byte*)NativeMemory.AlignedAlloc(Block, Block);
        try
        {
            Span<byte> buffer = new(native, Block);
            using FileStream stream = new(
                path,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                Block,
                FileOptions.WriteThrough | (FileOptions)0x20000000);
            stream.SetLength(size);
            long ops = 0;
            long bytes = 0;
            long started = TimeProvider.System.GetTimestamp();
            while (TimeProvider.System.GetElapsedTime(started) < duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Position = ops * Block % Math.Max(size, Block);
                long start = TimeProvider.System.GetTimestamp();
                stream.Write(buffer);
                samples.Add(TimeProvider.System.GetElapsedTime(start).TotalMilliseconds);
                ops++;
                bytes += Block;
            }

            double seconds = Math.Max(TimeProvider.System.GetElapsedTime(started).TotalSeconds, 0.001);
            return Task.FromResult(new StorageProbeModeResult(
                "unbuffered-write-through",
                (int)ops,
                ops / seconds,
                bytes / 1024.0 / 1024.0 / seconds,
                PercentileStats.From(samples)));
        }
        finally
        {
            NativeMemory.AlignedFree(native);
        }
    }

    private static async Task<StorageProbeModeResult> RunFlushAsync(string path, long size, TimeSpan duration, CancellationToken cancellationToken)
    {
        byte[] buffer = GC.AllocateArray<byte>(Block, true);
        List<double> samples = [];
        await using FileStream stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, Block, FileOptions.None);
        stream.SetLength(size);
        long ops = 0;
        long started = TimeProvider.System.GetTimestamp();
        while (TimeProvider.System.GetElapsedTime(started) < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = ops * Block % Math.Max(size, Block);
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            long start = TimeProvider.System.GetTimestamp();
            stream.Flush(true);
            samples.Add(TimeProvider.System.GetElapsedTime(start).TotalMilliseconds);
            ops++;
        }

        double seconds = Math.Max(TimeProvider.System.GetElapsedTime(started).TotalSeconds, 0.001);
        return new StorageProbeModeResult("flush", (int)ops, ops / seconds, 0, PercentileStats.From(samples));
    }

    private static async Task<StorageProbeModeResult> RunMetadataAsync(string directory, TimeSpan duration, CancellationToken cancellationToken)
    {
        List<double> samples = [];
        int ops = 0;
        byte[] payload = "vestigo"u8.ToArray();
        long started = TimeProvider.System.GetTimestamp();
        while (TimeProvider.System.GetElapsedTime(started) < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(directory, $"vestigo-meta-{Guid.NewGuid():N}.tmp");
            long start = TimeProvider.System.GetTimestamp();
            try
            {
                await File.WriteAllBytesAsync(path, payload, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            samples.Add(TimeProvider.System.GetElapsedTime(start).TotalMilliseconds);
            ops++;
        }

        double seconds = Math.Max(TimeProvider.System.GetElapsedTime(started).TotalSeconds, 0.001);
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
