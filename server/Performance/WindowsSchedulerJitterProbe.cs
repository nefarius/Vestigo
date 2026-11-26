using Windows.Win32;

namespace Vestigo.Performance;

/// <summary>Measures timer wake-drift and tight-spin preemption gaps with QueryPerformanceCounter.</summary>
public sealed class WindowsSchedulerJitterProbe : ISchedulerJitterProbe
{
    public async Task<OperationResult<JitterProbeResult>> ProbeAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<JitterProbeResult>.Fail("windows_required", "Scheduler jitter probing requires Windows.");
        }

        if (!PInvoke.QueryPerformanceFrequency(out long frequency) || frequency <= 0)
        {
            return OperationResult<JitterProbeResult>.Fail("qpc_unavailable", "QueryPerformanceFrequency failed.");
        }

        List<double> wake = [];
        List<double> spin = [];
        long started = TimeProvider.System.GetTimestamp();
        while (TimeProvider.System.GetElapsedTime(started) < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            wake.Add(await MeasureSleepDriftAsync(frequency, TimeSpan.FromMilliseconds(15), cancellationToken).ConfigureAwait(false));
            if (TimeProvider.System.GetElapsedTime(started) >= duration)
            {
                break;
            }

            spin.AddRange(MeasureSpinGaps(frequency, TimeSpan.FromMilliseconds(50), cancellationToken));
        }

        int over2 = spin.Count(value => value > 2);
        int over10 = spin.Count(value => value > 10);
        int over50 = spin.Count(value => value > 50);
        return OperationResult<JitterProbeResult>.Ok(new JitterProbeResult(
            (int)duration.TotalMilliseconds,
            wake.Count + spin.Count,
            PercentileStats.From(wake),
            PercentileStats.From(spin),
            over2,
            over10,
            over50));
    }

    private static async Task<double> MeasureSleepDriftAsync(long frequency, TimeSpan intended, CancellationToken cancellationToken)
    {
        PInvoke.QueryPerformanceCounter(out long start);
        await Task.Delay(intended, cancellationToken).ConfigureAwait(false);
        PInvoke.QueryPerformanceCounter(out long end);
        double actualMs = (end - start) * 1000.0 / frequency;
        return Math.Max(0, actualMs - intended.TotalMilliseconds);
    }

    private static IReadOnlyList<double> MeasureSpinGaps(long frequency, TimeSpan window, CancellationToken cancellationToken)
    {
        List<double> gaps = [];
        PInvoke.QueryPerformanceCounter(out long start);
        long previous = start;
        long until = start + (long)(window.TotalSeconds * frequency);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PInvoke.QueryPerformanceCounter(out long now);
            double gapMs = (now - previous) * 1000.0 / frequency;
            if (gapMs > 0.05)
            {
                gaps.Add(gapMs);
            }

            previous = now;
            if (now >= until)
            {
                break;
            }
        }

        return gaps;
    }
}
