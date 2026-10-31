using System.Text;

using CliWrap;

namespace Vestigo.RemoteDebugger;

/// <summary>Result of a short-lived helper process.</summary>
internal sealed record CliOutput(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs helper executables through CliWrap with a hard timeout that kills the process tree.</summary>
internal static class CliRunner
{
    /// <summary>
    /// Runs <paramref name="executable"/> with discrete <paramref name="arguments"/> and captures output.
    /// Returns null when the timeout elapses; CliWrap then kills the whole process tree.
    /// Throws <see cref="System.ComponentModel.Win32Exception"/> when the executable cannot be started.
    /// </summary>
    public static CliOutput? Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, Encoding? outputEncoding = null)
    {
        StringBuilder stdout = new();
        StringBuilder stderr = new();
        Encoding encoding = outputEncoding ?? Console.OutputEncoding;
        Command command = Cli.Wrap(executable)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout, encoding))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr, encoding));

        using CancellationTokenSource cts = new(timeout);
        try
        {
            CommandResult result = command.ExecuteAsync(cts.Token).Task.GetAwaiter().GetResult();
            return new CliOutput(result.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}