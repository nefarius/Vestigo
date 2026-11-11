using System.Diagnostics;

using Vestigo.Desktop;
using Vestigo.Jobs;

namespace Vestigo.Processes;

/// <summary>Cancels jobs Vestigo started on a test desktop, then kills any of those processes still running.</summary>
public sealed class JobOwnedProcessCleanup(JobManager jobs) : IOwnedProcessCleanup
{
    public IReadOnlyList<int> StillRunning(IReadOnlyList<OwnedDesktopProcess> processes)
    {
        List<int> alive = [];
        foreach (OwnedDesktopProcess process in processes)
        {
            if (process.ProcessId <= 4 || process.ProcessId == Environment.ProcessId)
            {
                continue;
            }

            try
            {
                using Process running = Process.GetProcessById(process.ProcessId);
                if (!running.HasExited && IsSameProcess(process, running))
                {
                    alive.Add(process.ProcessId);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // The id is already gone, or Windows has not published its exit yet and the next pass can see it.
            }
        }

        return alive;
    }

    public async Task TerminateAsync(IReadOnlyList<OwnedDesktopProcess> processes, CancellationToken cancellationToken)
    {
        foreach (OwnedDesktopProcess process in processes)
        {
            if (!string.IsNullOrWhiteSpace(process.JobId))
            {
                jobs.Cancel(process.JobId);
            }
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (StillRunning(processes).Count > 0 && DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        foreach (OwnedDesktopProcess process in processes)
        {
            if (!StillRunning([process]).Contains(process.ProcessId))
            {
                continue;
            }

            ProcessControl.Kill(process.ProcessId, null, entireTree: true, Environment.ProcessId);
        }
    }

    private static bool IsSameProcess(OwnedDesktopProcess owned, Process running)
    {
        if (owned.StartedAt is null || !ProcessStartTime.TryRead(running.SafeHandle.DangerousGetHandle(), out DateTimeOffset start, out _))
        {
            GC.KeepAlive(running);
            return false;
        }

        GC.KeepAlive(running);

        return start.UtcDateTime == owned.StartedAt.Value.UtcDateTime;
    }
}
