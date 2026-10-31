using Vestigo.Jobs;

namespace Vestigo.Processes;

/// <summary>Default <see cref="IProcessLauncher" />: service account via System.Diagnostics, console user via CreateProcessAsUser.</summary>
public sealed class ProcessLauncher : IProcessLauncher
{
    public OperationResult<bool> CheckAvailability(LaunchContext context, bool elevated) =>
        context switch
        {
            LaunchContext.Service => OperationResult<bool>.Ok(true),
            LaunchContext.Interactive when OperatingSystem.IsWindows() => InteractiveProcessRunner.CheckAvailability(elevated),
            _ => OperationResult<bool>.Fail("unsupported", "Interactive launch requires Windows.")
        };

    public Task<int> RunAsync(LaunchRequest request, JobContext output, CancellationToken cancellationToken) =>
        request.Context == LaunchContext.Interactive
            ? InteractiveProcessRunner.RunAsync(request, output, cancellationToken)
            : ServiceProcessRunner.RunAsync(request, output, cancellationToken);
}
