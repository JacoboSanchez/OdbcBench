using OdbcBench.Bench;

namespace OdbcBench.Tests;

public class StatsTests
{
    [Theory]
    [InlineData(50, 5)]
    [InlineData(95, 10)]
    [InlineData(10, 1)]
    [InlineData(100, 10)]
    [InlineData(0, 1)]
    public void Percentile_uses_nearest_rank(double p, double expected)
    {
        double[] sorted = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        Assert.Equal(expected, Stats.Percentile(sorted, p));
    }

    [Fact]
    public void Percentile_of_even_count_takes_the_lower_middle()
    {
        Assert.Equal(2, Stats.Percentile(new double[] { 1, 2, 3, 4 }, 50));
    }

    [Fact]
    public void Series_stats_describe_the_samples()
    {
        var s = SeriesStats.From(new double[] { 2, 4, 4, 4, 5, 5, 7, 9 })!;
        Assert.Equal(8, s.Count);
        Assert.Equal(2, s.Min);
        Assert.Equal(9, s.Max);
        Assert.Equal(5, s.Mean, 10);
        Assert.Equal(4, s.P50);
        Assert.Equal(9, s.P95);
        Assert.Equal(Math.Sqrt(32.0 / 7), s.StdDev, 10); // sample standard deviation
        Assert.Equal(Math.Sqrt(32.0 / 7) / 5 * 100, s.Cv, 10);
    }

    [Fact]
    public void Outliers_are_flagged_with_the_worst_iteration_but_kept()
    {
        var values = new double[] { 10, 12, 11, 13, 100 };
        var indices = new[] { 1, 2, 3, 4, 5 };
        var s = SeriesStats.From(values, indices)!;
        Assert.Equal(1, s.Outliers);
        Assert.Equal(5, s.WorstIterationIndex);
        Assert.Equal(100, s.Max);          // not removed
        Assert.Equal(29.2, s.Mean, 10);    // still part of the mean
    }

    [Fact]
    public void Identical_samples_have_no_outliers_and_zero_spread()
    {
        var s = SeriesStats.From(new double[] { 7, 7, 7 })!;
        Assert.Equal(0, s.Outliers);
        Assert.Equal(-1, s.WorstIterationIndex);
        Assert.Equal(0, s.StdDev);
        Assert.Equal(0, s.Cv);
    }

    [Fact]
    public void No_samples_means_no_stats()
    {
        Assert.Null(SeriesStats.From(Array.Empty<double>()));
    }

    [Fact]
    public void Single_sample_has_zero_deviation()
    {
        var s = SeriesStats.From(new double[] { 3.5 })!;
        Assert.Equal(3.5, s.P50);
        Assert.Equal(3.5, s.P95);
        Assert.Equal(0, s.StdDev);
    }
}
