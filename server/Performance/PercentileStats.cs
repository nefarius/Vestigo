namespace Vestigo.Performance;

/// <summary>Percentile statistics over a bounded sample series. Pure; used by counters, jitter, I/O, and ETW aggregators.</summary>
public sealed record PercentileStats(int Count, double Min, double Average, double P50, double P95, double P99, double Max)
{
    public static PercentileStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    public static PercentileStats From(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0)
        {
            return Empty;
        }

        double[] sorted = samples.ToArray();
        Array.Sort(sorted);
        double sum = 0;
        foreach (double value in sorted)
        {
            sum += value;
        }

        return new PercentileStats(
            sorted.Length,
            sorted[0],
            sum / sorted.Length,
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99),
            sorted[^1]);
    }

    public static PercentileStats FromMilliseconds(IReadOnlyList<double> samples) => From(samples);

    /// <summary>Nearest-rank percentile. <paramref name="p"/> is in 0..1.</summary>
    public static double Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0)
        {
            return 0;
        }

        if (sortedAscending.Count == 1)
        {
            return sortedAscending[0];
        }

        double rank = Math.Clamp(p, 0, 1) * (sortedAscending.Count - 1);
        int low = (int)Math.Floor(rank);
        int high = Math.Min(low + 1, sortedAscending.Count - 1);
        double fraction = rank - low;
        return sortedAscending[low] + ((sortedAscending[high] - sortedAscending[low]) * fraction);
    }
}
