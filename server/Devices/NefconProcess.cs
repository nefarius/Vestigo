using System.Diagnostics;
using System.Text;

namespace Vestigo.Devices;

/// <summary>Real <see cref="INefconProcess" /> backed by <see cref="Process" />.</summary>
internal sealed class NefconProcess : INefconProcess
{
    internal const int MaxOutputChars = 16 * 1024;

    public async Task<NefconProcessOutput> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        OutputTail tail = new(MaxOutputChars);
        process.OutputDataReceived += (_, e) => tail.Append(e.Data);
        process.ErrorDataReceived += (_, e) => tail.Append(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenSource timeoutSource = new(timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new NefconProcessOutput(-1, tail.ToString(), TimedOut: true);
        }

        // Parameterless WaitForExit lets the asynchronous output readers drain.
        process.WaitForExit();
        return new NefconProcessOutput(process.ExitCode, tail.ToString(), TimedOut: false);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already exited.
        }
    }

    /// <summary>Keeps only the last <c>capacity</c> characters of the output.</summary>
    internal sealed class OutputTail(int capacity)
    {
        private readonly StringBuilder _builder = new();
        private readonly object _gate = new();

        public void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_gate)
            {
                _builder.AppendLine(line);
                if (_builder.Length > capacity)
                {
                    _builder.Remove(0, _builder.Length - capacity);
                }
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return _builder.ToString().Trim();
            }
        }
    }
}