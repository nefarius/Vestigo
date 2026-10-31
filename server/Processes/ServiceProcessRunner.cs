using System.ComponentModel;
using System.Diagnostics;
using System.Text;

using Vestigo.Jobs;

namespace Vestigo.Processes;

/// <summary>
///     Runs a process under the Vestigo service account with redirected output appended to the job spool.
/// </summary>
internal static class ServiceProcessRunner
{
    public static async Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(request.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (request.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string name, string value) in request.Environment)
        {
            startInfo.Environment[name] = value;
        }

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ProcessLaunchException("start_failed", "The process did not start.");
            }
        }
        catch (Win32Exception ex)
        {
            throw new ProcessLaunchException("start_failed", ex.Message);
        }

        process.StandardInput.Close();
        Task stdout = PumpAsync(process.StandardOutput, output);
        Task stderr = PumpAsync(process.StandardError, output);

        using CancellationTokenRegistration registration = cancellationToken.Register(() => TryKillTree(process));
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, JobContext output)
    {
        char[] buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            output.Write(new string(buffer, 0, read));
        }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already exited.
        }
    }
}
