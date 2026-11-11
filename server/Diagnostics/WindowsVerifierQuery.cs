using System.Diagnostics;
using System.Text;

namespace Vestigo.Diagnostics;

/// <summary>
///     Runs <c>verifier.exe /query</c> and <c>verifier.exe /querysettings</c> from System32, one at a time.
///     No other arguments are accepted.
/// </summary>
internal sealed class WindowsVerifierQuery : IVerifierQuery
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public Task<VerifierCommandResult> QueryAsync(CancellationToken cancellationToken) => RunAsync("/query", cancellationToken);

    public Task<VerifierCommandResult> QuerySettingsAsync(CancellationToken cancellationToken) => RunAsync("/querysettings", cancellationToken);

    private static async Task<VerifierCommandResult> RunAsync(string argument, CancellationToken cancellationToken)
    {
        string path = Path.Combine(Environment.SystemDirectory, "verifier.exe");
        if (!File.Exists(path))
        {
            throw new VerifierQueryException("verifier_not_found", "verifier.exe was not found in the Windows system directory.");
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using Process process = new();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start())
            {
                throw new VerifierQueryException("verifier_failed", "verifier.exe did not start.");
            }

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                (string output, bool outputTruncated) = await ReadBoundedAsync(process.StandardOutput, linked.Token).ConfigureAwait(false);
                (string error, bool errorTruncated) = await ReadBoundedAsync(process.StandardError, linked.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                string text = string.IsNullOrEmpty(error) ? output : output + error;
                string bounded = VerifierStateParser.Bound(text, out bool truncated);
                return new VerifierCommandResult(process.ExitCode, bounded, truncated || outputTruncated || errorTruncated, false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return new VerifierCommandResult(null, string.Empty, false, true);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        char[] buffer = new char[4096];
        StringBuilder builder = new();
        bool truncated = false;
        while (true)
        {
            int count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            int room = VerifierStateParser.MaxTextChars - builder.Length;
            if (room > 0)
            {
                builder.Append(buffer, 0, Math.Min(count, room));
            }

            if (count > room)
            {
                truncated = true;
            }
        }

        return (builder.ToString(), truncated);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
