using System.Runtime.CompilerServices;
using OdbcBench.Bench;
using OdbcBench.Report;

namespace OdbcBench.Tests;

public class AnalysisAndReportTests
{
    [Fact]
    public void Statistics_ratios_and_statuses_are_computed()
    {
        var run = SampleRun.Create();
        var legacy = run.Series.Single(s => s.DsnName == "legacy" && s.BlockSize == 1000);
        var flight = run.Series.Single(s => s.DsnName == "flight" && s.BlockSize == 1000);

        Assert.True(legacy.IsBaseline);
        Assert.Equal("ok", legacy.Status);
        Assert.Equal(5, legacy.OkCount);
        Assert.Equal(808, legacy.Total!.P50, 6);          // 800 x 1.01 (nearest-rank median of 5)
        Assert.Equal(606.0 / 808.0, flight.RatioToBaseline!.Value, 9);
        Assert.Equal(500, flight.EffectiveBlockSize);

        var failing = run.Series.Single(s => s.DsnName == "flight" && s.BlockSize == 1);
        Assert.Equal("partial", failing.Status);
        Assert.Equal(1, failing.FailedCount);
        Assert.Equal(4, failing.OkCount);
    }

    [Fact]
    public void Warmups_never_enter_the_statistics()
    {
        var run = SampleRun.Create();
        var legacy = run.Series.Single(s => s.DsnName == "legacy" && s.BlockSize == 1000);
        Assert.True(legacy.Total!.Max < 1200); // the warmup took 1,200 ms
        Assert.Equal(5, legacy.Total.Count);
    }

    [Fact]
    public void Harness_estimate_scales_with_rows_and_columns()
    {
        var run = SampleRun.Create();
        var h = run.Series[0].Harness!;
        Assert.Equal(100_000 * 2 * 4.5 / 1e6, h.EstimatedMsPerIteration, 9);
        Assert.Equal(h.EstimatedMsPerIteration / run.Series[0].Total!.P50 * 100, h.PercentOfTotal, 9);
    }

    [Fact]
    public void Consistency_flags_follow_the_samples()
    {
        var run = SampleRun.Create();
        Assert.True(run.Consistency.RowCountsMatch);
        Assert.Equal(100_000, run.Consistency.Rows);
        Assert.True(run.Consistency.ChecksumsStable);
        Assert.True(run.Consistency.BlockSizeChecksumsEqual);
        Assert.False(run.Consistency.CrossDsnChecksumsEqual);

        run.Series[1].Samples[2].Rows = 99_999;
        run.Series[2].Samples[4].Checksum = "ffffffffffffffff";
        Analysis.Summarize(run, SampleRun.Iterations);
        Assert.False(run.Consistency.RowCountsMatch);
        Assert.False(run.Consistency.ChecksumsStable);
        Assert.Contains(run.Consistency.Notes, n => n.Contains("99,999"));
    }

    [Fact]
    public void Outcome_and_exit_code()
    {
        var run = SampleRun.Create();
        Assert.Equal(1, run.ExitCode); // a DSN failed to connect, a series is partial, validation FAIL
        Assert.Equal("completed with problems", run.Outcome);

        run.Validation!.StrictAbort = true;
        Analysis.ComputeOutcome(run);
        Assert.Equal(2, run.ExitCode);

        run.Validation.StrictAbort = false;
        foreach (var d in run.Dsns) d.Status = "not run";
        Analysis.ComputeOutcome(run);
        Assert.Equal(3, run.ExitCode);
    }

    [Fact]
    public void Clean_run_exits_zero()
    {
        var run = SampleRun.Create();
        run.Dsns.RemoveAll(d => d.Status != "connected");
        run.Series.RemoveAll(s => s.BlockSize == 1);
        run.Validation!.Issues.Clear();
        Analysis.Summarize(run, SampleRun.Iterations);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("completed", run.Outcome);
    }

    [Fact]
    public void Markdown_contains_every_section()
    {
        string md = MarkdownReportWriter.Render(SampleRun.Create());
        foreach (var heading in new[]
                 {
                     "# ODBC driver performance comparison", "## Summary", "## Environment", "### DSNs and drivers", "## Query",
                     "## Validation (first 10 rows)", "### Differing values", "## Column bindings", "## Results: block size 1,000",
                     "## Results: block size 1", "### Total time per iteration (ms)", "### Where the time goes (p50, ms)",
                     "### Throughput and resources (medians per iteration)", "## Data consistency", "### Harness overhead",
                     "## Warnings and errors", "## Appendix: every iteration",
                 })
            Assert.Contains(heading + "\n", md);

        Assert.Contains("| legacy (baseline) |", md);
        Assert.Contains("0.75×", md);                         // flight vs legacy at block 1000
        Assert.Contains("name: `Ana ` → `Ana\\|`", md);        // diff values keep spaces, pipes escaped
        Assert.Contains("Hint: DSN 'Nope' is not defined", md);
        Assert.Contains("driver changed SQL\\_ATTR\\_ROW\\_ARRAY\\_SIZE from 1000 to 500", md);
        Assert.DoesNotContain("token=abc", md);
    }

    [Fact]
    public void Markdown_tables_are_well_formed()
    {
        string md = MarkdownReportWriter.Render(SampleRun.Create());
        var lines = md.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("| ") || i + 1 >= lines.Length || !lines[i + 1].StartsWith("| ---")) continue;
            int columns = CountCells(lines[i]);
            for (int j = i + 1; j < lines.Length && lines[j].StartsWith("|"); j++)
                Assert.True(columns == CountCells(lines[j]), $"line {j + 1} has {CountCells(lines[j])} cells, header has {columns}: {lines[j]}");
        }
    }

    [Fact]
    public void Json_round_trip_renders_the_same_report()
    {
        var run = SampleRun.Create();
        string path = Path.Combine(Path.GetTempPath(), $"odbcbench-{Guid.NewGuid():N}.json");
        try
        {
            JsonResultWriter.Write(run, path);
            var back = JsonResultWriter.Read(path);
            Assert.Equal(MarkdownReportWriter.Render(run), MarkdownReportWriter.Render(back));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Markdown_matches_the_golden_file()
    {
        string actual = MarkdownReportWriter.Render(SampleRun.Create());
        string golden = Path.Combine(SourceDirectory(), "Golden", "report.md");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
            File.WriteAllText(golden, actual);
        }
        Assert.True(File.Exists(golden), $"golden file missing: run the tests once with UPDATE_GOLDEN=1, review {golden}, then commit it");
        Assert.Equal(File.ReadAllText(golden).Replace("\r\n", "\n"), actual);
    }

    private static int CountCells(string line)
    {
        int cells = 0;
        for (int i = 1; i < line.Length; i++)
            if (line[i] == '|' && line[i - 1] != '\\') cells++;
        return cells;
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
