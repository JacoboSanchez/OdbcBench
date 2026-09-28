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
    public void Markdown_tables_are_well_formed() => AssertTablesWellFormed(MarkdownReportWriter.Render(SampleRun.Create()));

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
    public void Markdown_matches_the_golden_file() => AssertGolden("report.md", MarkdownReportWriter.Render(SampleRun.Create()));

    // ---- insert workload

    [Fact]
    public void Insert_consistency_catches_rows_missing_from_the_table()
    {
        var run = SampleInsertRun.Create();
        Assert.True(run.Consistency.RowCountsMatch);
        Assert.True(run.Consistency.RowsAccepted);
        Assert.False(run.Consistency.TableRowsMatch);
        Assert.Equal(4 * (1 + SampleInsertRun.Iterations), run.Consistency.TableChecks);
        Assert.Contains(run.Consistency.Notes, n => n.Contains("vendor @1") && n.Contains("9,999"));
        Assert.Equal(1, run.ExitCode);

        run.Series[3].Samples[2].VerifiedRows = SampleInsertRun.Rows;
        Analysis.Summarize(run, SampleInsertRun.Iterations);
        Assert.True(run.Consistency.TableRowsMatch);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public void Insert_consistency_catches_refused_rows()
    {
        var run = SampleInsertRun.Create();
        run.Series[0].Samples[3].Rows = SampleInsertRun.Rows - 10;
        run.Series[0].Samples[3].VerifiedRows = SampleInsertRun.Rows - 10;
        Analysis.Summarize(run, SampleInsertRun.Iterations);
        Assert.False(run.Consistency.RowsAccepted);
        Assert.Contains(run.Consistency.Notes, n => n.Contains("psql @1000") && n.Contains("9,990 of the 10,000"));
    }

    [Fact]
    public void Insert_statistics_include_commit_and_generate()
    {
        var run = SampleInsertRun.Create();
        var s = run.Series[0];
        Assert.NotNull(s.Commit);
        Assert.Equal(2.5, s.Generate!.P50);
        Assert.Equal(1000, s.EffectiveBlockSize);
        Assert.Equal(1, run.Series[1].EffectiveBlockSize);
    }

    [Fact]
    public void Insert_markdown_contains_every_section()
    {
        string md = MarkdownReportWriter.Render(SampleInsertRun.Create());
        foreach (var heading in new[]
                 {
                     "# ODBC driver batch insert comparison", "## Summary", "## Environment", "## Insert statement",
                     "## Validation (first 10 rows)", "## Parameter bindings", "## Results: batch size 1,000", "## Results: batch size 1",
                     "### Where the time goes (p50, ms)", "## Data consistency", "## Appendix: every iteration",
                 })
            Assert.Contains(heading + "\n", md);

        Assert.Contains("**TABLE ROW COUNT DIFFERS**", md);
        Assert.Contains("| Batch 1,000 |", md);
        Assert.Contains("SQL\\_ATTR\\_PARAMSET\\_SIZE", md);
        Assert.DoesNotContain("## Query\n", md);
        Assert.DoesNotContain("### Harness overhead", md);
        Assert.DoesNotContain("token=abc", md);
    }

    [Fact]
    public void Insert_markdown_tables_are_well_formed() => AssertTablesWellFormed(MarkdownReportWriter.Render(SampleInsertRun.Create()));

    [Fact]
    public void Insert_json_round_trip_renders_the_same_report()
    {
        var run = SampleInsertRun.Create();
        var back = System.Text.Json.JsonSerializer.Deserialize<RunResult>(JsonResultWriter.Serialize(run), JsonResultWriter.Options)!;
        Assert.Equal(OdbcBench.Config.Workload.Insert, back.Workload);
        Assert.Equal(MarkdownReportWriter.Render(run), MarkdownReportWriter.Render(back));
    }

    [Fact]
    public void Results_without_a_workload_are_read_as_select()
    {
        string json = JsonResultWriter.Serialize(SampleRun.Create()).Replace("\"workload\": \"select\",", "");
        Assert.DoesNotContain("\"workload\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<RunResult>(json, JsonResultWriter.Options)!;
        Assert.Equal(OdbcBench.Config.Workload.Select, back.Workload);
    }

    [Fact]
    public void Insert_markdown_matches_the_golden_file() => AssertGolden("insert-report.md", MarkdownReportWriter.Render(SampleInsertRun.Create()));

    private static void AssertGolden(string name, string actual)
    {
        string golden = Path.Combine(SourceDirectory(), "Golden", name);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
            File.WriteAllText(golden, actual);
        }
        Assert.True(File.Exists(golden), $"golden file missing: run the tests once with UPDATE_GOLDEN=1, review {golden}, then commit it");
        Assert.Equal(File.ReadAllText(golden).Replace("\r\n", "\n"), actual);
    }

    private static void AssertTablesWellFormed(string md)
    {
        var lines = md.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("| ") || i + 1 >= lines.Length || !lines[i + 1].StartsWith("| ---")) continue;
            int columns = CountCells(lines[i]);
            for (int j = i + 1; j < lines.Length && lines[j].StartsWith("|"); j++)
                Assert.True(columns == CountCells(lines[j]), $"line {j + 1} has {CountCells(lines[j])} cells, header has {columns}: {lines[j]}");
        }
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
