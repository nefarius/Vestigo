using System.Runtime.CompilerServices;
using System.Threading.Channels;

using CliWrap;

using Vestigo.Configuration;

namespace Vestigo.LogFiles;

/// <summary>Real <see cref="IRipgrepProcess" /> backed by CliWrap.</summary>
internal sealed class RipgrepProcess(VestigoOptions options) : IRipgrepProcess
{
    public async IAsyncEnumerable<RipgrepLine> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<int>? onExited,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int maxBytes = RipgrepLineReader.RecordByteLimit(options.Logs.MaxSearchLineLength);
        Channel<RipgrepLine> lines = Channel.CreateUnbounded<RipgrepLine>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        Command command = Cli.Wrap(executable)
            .WithArguments(args =>
            {
                foreach (string argument in arguments)
                {
                    args.Add(argument);
                }
            })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(new BoundedLinePipe(lines.Writer, false, maxBytes))
            .WithStandardErrorPipe(new BoundedLinePipe(lines.Writer, true, maxBytes));

        // ripgrep holds no state worth a graceful stop, so cancellation kills the process tree at once.
        Task<int> running = RunProcessAsync(command, lines.Writer, cancellationToken);
        try
        {
            await foreach (RipgrepLine line in lines.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return line;
            }
        }
        finally
        {
            try
            {
                onExited?.Invoke(await running.ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task<int> RunProcessAsync(
        Command command,
        ChannelWriter<RipgrepLine> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            CommandResult result = await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return result.ExitCode;
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private sealed class BoundedLinePipe(ChannelWriter<RipgrepLine> writer, bool isError, int maxBytes) : PipeTarget
    {
        public override async Task CopyFromAsync(Stream source, CancellationToken cancellationToken = default)
        {
            await foreach (RipgrepLine line in RipgrepLineReader.ReadAsync(source, isError, maxBytes, cancellationToken)
                               .ConfigureAwait(false))
            {
                await writer.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
