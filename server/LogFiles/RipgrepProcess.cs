using System.Runtime.CompilerServices;
using System.Text;

using CliWrap;
using CliWrap.EventStream;

namespace Vestigo.LogFiles;

/// <summary>Real <see cref="IRipgrepProcess" /> backed by CliWrap.</summary>
internal sealed class RipgrepProcess : IRipgrepProcess
{
    public async IAsyncEnumerable<RipgrepLine> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<int>? onExited,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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

        // ripgrep holds no state worth a graceful stop, so cancellation kills the process tree at once.
        await foreach (CommandEvent item in command.ListenAsync(
                           Encoding.UTF8,
                           Encoding.UTF8,
                           cancellationToken,
                           CancellationToken.None))
        {
            switch (item)
            {
                case StandardOutputCommandEvent stdout:
                    yield return new RipgrepLine(stdout.Text, false);
                    break;
                case StandardErrorCommandEvent stderr:
                    yield return new RipgrepLine(stderr.Text, true);
                    break;
                case ExitedCommandEvent exited:
                    onExited?.Invoke(exited.ExitCode);
                    break;
            }
        }
    }
}
