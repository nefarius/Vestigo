using System.Text;

using CliWrap;
using CliWrap.EventStream;

namespace Vestigo.DebugOutput;

/// <summary>Real <see cref="IDebugViewProcess" /> backed by CliWrap.</summary>
internal sealed class DebugViewProcess : IDebugViewProcess
{
    /// <summary>How long a graceful (Ctrl+C) stop may take before the process tree is killed.</summary>
    internal static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(2);

    public async IAsyncEnumerable<DebugViewLine> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<int>? onStarted,
        Action<int>? onExited,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Command command = Cli.Wrap(executable)
            .WithArguments(args =>
            {
                foreach (string argument in arguments)
                {
                    args.Add(argument);
                }
            })
            .WithValidation(CommandResultValidation.None);

        using CancellationTokenSource forcefulSource = new();
        using CancellationTokenRegistration escalation = cancellationToken.Register(
            static state => ((CancellationTokenSource)state!).CancelAfter(GracePeriod),
            forcefulSource);

        await foreach (CommandEvent item in command.ListenAsync(
                           Encoding.UTF8,
                           Encoding.UTF8,
                           forcefulSource.Token,
                           cancellationToken))
        {
            switch (item)
            {
                case StartedCommandEvent started:
                    onStarted?.Invoke(started.ProcessId);
                    break;
                case StandardOutputCommandEvent stdout:
                    yield return new DebugViewLine(stdout.Text, false);
                    break;
                case StandardErrorCommandEvent stderr:
                    yield return new DebugViewLine(stderr.Text, true);
                    break;
                case ExitedCommandEvent exited:
                    onExited?.Invoke(exited.ExitCode);
                    break;
            }
        }
    }
}
