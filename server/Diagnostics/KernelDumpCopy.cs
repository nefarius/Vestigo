using System.Security.Cryptography;

namespace Vestigo.Diagnostics;

/// <summary>Streams one selected dump into a new file and hashes the bytes that were written.</summary>
internal static class KernelDumpCopy
{
    public static async Task<OperationResult<KernelDumpCopied>> CopyAsync(
        string source,
        string destination,
        long expectedBytes,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024 * 1024];
        long written = 0;
        string? failure = null;
        string sha256 = "";
        using SHA256 hash = SHA256.Create();
        try
        {
            await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                written += read;
                if (written > maxBytes || written > expectedBytes)
                {
                    failure = "dump_too_large";
                    break;
                }

                hash.TransformBlock(buffer, 0, read, null, 0);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (failure is null)
            {
                hash.TransformFinalBlock([], 0, 0);
                sha256 = Convert.ToHexString(hash.Hash!).ToLowerInvariant();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException)
        {
            failure = "cancelled";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = "dump_failed";
        }

        if (failure is null && written != expectedBytes)
        {
            failure = "dump_size_changed";
        }

        if (failure is not null)
        {
            string error = failure switch
            {
                "dump_too_large" => "The dump is larger than the size recorded by list_kernel_dumps or Vestigo:Diagnostics:MaxDumpBytes.",
                "cancelled" => "The copy was cancelled.",
                "dump_size_changed" => "The dump length changed while it was copied.",
                _ => "The dump could not be copied."
            };
            if (!Delete(destination))
            {
                error += " cleanup_failed";
            }

            return OperationResult<KernelDumpCopied>.Fail(failure, error);
        }

        return OperationResult<KernelDumpCopied>.Ok(new KernelDumpCopied(written, sha256));
    }

    private static bool Delete(string destination)
    {
        try
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

internal sealed record KernelDumpCopied(long Bytes, string Sha256);
