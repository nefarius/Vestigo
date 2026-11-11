using System.Runtime.CompilerServices;

using Vestigo.Configuration;
using Vestigo.Jobs;
using Vestigo.Processes;
using Vestigo.Security;

namespace Vestigo.Desktop;

/// <summary>Runs the window-event helper in the console session and reads its stdout line by line.</summary>
public sealed class HelperWindowEventSessionFactory(IProcessLauncher launcher) : IWindowEventSessionFactory
{
    public Task<OperationResult<IWindowEventSession>> StartAsync(WindowEventStart start, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(OperationResult<IWindowEventSession>.Fail("windows_required", "Window-event capture requires Windows."));
        }

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, start.Elevated);
        if (!available.Success)
        {
            return Task.FromResult(available.FailAs<IWindowEventSession>());
        }

        string directory;
        try
        {
            directory = StateDirectories.EnsureSubdirectory(Path.Combine(VestigoPaths.SpoolDirectory, "helper"));
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(OperationResult<IWindowEventSession>.Fail("untrusted_path", ex.Message));
        }

        string output = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".events");
        File.WriteAllBytes(output, []);
        (string executable, List<string> prefix) = SessionHelperClient.Host();
        List<string> arguments = [..prefix, ..SessionHelper.BuildWindowEventArguments(start)];
        LaunchRequest request = new(executable, arguments, Path.GetDirectoryName(executable), new Dictionary<string, string>(), LaunchContext.Interactive, start.Elevated, start.Desktop);
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        JobContext context = new("window-events", output, long.MaxValue / 4);
        Task<int> run = launcher.RunAsync(request, context, cancellation.Token);
        IWindowEventSession session = new HelperEventSession(output, run, cancellation);
        return Task.FromResult(OperationResult<IWindowEventSession>.Ok(session));
    }

    private sealed class HelperEventSession(string path, Task<int> run, CancellationTokenSource cancellation) : IWindowEventSession
    {
        public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is not null)
                {
                    if (line.Length > 0)
                    {
                        yield return line;
                    }

                    continue;
                }

                if (run.IsCompleted)
                {
                    yield break;
                }

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or ProcessLaunchException)
            {
                // The job object ends the helper. A launch failure is already on the captured task.
            }

            cancellation.Dispose();
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
