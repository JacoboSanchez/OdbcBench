using System.Globalization;
using OdbcBench.Fetch;
using OdbcBench.Report;

namespace OdbcBench.Bench;

/// <summary>Turns raw samples into statistics, baseline ratios, consistency verdicts and the run outcome.</summary>
public static class Analysis
{
    public const double NoisyCvPercent = 20;

    public static void Summarize(RunResult run, int expectedIterations)
    {
        foreach (var s in run.Series) SummarizeSeries(s, expectedIterations);
        ComputeRatios(run);
        ComputeConsistency(run);
        ComputeOutcome(run);
    }

    public static void SummarizeSeries(SeriesResult s, int expectedIterations)
    {
        var measured = s.Samples.Where(x => !x.Warmup).ToList();
        var ok = measured.Where(x => x.Ok).ToList();
        s.OkCount = ok.Count;
        s.FailedCount = measured.Count - ok.Count;

        if (s.Status is not ("abandoned" or "not run"))
        {
            s.Status = ok.Count == 0 ? (measured.Count == 0 ? "not run" : "failed")
                : s.FailedCount > 0 ? "partial"
                : ok.Count < expectedIterations ? "incomplete"
                : "ok";
        }

        var allOk = s.Samples.Where(x => x.Ok).ToList();
        s.ChecksumStable = allOk.Select(x => x.Checksum).Distinct().Count() <= 1;
        s.Checksum = allOk.Count > 0 ? allOk[^1].Checksum : null;
        if (allOk.Count > 0 && s.EffectiveBlockSize == 0) s.EffectiveBlockSize = allOk[^1].EffectiveBlockSize;

        if (ok.Count == 0) return;

        var indices = ok.Select(x => x.Index).ToList();
        s.Total = SeriesStats.From(ok.Select(x => x.TotalMs).ToList(), indices);
        s.Execute = SeriesStats.From(ok.Select(x => x.ExecuteMs).ToList(), indices);
        s.FirstBatch = SeriesStats.From(ok.Select(x => x.FirstBatchMs).ToList(), indices);
        s.Fetch = SeriesStats.From(ok.Select(x => x.FetchMs).ToList(), indices);
        s.Close = SeriesStats.From(ok.Select(x => x.CloseMs).ToList(), indices);
        s.Cpu = SeriesStats.From(ok.Select(x => x.CpuMs).ToList(), indices);
        s.Describe = ok.Any(x => x.DescribeMs > 0) ? SeriesStats.From(ok.Select(x => x.DescribeMs).ToList(), indices) : null;
        s.Connect = ok.Any(x => x.ConnectMs > 0) ? SeriesStats.From(ok.Select(x => x.ConnectMs).ToList(), indices) : null;

        s.Rows = (long)Median(ok.Select(x => (double)x.Rows));
        s.RowsVary = ok.Any(x => x.Rows != ok[0].Rows);
        s.MedianBytes = Median(ok.Select(x => (double)x.Bytes));
        s.RowsPerSecond = Median(ok.Select(x => x.TotalMs > 0 ? x.Rows / (x.TotalMs / 1000.0) : 0));
        s.MegabytesPerSecond = Median(ok.Select(x => x.TotalMs > 0 ? x.Bytes / 1e6 / (x.TotalMs / 1000.0) : 0));
        s.CpuPercent = Median(ok.Select(x => x.TotalMs > 0 ? x.CpuMs / x.TotalMs * 100 : 0));
        s.Truncations = ok.Max(x => x.Truncations);
        s.GcCollections = ok.Sum(x => x.Gc0 + x.Gc1 + x.Gc2);
        s.AllocatedBytes = ok.Max(x => x.AllocatedBytes);
        s.Noisy = s.Total!.Count >= 3 && s.Total.Cv > NoisyCvPercent;

        if (s.Harness != null && s.Total.P50 > 0)
        {
            s.Harness.EstimatedMsPerIteration = s.Rows * (double)s.Harness.BoundColumns * s.Harness.NsPerValue / 1e6;
            s.Harness.PercentOfTotal = s.Harness.EstimatedMsPerIteration / s.Total.P50 * 100;
        }
    }

    public static void ComputeRatios(RunResult run)
    {
        foreach (var group in run.Series.GroupBy(s => s.BlockSize))
        {
            var baseline = group.FirstOrDefault(s => string.Equals(s.DsnName, run.BaselineDsn, StringComparison.OrdinalIgnoreCase));
            foreach (var s in group)
            {
                s.IsBaseline = ReferenceEquals(s, baseline);
                if (baseline?.Total is { P50: > 0 } b && s.Total != null)
                {
                    s.RatioToBaseline = s.Total.P50 / b.P50;
                    s.PercentVsBaseline = (s.RatioToBaseline - 1) * 100;
                }
                else
                {
                    s.RatioToBaseline = null;
                    s.PercentVsBaseline = null;
                }
            }
        }
    }

    public static void ComputeConsistency(RunResult run)
    {
        var c = new ConsistencyResult();
        var withData = run.Series.Where(s => s.Samples.Any(x => x.Ok)).ToList();

        // Row counts: every successful iteration (warmups included) of every DSN and block size.
        var rowCounts = withData
            .SelectMany(s => s.Samples.Where(x => x.Ok).Select(x => (Series: s, x.Rows)))
            .ToList();
        var distinctRows = rowCounts.Select(r => r.Rows).Distinct().ToList();
        c.RowCountsMatch = distinctRows.Count <= 1;
        c.Rows = distinctRows.Count == 1 ? distinctRows[0] : null;
        if (!c.RowCountsMatch)
        {
            foreach (var s in withData)
            {
                var counts = s.Samples.Where(x => x.Ok).Select(x => x.Rows).Distinct().OrderBy(x => x).ToList();
                c.Notes.Add($"{s.Key}: {string.Join(" / ", counts.Select(n => n.ToString("N0", CultureInfo.InvariantCulture)))} rows");
            }
        }

        // Checksums stable across the iterations of each series.
        foreach (var s in withData.Where(s => !s.ChecksumStable))
        {
            c.ChecksumsStable = false;
            int distinct = s.Samples.Where(x => x.Ok).Select(x => x.Checksum).Distinct().Count();
            c.Notes.Add($"{s.Key}: {distinct} different checksums across iterations (the data or its order changed between executions)");
        }

        // Same DSN, different block sizes: the per-column row-order hash does not depend on the block size.
        foreach (var dsn in withData.GroupBy(s => s.DsnName))
        {
            var sums = dsn.Where(s => s.ChecksumStable && s.Checksum != null).Select(s => (s.BlockSize, s.Checksum)).ToList();
            if (sums.Select(x => x.Checksum).Distinct().Count() > 1)
            {
                c.BlockSizeChecksumsEqual = false;
                c.Notes.Add($"{dsn.Key}: checksums differ between block sizes ({string.Join(", ", sums.Select(x => $"{x.BlockSize}: {x.Checksum}"))}); " +
                            "the driver returned different data depending on the row array size");
            }
        }

        // Across DSNs (informational): only equal when the drivers return byte-identical values in the same C types.
        foreach (var group in withData.GroupBy(s => s.BlockSize))
        {
            var sums = group.Where(s => s.Checksum != null).Select(s => s.Checksum).Distinct().Count();
            if (sums > 1) c.CrossDsnChecksumsEqual = false;
        }

        run.Consistency = c;
    }

    public static void ComputeOutcome(RunResult run)
    {
        bool anyConnected = run.Dsns.Any(d => d.Status == "connected");
        bool strictStop = run.Validation?.StrictAbort == true;
        bool problems =
            run.Dsns.Any(d => d.Status != "connected") ||
            run.Series.Any(s => s.Status != "ok") ||
            !run.Consistency.RowCountsMatch ||
            run.Validation?.Status == "FAIL";

        if (run.Fatal)
        {
            run.ExitCode = 3;
            if (string.IsNullOrEmpty(run.Outcome)) run.Outcome = "failed";
        }
        else if (!anyConnected)
        {
            run.ExitCode = 3;
            run.Outcome = "no DSN could be connected";
        }
        else if (strictStop)
        {
            run.ExitCode = 2;
            run.Outcome = "stopped after validation (FAIL in strict mode); nothing was benchmarked";
        }
        else if (run.Interrupted)
        {
            run.ExitCode = 1;
            run.Outcome = "interrupted; partial results";
        }
        else if (problems)
        {
            run.ExitCode = 1;
            run.Outcome = "completed with problems";
        }
        else
        {
            run.ExitCode = 0;
            run.Outcome = "completed";
        }
    }

    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.ToArray();
        if (sorted.Length == 0) return 0;
        Array.Sort(sorted);
        return Stats.Percentile(sorted, 50);
    }
}
