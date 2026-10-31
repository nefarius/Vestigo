using System.Text;

using CliWrap;

namespace Vestigo.Devices;

/// <summary>Real <see cref="INefconProcess" /> backed by CliWrap.</summary>
internal sealed class NefconProcess : INefconProcess
{
    internal const int MaxOutputChars = 16 * 1024;

    /// <summary>How long a graceful (Ctrl+C) stop may take before the process tree is killed.</summary>
    internal static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(2);

    public async Task<NefconProcessOutput> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        OutputTail tail = new(MaxOutputChars);
        PipeTarget sink = PipeTarget.ToDelegate(tail.Append, Encoding.UTF8);

        Command command = Cli.Wrap(executable)
            .WithArguments(args =>
            {
                foreach (string argument in arguments)
                {
                    args.Add(argument);
                }
            })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(sink)
            .WithStandardErrorPipe(sink);

        using CancellationTokenSource timeoutSource = new(timeout);
        using CancellationTokenSource gracefulSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using CancellationTokenSource forcefulSource = new();
        // Ask the process to stop first; escalate to killing the whole process tree after the grace period.
        using CancellationTokenRegistration escalation = gracefulSource.Token.Register(
            static state => ((CancellationTokenSource)state!).CancelAfter(GracePeriod),
            forcefulSource);

        try
        {
            CommandResult result = await command
                .ExecuteAsync(forcefulSource.Token, gracefulSource.Token)
                .ConfigureAwait(false);
            return new NefconProcessOutput(result.ExitCode, tail.ToString(), TimedOut: false);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new NefconProcessOutput(-1, tail.ToString(), TimedOut: true);
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