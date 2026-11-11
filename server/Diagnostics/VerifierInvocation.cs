using System.Diagnostics;

namespace Vestigo.Diagnostics;

/// <summary>Runs one fixed verifier command. Query and control share one gate.</summary>
internal static class VerifierInvocation
{
    internal static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<VerifierCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
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
            foreach (string argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                throw new VerifierQueryException("verifier_failed", "verifier.exe did not start.");
            }

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                Task<string> outputTask = process.StandardOutput.ReadToEndAsync(linked.Token);
                Task<string> errorTask = process.StandardError.ReadToEndAsync(linked.Token);
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                string output = await outputTask.ConfigureAwait(false);
                string error = await errorTask.ConfigureAwait(false);
                string text = string.IsNullOrEmpty(error) ? output : output + error;
                string bounded = VerifierStateParser.Bound(text, out bool truncated);
                return new VerifierCommandResult(process.ExitCode, bounded, truncated, false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
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

                return new VerifierCommandResult(null, string.Empty, false, true);
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
