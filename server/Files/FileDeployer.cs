using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Vestigo.Files;

/// <summary>
///     Schedules file operations that Windows performs at the next reboot (<c>MoveFileEx</c> with
///     <c>MOVEFILE_DELAY_UNTIL_REBOOT</c>). Behind an interface so tests do not touch the registry.
/// </summary>
public interface IRebootScheduler
{
    /// <summary>Deletes <paramref name="path" /> at reboot. Returns 0 or a Win32 error code.</summary>
    int ScheduleDelete(string path);

    /// <summary>Moves <paramref name="source" /> over <paramref name="destination" /> at reboot. Returns 0 or a Win32 error code.</summary>
    int ScheduleReplace(string source, string destination);
}

/// <summary>What to store and how to treat an existing target.</summary>
public sealed record DeployRequest(
    string TargetPath,
    bool Overwrite = false,
    bool DeferToReboot = false,
    long MaxBytes = long.MaxValue,
    string? ExpectedSha256 = null);

/// <summary>Outcome of a deployment. AsideFile is the renamed previous file when the target was locked; AsideCleanup is scheduled_on_reboot or failed.</summary>
public sealed record DeployResult(
    string FullPath,
    long Size,
    string Sha256,
    bool ReplacedExisting,
    bool RebootRequired,
    string? AsideFile = null,
    string? AsideCleanup = null);

/// <summary>
///     Streams content to a temporary file next to the target, verifies size and hash, then commits with an
///     atomic replace. A locked target is renamed aside (running images can be renamed but not overwritten)
///     or, on request, replaced at the next reboot.
/// </summary>
public sealed partial class FileDeployer(IRebootScheduler scheduler, ILogger<FileDeployer>? logger = null)
{
    /// <summary>Atomic replace step. Tests substitute a failing one to exercise the rename-aside fallback.</summary>
    internal Action<string, string> ReplaceFile { get; init; } =
        (source, target) => File.Replace(source, target, null, ignoreMetadataErrors: true);

    private const string TempPrefix = ".vestigo-upload-";

    public async Task<OperationResult<DeployResult>> DeployAsync(
        Stream content,
        DeployRequest request,
        CancellationToken cancellationToken)
    {
        string? expected = null;
        if (request.ExpectedSha256 is not null)
        {
            expected = request.ExpectedSha256.Trim();
            if (!Sha256Pattern().IsMatch(expected))
            {
                return Fail("invalid_hash", "Expected SHA-256 must be 64 hexadecimal characters.");
            }

            expected = expected.ToUpperInvariant();
        }

        string target = Path.GetFullPath(request.TargetPath);
        string? directory = Path.GetDirectoryName(target);
        if (directory is null)
        {
            return Fail("invalid_path", "Target needs a file name.");
        }

        if (Directory.Exists(target))
        {
            return Fail("is_directory", $"Target is a directory: '{target}'.");
        }

        bool existed = File.Exists(target);
        if (existed && !request.Overwrite)
        {
            return Fail("already_exists", $"File already exists: '{target}'. Pass overwrite=true to replace it.");
        }

        string temp = Path.Combine(directory, TempPrefix + Guid.NewGuid().ToString("N") + ".tmp");
        bool keepTemp = false;
        try
        {
            Directory.CreateDirectory(directory);

            long size;
            string hash;
            using (IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using FileStream output = new(
                    temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                byte[] buffer = new byte[81920];
                size = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    size += read;
                    if (size > request.MaxBytes)
                    {
                        return Fail("too_large", $"Content exceeds the {request.MaxBytes} byte limit.");
                    }

                    hasher.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                hash = Convert.ToHexString(hasher.GetHashAndReset());
            }

            if (expected is not null && !string.Equals(expected, hash, StringComparison.Ordinal))
            {
                return Fail("hash_mismatch", $"SHA-256 mismatch: expected {expected}, received {hash}. Nothing was written.");
            }

            existed = File.Exists(target);
            if (!existed)
            {
                try
                {
                    File.Move(temp, target, overwrite: false);
                    return Ok(new DeployResult(target, size, hash, false, false));
                }
                catch (IOException) when (File.Exists(target))
                {
                    existed = true;
                    if (!request.Overwrite)
                    {
                        return Fail("already_exists", $"File already exists: '{target}'. Pass overwrite=true to replace it.");
                    }
                }
            }

            if (request.DeferToReboot)
            {
                int error = scheduler.ScheduleReplace(temp, target);
                if (error != 0)
                {
                    return Fail("reboot_schedule_failed", $"Could not schedule the replacement for reboot: {ErrorText(error)}");
                }

                keepTemp = true;
                return Ok(new DeployResult(target, size, hash, true, true));
            }

            try
            {
                ReplaceFile(temp, target);
                return Ok(new DeployResult(target, size, hash, true, false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogInformation(ex, "Replace of {Target} failed; renaming the old file aside", target);
            }

            return MoveAside(temp, target, size, hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("io_error", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (!keepTemp)
            {
                TryDelete(temp);
            }
        }
    }

    private OperationResult<DeployResult> MoveAside(string temp, string target, long size, string hash)
    {
        string aside = $"{target}.vestigo-old-{Guid.NewGuid():N}";
        try
        {
            File.Move(target, aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(
                "file_locked",
                $"'{target}' is locked and cannot be renamed ({ex.Message}). Retry with deferToReboot=true to replace it at the next reboot.");
        }

        try
        {
            File.Move(temp, target, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Move(aside, target);
            }
            catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
            {
                logger?.LogError(restore, "Could not restore {Target} from {Aside}", target, aside);
            }

            return Fail("io_error", $"Could not move the new file into place: {ex.Message}");
        }

        string cleanup;
        if (TryDelete(aside))
        {
            cleanup = "deleted";
            aside = string.Empty;
        }
        else if (scheduler.ScheduleDelete(aside) == 0)
        {
            cleanup = "scheduled_on_reboot";
        }
        else
        {
            cleanup = "failed";
        }

        return Ok(new DeployResult(target, size, hash, true, false, aside.Length == 0 ? null : aside, aside.Length == 0 ? null : cleanup));
    }

    internal static string ErrorText(int error) => $"{System.Runtime.InteropServices.Marshal.GetPInvokeErrorMessage(error)} (Win32 {error})";

    private static OperationResult<DeployResult> Ok(DeployResult value) => OperationResult<DeployResult>.Ok(value);

    private static OperationResult<DeployResult> Fail(string code, string message) =>
        OperationResult<DeployResult>.Fail(code, message);

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
