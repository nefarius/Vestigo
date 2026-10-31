using System.ComponentModel;
using System.Text;

using CliWrap;

using Vestigo.Jobs;

namespace Vestigo.Processes;

/// <summary>
///     Runs a process under the Vestigo service account through CliWrap with output appended to the job spool.
/// </summary>
internal static class ServiceProcessRunner
{
    public static async Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken)
    {
        Command command = Cli.Wrap(request.Executable)
            .WithArguments(request.Arguments, escape: true)
            .WithEnvironmentVariables(builder =>
            {
                foreach ((string name, string value) in request.Environment)
                {
                    builder.Set(name, value);
                }
            })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(new JobOutputPipe(output))
            .WithStandardErrorPipe(new JobOutputPipe(output));
        if (request.WorkingDirectory is not null)
        {
            command = command.WithWorkingDirectory(request.WorkingDirectory);
        }

        try
        {
            // CliWrap kills the whole process tree when the forceful token is cancelled and throws
            // OperationCanceledException once the process is gone.
            CommandResult result = await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return result.ExitCode;
        }
        catch (Win32Exception ex)
        {
            throw new ProcessLaunchException("start_failed", ex.Message);
        }
    }

    /// <summary>Decodes UTF-8 chunks and appends them to the job spool, which enforces the output cap.</summary>
    private sealed class JobOutputPipe(JobContext output) : PipeTarget
    {
        public override async Task CopyFromAsync(Stream source, CancellationToken cancellationToken = default)
        {
            using StreamReader reader = new(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            char[] buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                output.Write(new string(buffer, 0, read));
            }
        }
    }
}