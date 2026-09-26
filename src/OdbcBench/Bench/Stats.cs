namespace OdbcBench.Bench;

/// <summary>Descriptive statistics of one metric over the measured iterations of a series.</summary>
public sealed class SeriesStats
{
    public int Count { get; init; }
    public double Min { get; init; }
    /// <summary>Nearest-rank median.</summary>
    public double P50 { get; init; }
    public double Mean { get; init; }
    /// <summary>Nearest-rank 95th percentile.</summary>
    public double P95 { get; init; }
    public double Max { get; init; }
    /// <summary>Sample standard deviation.</summary>
    public double StdDev { get; init; }
    /// <summary>Coefficient of variation in percent (StdDev / Mean * 100).</summary>
    public double Cv { get; init; }
    /// <summary>Samples above P50 + 3 * 1.4826 * MAD. Never removed from the statistics, only flagged.</summary>
    public int Outliers { get; init; }
    /// <summary>Iteration index of the largest outlier, or -1.</summary>
    public int WorstIterationIndex { get; init; } = -1;

    public static SeriesStats? From(IReadOnlyList<double> values, IReadOnlyList<int>? iterationIndices = null)
    {
        int n = values.Count;
        if (n == 0) return null;

        var sorted = values.ToArray();
        Array.Sort(sorted);
        double mean = sorted.Average();
        double p50 = Stats.Percentile(sorted, 50);
        double stdDev = n > 1 ? Math.Sqrt(sorted.Sum(v => (v - mean) * (v - mean)) / (n - 1)) : 0;
        double mad = Stats.Mad(sorted, p50);
        double threshold = p50 + 3 * 1.4826 * mad;

        int outliers = 0;
        int worst = -1;
        double worstValue = double.MinValue;
        if (mad > 0)
        {
            for (int i = 0; i < n; i++)
            {
                if (values[i] <= threshold) continue;
                outliers++;
                if (values[i] > worstValue)
                {
                    worstValue = values[i];
                    worst = iterationIndices != null ? iterationIndices[i] : i;
                }
            }
        }

        return new SeriesStats
        {
            Count = n,
            Min = sorted[0],
            P50 = p50,
            Mean = mean,
            P95 = Stats.Percentile(sorted, 95),
            Max = sorted[^1],
            StdDev = stdDev,
            Cv = mean > 0 ? stdDev / mean * 100 : 0,
            Outliers = outliers,
            WorstIterationIndex = worst,
        };
    }
}

public static class Stats
{
    /// <summary>Nearest-rank percentile of a sorted array: the value at rank ceil(p/100 * n).</summary>
    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return double.NaN;
        int rank = (int)Math.Ceiling(p / 100.0 * sorted.Length);
        int index = Math.Clamp(rank - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    public static double Median(double[] sorted) => Percentile(sorted, 50);

    /// <summary>Median absolute deviation around the given median.</summary>
    public static double Mad(double[] sorted, double median)
    {
        var deviations = sorted.Select(v => Math.Abs(v - median)).ToArray();
        Array.Sort(deviations);
        return Percentile(deviations, 50);
    }
}
