using System.Globalization;
using System.Text;
using OdbcBench.Bench;
using OdbcBench.Config;
using OdbcBench.Fetch;
using OdbcBench.Insert;
using OdbcBench.Validation;

namespace OdbcBench.Report;

/// <summary>Renders the comparison document from a <see cref="RunResult"/> (live or re-read from JSON).</summary>
public static class MarkdownReportWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const int MaxMessagesInReport = 200;

    public static void Write(RunResult run, string path) => File.WriteAllText(path, Render(run), new UTF8Encoding(false));

    public static string Render(RunResult run)
    {
        var md = new Md();
        Header(md, run);
        Summary(md, run);
        EnvironmentSection(md, run);
        QuerySection(md, run);
        ValidationSection(md, run);
        BindingsSection(md, run);
        foreach (int blockSize in BlockSizes(run)) ResultsSection(md, run, blockSize);
        ConsistencySection(md, run);
        MessagesSection(md, run);
        Appendix(md, run);
        return md.ToString();
    }

    // ---------------------------------------------------------------- sections

    private static bool IsInsert(RunResult run) => run.Workload == Workload.Insert;

    private static void Header(Md md, RunResult run)
    {
        var cfg = run.Config;
        if (IsInsert(run))
        {
            InsertHeader(md, run);
            return;
        }
        md.Heading(1, "ODBC driver performance comparison");

        string validation = run.Validation?.Status ?? (cfg.Validation.Enabled ? "not run" : "skipped");
        string consistency = run.Series.Any(s => s.OkCount > 0)
            ? run.Consistency.RowCountsMatch ? "row counts match" : "**ROW COUNTS DIFFER**"
            : "no data";
        md.Paragraph($"**Outcome:** {Esc(run.Outcome)} (exit code {run.ExitCode}) · **Validation:** {validation} · **Consistency:** {consistency}");

        string longMode = cfg.LongColumnMode.Equals("bindCapped", StringComparison.OrdinalIgnoreCase)
            ? string.Create(Inv, $"bound with a {cfg.LongColumnCapBytes:N0}-byte cap, truncations counted")
            : "read row by row with SQLGetData (block fetch off for that query)";
        var rows = new List<string[]>
        {
            new[] { "Run", $"{run.StartedUtc.ToString("yyyy-MM-dd HH:mm:ss", Inv)} UTC, {Duration(run.FinishedUtc - run.StartedUtc)}" },
            new[] { "Host", $"{Esc(run.Host)}, {run.ProcessBitness} process" },
            new[] { "Tool", $"OdbcBench {Esc(run.ToolVersion)}" },
            new[] { "Configuration", run.ConfigPath.Length == 0 ? "–" : $"{Code(run.ConfigPath)} (SHA-256 {Short(run.ConfigSha256)})" },
            new[] { "Command line", run.CommandLine.Length == 0 ? "–" : Code(run.CommandLine) },
            new[] { "Iterations", $"{cfg.WarmupIterations} warmup + {cfg.Iterations} measured per series; statistics use measured iterations only" },
            new[] { "Order", cfg.Interleave ? "interleaved: each round runs every series once, alternating direction between rounds" : "one series after another" },
            new[] { "Connections", cfg.ConnectionPerIteration ? "new connection every iteration (connect timed separately, not part of total)" : "one connection per DSN, reused by every iteration" },
            new[] { "Statements", cfg.ReuseStatement ? "one statement per series, reused (describe and bind once)" : "new statement every iteration (describe and bind are part of total)" },
            new[] { "Binding", $"{(cfg.BindMode.Equals("wchar", StringComparison.OrdinalIgnoreCase) ? "every column as SQL_C_WCHAR" : "native C types")}; long columns {longMode}" },
            new[] { "Total time", "SQLExecDirectW + describe/bind (only when the statement is built) + every SQLFetchScroll and value read + SQLFreeStmt(SQL_CLOSE)" },
            new[] { "Baseline", run.BaselineDsn.Length == 0 ? "–" : Esc(run.BaselineDsn) },
        };
        md.Table(new[] { "Item", "Value" }, rows, "ll");
    }

    private static void InsertHeader(Md md, RunResult run)
    {
        var cfg = run.Config;
        var insert = cfg.Insert ?? new InsertConfig();
        var c = run.Consistency;
        md.Heading(1, "ODBC driver batch insert comparison");

        string validation = run.Validation?.Status ?? (cfg.Validation.Enabled ? "not run" : "skipped");
        string consistency = !run.Series.Any(s => s.OkCount > 0) ? "no data"
            : !c.RowCountsMatch ? "**ROW COUNTS DIFFER**"
            : !c.RowsAccepted ? "**ROWS REFUSED**"
            : !c.TableRowsMatch ? "**TABLE ROW COUNT DIFFERS**"
            : c.TableChecks > 0 ? "every row sent is in the table"
            : "row counts match";
        md.Paragraph($"**Outcome:** {Esc(run.Outcome)} (exit code {run.ExitCode}) · **Validation:** {validation} · **Consistency:** {consistency}");

        string table = string.IsNullOrWhiteSpace(insert.Table) ? "set per DSN" : Code(insert.Table.Trim());
        string columns = insert.Columns is { Count: > 0 } ? string.Join(", ", insert.Columns.Select(x => Code(x.Trim()))) : "every column that accepts a value";
        string transactions = cfg.TransactionModeValue switch
        {
            TransactionMode.PerBatch => "autocommit off, SQLEndTran(SQL_COMMIT) after every SQLExecute",
            TransactionMode.Autocommit => "autocommit on: the driver commits every SQLExecute by itself",
            _ => "autocommit off, one SQLEndTran(SQL_COMMIT) at the end of the iteration",
        };
        string cleanup = cfg.CleanupModeValue switch
        {
            CleanupMode.Delete => "DELETE FROM the table before every iteration, not timed",
            CleanupMode.Truncate => "TRUNCATE TABLE before every iteration, not timed",
            _ => "none: every iteration adds its rows to the table",
        };
        var rows = new List<string[]>
        {
            new[] { "Run", $"{run.StartedUtc.ToString("yyyy-MM-dd HH:mm:ss", Inv)} UTC, {Duration(run.FinishedUtc - run.StartedUtc)}" },
            new[] { "Host", $"{Esc(run.Host)}, {run.ProcessBitness} process" },
            new[] { "Tool", $"OdbcBench {Esc(run.ToolVersion)}" },
            new[] { "Configuration", run.ConfigPath.Length == 0 ? "–" : $"{Code(run.ConfigPath)} (SHA-256 {Short(run.ConfigSha256)})" },
            new[] { "Command line", run.CommandLine.Length == 0 ? "–" : Code(run.CommandLine) },
            new[] { "Target", string.Create(Inv, $"{table}, {insert.Rows:N0} rows per iteration; columns: {columns}") },
            new[] { "Values", string.Create(Inv, $"generated from the row number, the same rows in every iteration; text and binary values of {insert.ValueLength:N0} characters or bytes, capped at the column size") },
            new[] { "Iterations", $"{cfg.WarmupIterations} warmup + {cfg.Iterations} measured per series; statistics use measured iterations only" },
            new[] { "Order", cfg.Interleave ? "interleaved: each round runs every series once, alternating direction between rounds" : "one series after another" },
            new[] { "Connections", cfg.ConnectionPerIteration ? "new connection every iteration (connect timed separately, not part of total)" : "one connection per DSN, reused by every iteration" },
            new[] { "Statements", cfg.ReuseStatement ? "one statement per series, reused (prepare and bind once)" : "new statement every iteration (prepare and bind are part of total)" },
            new[] { "Binding", $"column-wise parameter arrays, {(cfg.BindMode.Equals("wchar", StringComparison.OrdinalIgnoreCase) ? "every parameter as SQL_C_WCHAR (binary as SQL_C_BINARY)" : "native C types")}" },
            new[] { "Transactions", transactions },
            new[] { "Cleanup", cleanup },
            new[] { "Row count check", insert.Verify ? "SELECT COUNT(*) on the table before and after every iteration, not timed" : "off" },
            new[] { "Total time", "prepare/bind (only when the statement is built) + every SQLExecute + SQLEndTran(SQL_COMMIT); generating the values is not included" },
            new[] { "Baseline", run.BaselineDsn.Length == 0 ? "–" : Esc(run.BaselineDsn) },
        };
        md.Table(new[] { "Item", "Value" }, rows, "ll");
    }

    private static void Summary(Md md, RunResult run)
    {
        bool insert = IsInsert(run);
        md.Heading(2, "Summary");
        var bullets = new List<string>();

        foreach (int bs in BlockSizes(run))
        {
            var done = run.Series.Where(s => s.BlockSize == bs && s.Total != null).ToList();
            string label = string.Create(Inv, $"**{(insert ? "Batch" : "Block")} size {bs:N0}:**");
            if (done.Count == 0)
            {
                bullets.Add($"{label} no series completed.");
                continue;
            }
            var fastest = done.MinBy(s => s.Total!.P50)!;
            var baseline = done.FirstOrDefault(s => s.IsBaseline);
            if (done.Count == 1)
            {
                bullets.Add($"{label} only {Esc(fastest.DsnName)} completed, with a p50 total of {Ms(fastest.Total!.P50)} ms.");
            }
            else if (baseline == null)
            {
                bullets.Add($"{label} {Esc(fastest.DsnName)} is fastest with a p50 total of {Ms(fastest.Total!.P50)} ms. The baseline did not complete, so there are no ratios.");
            }
            else if (ReferenceEquals(fastest, baseline))
            {
                var next = done.Where(s => !ReferenceEquals(s, baseline)).MinBy(s => s.Total!.P50)!;
                bullets.Add($"{label} the baseline {Esc(baseline.DsnName)} is fastest with a p50 total of {Ms(baseline.Total!.P50)} ms. " +
                            $"Next is {Esc(next.DsnName)} at {Ms(next.Total!.P50)} ms, {Ratio(next.RatioToBaseline)} the baseline time.");
            }
            else
            {
                double ratio = fastest.RatioToBaseline ?? double.NaN;
                bullets.Add($"{label} {Esc(fastest.DsnName)} is fastest with a p50 total of {Ms(fastest.Total!.P50)} ms: " +
                            $"{Ratio(ratio)} the time of the baseline {Esc(baseline.DsnName)} ({Ms(baseline.Total!.P50)} ms), " +
                            $"or {(1 / ratio).ToString("0.00", Inv)}× its speed.");
            }
        }

        if (run.Series.Any(s => s.OkCount > 0))
        {
            if (run.Consistency.RowCountsMatch && run.Consistency.Rows is long n)
                bullets.Add(string.Create(Inv, $"Every successful iteration of every DSN {(insert ? "inserted" : "returned")} {n:N0} rows."));
            else if (!run.Consistency.RowCountsMatch)
                bullets.Add(insert
                    ? "**Row counts differ between DSNs or iterations: the DSNs did not insert the same number of rows.** See [Data consistency](#data-consistency)."
                    : "**Row counts differ between DSNs or iterations: the DSNs did not return the same result.** See [Data consistency](#data-consistency).");
        }
        if (insert && !run.Consistency.RowsAccepted)
            bullets.Add("**A driver did not accept every row it was sent.** See [Data consistency](#data-consistency).");
        if (insert && !run.Consistency.TableRowsMatch)
            bullets.Add("**The table did not gain the rows a driver reported as inserted.** See [Data consistency](#data-consistency).");

        if (run.Validation is { } v)
            bullets.Add($"Validation of the first {run.Config.Validation.Rows} rows: **{v.Status}** ({Count(v.FailCount, "failure")}, {Count(v.WarnCount, "warning")}).");
        else
            bullets.Add(run.Config.Validation.Enabled ? "Validation did not run." : "Validation was skipped.");

        foreach (var d in run.Dsns.Where(d => d.Status != "connected"))
            bullets.Add($"{Esc(d.Name)} could not be benchmarked: {Esc(d.Status)}{(d.ErrorSqlState != null ? $" [{d.ErrorSqlState}]" : "")}.");
        foreach (var s in run.Series.Where(s => s.Status is "failed" or "abandoned" or "partial"))
            bullets.Add($"{Esc(s.Key)}: {s.Status}, {Count(s.FailedCount, "failed measured iteration")}. See [Warnings and errors](#warnings-and-errors).");
        foreach (var s in run.Series.Where(s => s.Noisy))
            bullets.Add($"{Esc(s.Key)} is noisy (CV {Pct(s.Total!.Cv)}): more iterations or a quieter machine would tighten the comparison.");

        var heavy = run.Series.Where(s => s.Harness is { PercentOfTotal: > 5 }).ToList();
        if (heavy.Count > 0)
            bullets.Add($"The harness's own value-reading loop is estimated at over 5% of the total time for {string.Join(", ", heavy.Select(s => Esc(s.Key)))}; see [Harness overhead](#harness-overhead).");
        if (run.Interrupted)
            bullets.Add("**The run was interrupted**: statistics cover only the iterations that finished.");

        foreach (var b in bullets) md.Bullet(b);
        md.Line();

        var blockSizes = BlockSizes(run).ToList();
        if (blockSizes.Count == 0) return;
        md.Paragraph(insert
            ? "p50 of the total time per iteration (prepare when the statement is built, every SQLExecute, commit). Ratios are relative to the baseline; below 1.00× is faster."
            : "p50 of the total time per iteration (execute, read every row and value, close). Ratios are relative to the baseline; below 1.00× is faster.");
        var headers = new List<string> { "DSN", "Driver" };
        headers.AddRange(blockSizes.Select(b => string.Create(Inv, $"{(insert ? "Batch" : "Block")} {b:N0}")));
        var rows = new List<string[]>();
        foreach (var d in run.Dsns)
        {
            var row = new List<string>
            {
                Esc(d.Name) + (string.Equals(d.Name, run.BaselineDsn, StringComparison.OrdinalIgnoreCase) ? " (baseline)" : ""),
                d.Driver == null ? "–" : Esc($"{d.Driver.DriverName} {d.Driver.DriverVersion}".Trim()),
            };
            foreach (int bs in blockSizes)
            {
                var s = run.Series.FirstOrDefault(x => x.BlockSize == bs && x.DsnName == d.Name);
                row.Add(s == null ? Esc(d.Status)
                    : s.Total == null ? Esc(s.Status)
                    : s.IsBaseline || s.RatioToBaseline == null ? $"{Ms(s.Total.P50)} ms"
                    : $"{Ms(s.Total.P50)} ms ({Ratio(s.RatioToBaseline)})");
            }
            rows.Add(row.ToArray());
        }
        md.Table(headers, rows, "ll" + new string('r', blockSizes.Count));
    }

    private static void EnvironmentSection(Md md, RunResult run)
    {
        var e = run.Environment;
        md.Heading(2, "Environment");
        md.Table(new[] { "Item", "Value" }, new[]
        {
            new[] { "OS", Esc(e.Os) },
            new[] { "CPU", $"{Esc(e.Cpu.Length > 0 ? e.Cpu : "unknown")} ({e.LogicalProcessors} logical processors)" },
            new[] { "Memory", $"{e.MemoryGb.ToString("0.0", Inv)} GB" },
            new[] { "Power plan", Esc(e.PowerPlan) },
            new[] { "Runtime", $"{Esc(e.Runtime)}; tiered compilation {(e.TieredCompilation ? "on" : "off")}; high-resolution timer {(e.HighResolutionTimer ? "yes" : "no")}" },
            new[] { "ODBC", $"Driver Manager {Esc(e.DriverManagerVersion)}; ODBC {Esc(e.OdbcVersion)} behaviour; connection pooling {Esc(e.ConnectionPooling)}" },
        }, "ll");

        md.Heading(3, "DSNs and drivers");
        md.Table(new[] { "DSN", "Source", "Driver", "Version", "ODBC", "Unicode", "SQLGetData extensions" },
            run.Dsns.Select(d => new[]
            {
                Esc(d.Name), Esc(d.Source),
                Esc(d.Driver?.DriverName ?? "–"), Esc(d.Driver?.DriverVersion ?? "–"), Esc(d.Driver?.DriverOdbcVersion ?? "–"),
                d.Driver?.UnicodeNative switch { true => "yes", false => "**no**", _ => "–" },
                Esc(d.Driver?.GetDataExtensionsText ?? "–"),
            }), "lllllll");

        md.Table(new[] { "DSN", "DBMS", "DBMS version", "Server", "First connect ms", "Connect p50 ms (n)", "Status" },
            run.Dsns.Select(d => new[]
            {
                Esc(d.Name), Esc(d.Driver?.DbmsName ?? "–"), Esc(d.Driver?.DbmsVersion ?? "–"), Esc(d.Driver?.ServerName ?? "–"),
                d.FirstConnectMs > 0 ? Ms(d.FirstConnectMs) : "–",
                d.Connect == null ? "–" : $"{Ms(d.Connect.P50)} ({d.Connect.Count})",
                Esc(d.Status) + (d.ErrorSqlState != null ? $" [{d.ErrorSqlState}]" : ""),
            }), "llllrrl");

        md.Paragraph("First connect includes loading the driver DLL. Connect p50 is the median of extra connections opened and closed afterwards (DNS, TCP, TLS and authentication).");

        foreach (var d in run.Dsns.Where(d => d.Error != null))
        {
            md.Line($"- **{Esc(d.Name)}**: {Esc(d.Error!)}");
            if (d.Hint != null) md.Line($"  - Hint: {Esc(d.Hint)}");
        }
        if (run.Dsns.Any(d => d.Error != null)) md.Line();
    }

    private static void QuerySection(Md md, RunResult run)
    {
        if (IsInsert(run))
        {
            md.Heading(2, "Insert statement");
            md.Paragraph("Built from the columns the driver describes for the table. One SQLExecute sends one array of rows.");
            md.Fenced("sql", run.Query);
            foreach (var d in run.Dsns.Where(d => d.QueryOverridden && d.Query != null))
            {
                md.Paragraph($"{Esc(d.Name)} runs its own statement:");
                md.Fenced("sql", d.Query!);
            }
            return;
        }

        md.Heading(2, "Query");
        md.Fenced("sql", run.Query);
        foreach (var d in run.Dsns.Where(d => d.QueryOverridden && d.Query != null))
        {
            md.Paragraph($"{Esc(d.Name)} runs its own query:");
            md.Fenced("sql", d.Query!);
        }
    }

    private static void ValidationSection(Md md, RunResult run)
    {
        var cfg = run.Config.Validation;
        md.Heading(2, $"Validation (first {cfg.Rows} rows)");
        var v = run.Validation;
        if (v == null)
        {
            md.Paragraph(cfg.Enabled ? "Validation did not run." : "Validation was skipped (disabled in the configuration or with --no-validate).");
            return;
        }

        var n = cfg.Normalization;
        string normalization =
            $"Before comparing: trailing spaces {(n.TrimTrailingSpaces ? "trimmed" : "kept")}; NULL {(n.NullEqualsEmpty ? "equals" : "differs from")} the empty string; " +
            $"decimals compared by value; floats with a relative tolerance of {n.FloatTolerance.ToString("G3", Inv)}; " +
            "dates and times parsed, with fractional seconds compared at the coarser precision of the two.";
        if (!IsInsert(run))
            md.Paragraph($"**{v.Status}**: {v.RowsCompared} rows compared across {v.DsnsCompared} DSNs against {Esc(v.BaselineDsn)}. " +
                         "Each DSN's rows were read with SQLFetch and SQLGetData as text, independently of the block-fetch path. " +
                         normalization);
        else if (v.DsnsCompared == 0)
            md.Paragraph($"**{v.Status}**: every series wrote its first rows in a dry run through the batch insert path. The rows were not read back.");
        else
            md.Paragraph($"**{v.Status}**: every series wrote its first rows in a dry run through the batch insert path. " +
                         $"The {v.RowsCompared} rows each DSN wrote last were then read back through that DSN with SQLFetch and SQLGetData as text, " +
                         $"and compared with the values that were sent, shown as {Esc(v.BaselineDsn)}. {Count(v.DsnsCompared - 1, "DSN")} compared. " +
                         normalization);

        if (v.Metadata.Count > 0)
        {
            var dsnNames = v.Metadata.SelectMany(m => m.TypeByDsn.Keys).Distinct().ToList();
            var headers = new List<string> { "#", "Column", "Compared as" };
            headers.AddRange(dsnNames.Select(Esc));
            md.Table(headers, v.Metadata.Select(m =>
            {
                var row = new List<string> { m.Ordinal.ToString(Inv), Esc(m.Name), m.ComparedAs };
                row.AddRange(dsnNames.Select(d => m.TypeByDsn.TryGetValue(d, out var t) ? Esc(t) : "–"));
                return row.ToArray();
            }), "rll" + new string('l', dsnNames.Count));
        }

        var cellDiffs = v.Issues.Where(i => i.CellDiff).ToList();
        if (cellDiffs.Count > 0)
        {
            md.Heading(3, "Differing values");
            md.Paragraph($"One line per differing row: column, then the value on {Esc(v.BaselineDsn)} → the value on the other DSN.");
            md.Table(new[] { "Row", "DSN", "Differences" }, cellDiffs
                .GroupBy(i => (i.Dsn, i.Row))
                .Select(g => new[]
                {
                    g.Key.Row?.ToString(Inv) ?? "",
                    Esc(g.Key.Dsn ?? ""),
                    string.Join("; ", g.Select(i => $"{Esc(i.Column ?? "")}: {Value(i.BaselineValue)} → {Value(i.Value)}")),
                }), "rll");
        }

        var others = v.Issues.Where(i => !i.CellDiff).OrderByDescending(i => i.Severity).ToList();
        if (others.Count > 0)
        {
            foreach (var i in others)
                md.Line($"- {SeverityLabel(i.Severity)} {Esc(i.Message)}");
            md.Line();
        }
    }

    private static void BindingsSection(Md md, RunResult run)
    {
        var perDsn = run.Series
            .Where(s => s.Columns.Count > 0)
            .GroupBy(s => s.DsnName)
            .Select(g => g.First())
            .ToList();
        if (perDsn.Count == 0) return;

        if (IsInsert(run))
        {
            md.Heading(2, "Parameter bindings");
            md.Paragraph("How each target column was described by the driver (SQLDescribeColW on an empty SELECT of the table) and bound as a parameter array (SQLBindParameter C type × element size).");
        }
        else
        {
            md.Heading(2, "Column bindings");
            md.Paragraph("How each column was described by the driver (SQLDescribeColW) and bound for block fetch (SQLBindCol target type × element size). Columns marked SQLGetData are read in chunks after each fetch.");
        }
        int columns = perDsn.Max(s => s.Columns.Count);
        var headers = new List<string> { "#", "Column" };
        headers.AddRange(perDsn.Select(s => Esc(s.DsnName)));
        var rows = new List<string[]>();
        for (int i = 0; i < columns; i++)
        {
            var name = perDsn.Select(s => i < s.Columns.Count ? s.Columns[i].Name : null).FirstOrDefault(x => x != null) ?? "";
            var row = new List<string> { (i + 1).ToString(Inv), Esc(name) };
            foreach (var s in perDsn)
            {
                if (i >= s.Columns.Count)
                {
                    row.Add("–");
                    continue;
                }
                var c = s.Columns[i];
                string size = c.Size is "" or "0" ? "" : $"({c.Size})";
                row.Add(Esc($"{c.SqlType}{size} → {c.Binding}"));
            }
            rows.Add(row.ToArray());
        }
        md.Table(headers, rows, "rl" + new string('l', perDsn.Count));
    }

    private static void ResultsSection(Md md, RunResult run, int blockSize)
    {
        bool insert = IsInsert(run);
        var group = run.Series.Where(s => s.BlockSize == blockSize).ToList();
        md.Heading(2, string.Create(Inv, $"Results: {(insert ? "batch" : "block")} size {blockSize:N0}"));

        var effective = group.Where(s => s.EffectiveBlockSize > 0).Select(s => string.Create(Inv, $"{Esc(s.DsnName)} {s.EffectiveBlockSize:N0}")).ToList();
        string effectiveText = effective.Count == 0 ? "" : $" Effective {(insert ? "parameter" : "row")} array size: {string.Join(", ", effective)}.";
        md.Paragraph(insert
            ? string.Create(Inv, $"Requested parameter array size {blockSize:N0} (SQL_ATTR_PARAMSET_SIZE).{effectiveText}")
            : string.Create(Inv, $"Requested row array size {blockSize:N0} (SQL_ATTR_ROW_ARRAY_SIZE).{effectiveText}"));

        md.Heading(3, "Total time per iteration (ms)");
        md.Table(new[] { "DSN", "Status", "OK", "Min", "p50", "Mean", "p95", "Max", "Std dev", "CV", "Outliers", "vs baseline" },
            group.Select(s => new[]
            {
                Esc(s.DsnName) + (s.IsBaseline ? " (baseline)" : ""),
                s.Status,
                $"{s.OkCount}/{s.OkCount + s.FailedCount}",
                Ms(s.Total?.Min), Ms(s.Total?.P50), Ms(s.Total?.Mean), Ms(s.Total?.P95), Ms(s.Total?.Max), Ms(s.Total?.StdDev),
                s.Total == null ? "–" : Pct(s.Total.Cv) + (s.Noisy ? " (noisy)" : ""),
                s.Total == null ? "–" : s.Total.Outliers == 0 ? "0" : $"{s.Total.Outliers} (worst #{s.Total.WorstIterationIndex})",
                s.IsBaseline ? "baseline" : s.RatioToBaseline == null ? "–" : $"{Ratio(s.RatioToBaseline)} ({Signed(s.PercentVsBaseline!.Value)})",
            }), "lllrrrrrrrrr");

        if (insert) InsertBreakdown(md, group);
        else FetchBreakdown(md, group);

        var notes = group.SelectMany(s => s.Warnings.Select(w => (s.DsnName, w))).ToList();
        if (notes.Count > 0)
        {
            md.Line("Notes:");
            md.Line();
            foreach (var (dsn, w) in notes) md.Line($"- {Esc(dsn)}: {Esc(w)}");
            md.Line();
        }
    }

    private static void InsertBreakdown(Md md, List<SeriesResult> group)
    {
        bool prepare = group.Any(s => s.Describe != null);
        bool connect = group.Any(s => s.Connect != null);
        md.Heading(3, "Where the time goes (p50, ms)");
        var headers = new List<string> { "DSN", "Execute", "First batch", "Commit" };
        if (prepare) headers.Add("Prepare + bind");
        headers.Add("Generate (not in total)");
        if (connect) headers.Add("Connect (not in total)");
        md.Table(headers, group.Select(s =>
        {
            var row = new List<string> { Esc(s.DsnName), Ms(s.Execute?.P50), Ms(s.FirstBatch?.P50), Ms(s.Commit?.P50) };
            if (prepare) row.Add(Ms(s.Describe?.P50));
            row.Add(Ms(s.Generate?.P50));
            if (connect) row.Add(Ms(s.Connect?.P50));
            return row.ToArray();
        }), "l" + new string('r', headers.Count - 1));
        md.Paragraph("Execute covers every SQLExecute call, one per parameter array, first batch included. First batch is the first SQLExecute. " +
                     "Commit is every SQLEndTran(SQL_COMMIT); a dash means the driver commits by itself. " +
                     "Generate is the time the tool took to fill the parameter arrays, which happens between the timed calls.");

        md.Heading(3, "Throughput and resources (medians per iteration)");
        md.Table(new[] { "DSN", "Rows", "Data MB", "Rows/s", "MB/s", "CPU", "Rejected rows", "GCs", "Insert-loop alloc B", "Effective batch" },
            group.Select(s => s.Total == null
                ? new[] { Esc(s.DsnName), "–", "–", "–", "–", "–", "–", "–", "–", s.EffectiveBlockSize > 0 ? s.EffectiveBlockSize.ToString("N0", Inv) : "–" }
                : new[]
                {
                    Esc(s.DsnName),
                    s.Rows.ToString("N0", Inv) + (s.RowsVary ? " (varies)" : ""),
                    (s.MedianBytes / 1e6).ToString("N2", Inv),
                    s.RowsPerSecond.ToString("N0", Inv),
                    s.MegabytesPerSecond.ToString("N2", Inv),
                    Pct(s.CpuPercent),
                    s.RowErrors.ToString("N0", Inv),
                    s.GcCollections.ToString("N0", Inv),
                    s.AllocatedBytes.ToString("N0", Inv),
                    s.EffectiveBlockSize.ToString("N0", Inv),
                }), "lrrrrrrrrr");
        md.Paragraph("Data MB counts the bytes of every value as sent in its C type (10^6 bytes), not network bytes. " +
                     "Rows/s and MB/s divide by the total time. CPU is process user + kernel time, generating the values included, over the total time; above 100% means the driver used several threads. " +
                     "Windows accounts CPU time in ticks of about 15.6 ms, so CPU figures are coarse for short iterations.");
    }

    private static void FetchBreakdown(Md md, List<SeriesResult> group)
    {
        bool describe = group.Any(s => s.Describe != null);
        bool connect = group.Any(s => s.Connect != null);
        md.Heading(3, "Where the time goes (p50, ms)");
        var headers = new List<string> { "DSN", "Execute", "First batch", "Fetch", "Close" };
        if (describe) headers.Add("Describe + bind");
        if (connect) headers.Add("Connect (not in total)");
        md.Table(headers, group.Select(s =>
        {
            var row = new List<string> { Esc(s.DsnName), Ms(s.Execute?.P50), Ms(s.FirstBatch?.P50), Ms(s.Fetch?.P50), Ms(s.Close?.P50) };
            if (describe) row.Add(Ms(s.Describe?.P50));
            if (connect) row.Add(Ms(s.Connect?.P50));
            return row.ToArray();
        }), "l" + new string('r', headers.Count - 1));
        md.Paragraph("Execute is SQLExecDirectW. First batch is the first SQLFetchScroll call, which returns the first row array. " +
                     "Fetch covers every SQLFetchScroll call plus reading every value, first batch included. Close is SQLFreeStmt(SQL_CLOSE).");

        md.Heading(3, "Throughput and resources (medians per iteration)");
        md.Table(new[] { "DSN", "Rows", "Data MB", "Rows/s", "MB/s", "CPU", "Truncated", "GCs", "Fetch-loop alloc B", "Effective block" },
            group.Select(s => s.Total == null
                ? new[] { Esc(s.DsnName), "–", "–", "–", "–", "–", "–", "–", "–", s.EffectiveBlockSize > 0 ? s.EffectiveBlockSize.ToString("N0", Inv) : "–" }
                : new[]
                {
                    Esc(s.DsnName),
                    s.Rows.ToString("N0", Inv) + (s.RowsVary ? " (varies)" : ""),
                    (s.MedianBytes / 1e6).ToString("N2", Inv),
                    s.RowsPerSecond.ToString("N0", Inv),
                    s.MegabytesPerSecond.ToString("N2", Inv),
                    Pct(s.CpuPercent),
                    s.Truncations.ToString("N0", Inv),
                    s.GcCollections.ToString("N0", Inv),
                    s.AllocatedBytes.ToString("N0", Inv),
                    s.EffectiveBlockSize.ToString("N0", Inv),
                }), "lrrrrrrrrr");
        md.Paragraph("Data MB counts the bytes of every non-NULL value as delivered in its C type (10^6 bytes), not network bytes. " +
                     "Rows/s and MB/s divide by the total time. CPU is process user + kernel time over wall time; above 100% means the driver used several threads. Windows accounts CPU time in ticks of about 15.6 ms, so CPU figures are coarse for short iterations.");
    }

    private static void InsertConsistency(Md md, RunResult run, List<SeriesResult> withData)
    {
        var c = run.Consistency;
        md.Bullet(c.RowCountsMatch
            ? $"**Row counts:** every successful iteration inserted {c.Rows?.ToString("N0", Inv) ?? "the same number of"} rows."
            : "**Row counts: FAIL.** Different DSNs or iterations inserted different numbers of rows.");
        md.Bullet(c.RowsAccepted
            ? "**Accepted:** every driver accepted every row it was sent."
            : "**Accepted: FAIL.** A driver rejected rows (SQL_PARAM_ERROR) or left part of a parameter array unprocessed.");
        md.Bullet(c.TableChecks == 0
            ? "**Table:** the rows of the table were not counted."
            : c.TableRowsMatch
                ? $"**Table:** in each of the {c.TableChecks.ToString("N0", Inv)} iterations counted, the table gained exactly the rows the driver accepted."
                : "**Table: FAIL.** The table did not gain the rows a driver reported as inserted: that driver does not write the whole parameter array, or something else writes to the table.");
        md.Bullet(c.ChecksumsStable
            ? "**Values:** every series sent the same values in every iteration."
            : "**Values: WARN.** A series sent different values in different iterations.");
        if (withData.Select(s => s.BlockSize).Distinct().Count() > 1)
            md.Bullet(c.BlockSizeChecksumsEqual
                ? "**Batch sizes:** each DSN was sent the same values at every batch size."
                : "**Batch sizes: WARN.** A DSN was sent different values depending on the batch size.");
        if (withData.Select(s => s.DsnName).Distinct().Count() > 1)
            md.Bullet(c.CrossDsnChecksumsEqual
                ? "**Across DSNs:** all DSNs were sent byte-identical values."
                : "**Across DSNs (informational):** checksums differ. The drivers describe the columns of the table differently, " +
                  "so a value was generated or bound differently, for example an integer of another width. The validation section compares what was stored.");
        foreach (var note in c.Notes) md.Bullet(Esc(note));
        md.Line();

        md.Table(new[] { "Series", "Rows accepted", "Rows the table gained", "Checksum of the values sent", "Stable" },
            withData.Select(s =>
            {
                var counted = s.Samples.Where(x => x.Ok && x.VerifiedRows != null).Select(x => x.VerifiedRows!.Value).Distinct().ToList();
                return new[]
                {
                    Esc(s.Key), s.Rows.ToString("N0", Inv) + (s.RowsVary ? " (varies)" : ""),
                    counted.Count == 0 ? "not counted" : counted[0].ToString("N0", Inv) + (counted.Count > 1 ? " (varies)" : ""),
                    Code(s.Checksum ?? "–"), s.ChecksumStable ? "yes" : "**no**",
                };
            }), "lrrll");
    }

    private static void ConsistencySection(Md md, RunResult run)
    {
        var c = run.Consistency;
        var withData = run.Series.Where(s => s.Samples.Any(x => x.Ok)).ToList();
        md.Heading(2, "Data consistency");
        if (withData.Count == 0)
        {
            md.Paragraph("No iteration completed, so there is nothing to check.");
            return;
        }
        if (IsInsert(run))
        {
            InsertConsistency(md, run, withData);
            return;
        }

        md.Bullet(c.RowCountsMatch
            ? $"**Row counts:** every successful iteration returned {c.Rows?.ToString("N0", Inv) ?? "the same number of"} rows."
            : "**Row counts: FAIL.** Different DSNs or iterations returned different numbers of rows, so they did not return the same result.");
        md.Bullet(c.ChecksumsStable
            ? "**Stability:** every series produced the same checksum in every iteration."
            : "**Stability: WARN.** Some series produced different checksums between iterations: the data or its order changed between executions (add an ORDER BY, or the data is changing).");
        if (withData.Select(s => s.BlockSize).Distinct().Count() > 1)
            md.Bullet(c.BlockSizeChecksumsEqual
                ? "**Block sizes:** each DSN produced the same checksum at every block size."
                : "**Block sizes: WARN.** A DSN returned different data depending on the row array size, which points at a block-cursor problem in the driver.");
        if (withData.Select(s => s.DsnName).Distinct().Count() > 1)
            md.Bullet(c.CrossDsnChecksumsEqual
                ? "**Across DSNs:** all DSNs produced identical checksums, so they returned byte-identical values."
                : "**Across DSNs (informational):** checksums differ. They only match when drivers return byte-identical values in the same C types; " +
                  "a different binding (for example a timestamp delivered as text) or text formatting changes the checksum even for equal data. The validation section compares values properly.");
        foreach (var note in c.Notes) md.Bullet(Esc(note));
        md.Line();

        md.Table(new[] { "Series", "Rows", "Checksum", "Stable" },
            withData.Select(s => new[]
            {
                Esc(s.Key), s.Rows.ToString("N0", Inv) + (s.RowsVary ? " (varies)" : ""), Code(s.Checksum ?? "–"), s.ChecksumStable ? "yes" : "**no**",
            }), "lrll");

        var harness = run.Series.Where(s => s.Harness != null).ToList();
        if (harness.Count > 0)
        {
            md.Heading(3, "Harness overhead");
            md.Paragraph("After the benchmark, the value-reading loop was replayed over the bound buffers without calling ODBC. " +
                         "The estimate is rows × bound columns × time per value; it is included in every DSN's fetch time in the same way.");
            md.Table(new[] { "Series", "Bound columns", "ns per value", "Estimated ms per iteration", "Share of p50 total" },
                harness.Select(s => new[]
                {
                    Esc(s.Key), s.Harness!.BoundColumns.ToString(Inv), s.Harness.NsPerValue.ToString("0.00", Inv),
                    Ms(s.Harness.EstimatedMsPerIteration), Pct(s.Harness.PercentOfTotal),
                }), "lrrrr");
        }
    }

    private static void MessagesSection(Md md, RunResult run)
    {
        md.Heading(2, "Warnings and errors");
        var messages = run.Messages.Where(m => m.Severity != "info").ToList();
        var infos = run.Messages.Where(m => m.Severity == "info").ToList();
        if (messages.Count == 0 && infos.Count == 0)
        {
            md.Paragraph("None.");
            return;
        }
        var shown = messages.Concat(infos).Take(MaxMessagesInReport).ToList();
        md.Table(new[] { "Phase", "Severity", "DSN", "Series", "Iteration", "SQLSTATE", "Message" },
            shown.Select(m => new[]
            {
                m.Phase, m.Severity == "error" ? "**error**" : m.Severity, Esc(m.Dsn ?? ""), Esc(m.Series ?? ""),
                m.Iteration?.ToString(Inv) ?? "", m.SqlState ?? "", Esc(m.Message),
            }), "lllllll");
        if (run.Messages.Count > shown.Count)
            md.Paragraph($"{run.Messages.Count - shown.Count} more messages are in the JSON result.");
    }

    private static void Appendix(Md md, RunResult run)
    {
        var series = run.Series.Where(s => s.Samples.Count > 0).ToList();
        if (series.Count == 0) return;
        bool insert = IsInsert(run);
        md.Heading(2, "Appendix: every iteration");
        md.Paragraph("Times in ms. Warmup iterations are listed but never enter the statistics.");
        foreach (var s in series)
        {
            md.Line("<details>");
            md.Line($"<summary>{Esc(s.Key)}: {s.Samples.Count} iterations</summary>");
            md.Line();
            if (insert)
                md.Table(new[] { "Phase", "#", "Total", "Execute", "First batch", "Commit", "Generate", "Rows", "Table gained", "CPU", "Result" },
                    s.Samples.Select(x => new[]
                    {
                        x.Warmup ? "warmup" : "measured", x.Index.ToString(Inv),
                        Ms(x.TotalMs), Ms(x.ExecuteMs), Ms(x.FirstBatchMs), Ms(x.CommitMs), Ms(x.GenerateMs),
                        x.Rows.ToString("N0", Inv), x.VerifiedRows?.ToString("N0", Inv) ?? "", Ms(x.CpuMs),
                        Result(x),
                    }), "lrrrrrrrrrl");
            else
                md.Table(new[] { "Phase", "#", "Total", "Execute", "First batch", "Fetch", "Close", "Rows", "CPU", "Checksum", "Result" },
                    s.Samples.Select(x => new[]
                    {
                        x.Warmup ? "warmup" : "measured", x.Index.ToString(Inv),
                        Ms(x.TotalMs), Ms(x.ExecuteMs), Ms(x.FirstBatchMs), Ms(x.FetchMs), Ms(x.CloseMs),
                        x.Rows.ToString("N0", Inv), Ms(x.CpuMs), x.Ok ? Code(x.Checksum) : "",
                        Result(x),
                    }), "lrrrrrrrrll");
            md.Line("</details>");
            md.Line();
        }
    }

    private static string Result(IterationSample x) =>
        x.Ok ? "ok" : $"**failed** {(x.ErrorSqlState != null ? $"[{x.ErrorSqlState}] " : "")}{Esc(Truncate(x.ErrorMessage ?? "", 120))}";

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<int> BlockSizes(RunResult run)
    {
        var present = run.Series.Select(s => s.BlockSize).ToHashSet();
        var ordered = run.Config.BlockSizes.Where(present.Contains).ToList();
        ordered.AddRange(present.Where(b => !ordered.Contains(b)).OrderBy(b => b));
        return ordered;
    }

    private static string SeverityLabel(Severity s) => s switch
    {
        Severity.Fail => "**FAIL**",
        Severity.Warn => "**WARN**",
        _ => "INFO",
    };

    public static string Ms(double? value)
    {
        if (value is not double v || double.IsNaN(v) || double.IsInfinity(v)) return "–";
        double a = Math.Abs(v);
        return a < 10 ? v.ToString("0.000", Inv) : a < 1000 ? v.ToString("0.00", Inv) : v.ToString("N1", Inv);
    }

    private static string Count(int n, string noun) => $"{n.ToString(Inv)} {noun}{(n == 1 ? "" : "s")}";

    private static string Pct(double v) => v.ToString("0.0", Inv) + "%";

    private static string Ratio(double? r) => r is double v && !double.IsNaN(v) ? v.ToString("0.00", Inv) + "×" : "–";

    private static string Signed(double pct) => (pct >= 0 ? "+" : "") + pct.ToString("0.0", Inv) + "%";

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";

    private static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalSeconds < 60) return $"{t.TotalSeconds.ToString("0.0", Inv)} s";
        if (t.TotalMinutes < 60) return string.Create(Inv, $"{(int)t.TotalMinutes} min {t.Seconds} s");
        return string.Create(Inv, $"{(int)t.TotalHours} h {t.Minutes:00} min");
    }

    /// <summary>Escapes text for Markdown prose and table cells: HTML-significant characters and table pipes.</summary>
    public static string Esc(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("|", "\\|").Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`")
        .Replace("\r", "").Replace("\n", " ");

    /// <summary>An inline code span (pipes still escaped for tables).</summary>
    public static string Code(string text)
    {
        text = text.Replace("\r", "").Replace("\n", " ").Replace("|", "\\|");
        string fence = text.Contains('`') ? "``" : "`";
        string pad = text.StartsWith('`') || text.EndsWith('`') ? " " : "";
        return $"{fence}{pad}{text}{pad}{fence}";
    }

    /// <summary>A data value from the validation sample: NULL marker or a code span that keeps spaces visible.</summary>
    private static string Value(string? value) => value == null ? "NULL" : value.Length == 0 ? "(empty)" : Code(Truncate(value, 80));

    private sealed class Md
    {
        private readonly StringBuilder _sb = new();

        public void Line(string text = "") => _sb.Append(text).Append('\n');

        public void Heading(int level, string text)
        {
            Line($"{new string('#', level)} {text}");
            Line();
        }

        public void Paragraph(string text)
        {
            Line(text);
            Line();
        }

        public void Bullet(string text) => Line($"- {text}");

        public void Fenced(string language, string text)
        {
            string fence = text.Contains("```") ? "~~~~" : "```";
            Line(fence + language);
            Line(text.Replace("\r\n", "\n").TrimEnd());
            Line(fence);
            Line();
        }

        /// <summary>Cells must already be escaped. <paramref name="align"/> has one 'l' or 'r' per column.</summary>
        public void Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, string align)
        {
            Line("| " + string.Join(" | ", headers) + " |");
            var sb = new StringBuilder("|");
            for (int i = 0; i < headers.Count; i++) sb.Append(i < align.Length && align[i] == 'r' ? " ---: |" : " --- |");
            Line(sb.ToString());
            foreach (var row in rows) Line("| " + string.Join(" | ", row.Select(c => c.Length == 0 ? " " : c)) + " |");
            Line();
        }

        public override string ToString() => _sb.ToString();
    }
}
