using OdbcBench.Bench;
using OdbcBench.Config;
using OdbcBench.Fetch;
using OdbcBench.Odbc;
using OdbcBench.Report;
using OdbcBench.Validation;

namespace OdbcBench.Tests;

/// <summary>
/// A deterministic insert run: two DSNs and two batch sizes. One driver caps the parameter array at 1, and in one
/// iteration the table gains fewer rows than that driver reported.
/// </summary>
internal static class SampleInsertRun
{
    public const int Iterations = 5;
    public const long Rows = 10_000;

    public static RunResult Create()
    {
        var config = BenchConfig.Parse("""
            {
              "baseline": "psql",
              "dsns": [
                { "name": "psql", "dsn": "PostgreSQL35W", "uid": "bench", "pwd": "***" },
                { "name": "vendor", "connectionString": "Driver={Vendor ODBC};Host=h;token=***" }
              ],
              "warmupIterations": 1,
              "iterations": 5,
              "blockSizes": [ 1000, 1 ],
              "insert": { "table": "bench.orders", "rows": 10000, "cleanup": "truncate" }
            }
            """);
        config.ResolveQuery(null);

        var driverA = new DriverInfo("PSQLODBC35W.DLL", "17.00.0007", "03.51", "PostgreSQL", "18.0.4", "localhost", "PostgreSQL35W", "03.81.26100.0000", 15, true);
        var driverB = new DriverInfo("vendorodbc.dll", "2.1.0", "03.80", "PostgreSQL", "18.0.4", "localhost", "", "03.81.26100.0000", 1, true);

        var run = new RunResult
        {
            ToolVersion = "0.1.0",
            StartedUtc = new DateTime(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc),
            FinishedUtc = new DateTime(2026, 9, 28, 18, 1, 5, DateTimeKind.Utc),
            Host = "BENCHBOX",
            ProcessBitness = "64-bit",
            ConfigPath = @"C:\bench\bench.json",
            ConfigSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            CommandLine = "OdbcBench insert --config bench.json",
            BaselineDsn = "psql",
            Workload = Workload.Insert,
            Config = config.Redacted(),
            Query = "INSERT INTO bench.orders (\"id\", \"customer\", \"amount\") VALUES (?, ?, ?)",
            Environment = new EnvironmentInfo
            {
                Os = "Microsoft Windows 10.0.26100 (X64)", Cpu = "Test CPU", LogicalProcessors = 16, MemoryGb = 64,
                PowerPlan = "High performance", Runtime = ".NET 8.0.31 X64", DriverManagerVersion = "03.81.26100.0000",
                OdbcVersion = "3.80", TieredCompilation = false, HighResolutionTimer = true,
            },
            Dsns =
            {
                new DsnResult { Name = "psql", Source = "DSN=PostgreSQL35W", Status = "connected", Driver = driverA, FirstConnectMs = 60.5, ConnectSamplesMs = { 38, 39 }, Connect = SeriesStats.From(new double[] { 38, 39 }) },
                new DsnResult { Name = "vendor", Source = "Driver={Vendor ODBC};Host=h;token=***", Status = "connected", Driver = driverB, FirstConnectMs = 80.25, ConnectSamplesMs = { 45, 44 }, Connect = SeriesStats.From(new double[] { 45, 44 }) },
            },
            Validation = new ValidationResult
            {
                RowsCompared = 10,
                DsnsCompared = 3,
                BaselineDsn = "(sent)",
                Metadata =
                {
                    new ColumnMetadataRow { Ordinal = 1, Name = "id", ComparedAs = "integer", TypeByDsn = { ["(sent)"] = "INTEGER(10) int4", ["psql"] = "INTEGER(10) int4", ["vendor"] = "INTEGER(10) int4" } },
                    new ColumnMetadataRow { Ordinal = 2, Name = "customer", ComparedAs = "text", TypeByDsn = { ["(sent)"] = "WVARCHAR(40) varchar", ["psql"] = "WVARCHAR(40) varchar", ["vendor"] = "VARCHAR(40) varchar" } },
                    new ColumnMetadataRow { Ordinal = 3, Name = "amount", ComparedAs = "decimal", TypeByDsn = { ["(sent)"] = "NUMERIC(12,2) numeric", ["psql"] = "NUMERIC(12,2) numeric", ["vendor"] = "NUMERIC(12,2) numeric" } },
                },
                Issues = { new ValidationIssue(Severity.Info, "column 'customer': WVARCHAR(40) varchar on (sent) vs VARCHAR(40) varchar on vendor", "customer", Dsn: "vendor") },
            },
        };

        run.Series.Add(Series("psql", 1000, baseMs: 280, effective: 1000, checksum: "00000000000000cc", Bindings("SQL_C_WCHAR x 66 B")));
        run.Series.Add(Series("vendor", 1000, baseMs: 3900, effective: 1, checksum: "00000000000000cc", Bindings("SQL_C_CHAR x 33 B"),
            warnings: new List<string> { "SQL_ATTR_PARAMSET_SIZE=1000 not accepted by the driver: [HYC00] Optional feature not implemented; every SQLExecute sends one row" }));
        run.Series.Add(Series("psql", 1, baseMs: 720, effective: 1, checksum: "00000000000000cc", Bindings("SQL_C_WCHAR x 66 B")));
        var short1 = Series("vendor", 1, baseMs: 3950, effective: 1, checksum: "00000000000000cc", Bindings("SQL_C_CHAR x 33 B"));
        short1.Samples[2].VerifiedRows = Rows - 1;
        short1.Samples[2].AddWarning("the driver accepted 10,000 rows but the table gained 9,999 (SELECT COUNT(*) FROM bench.orders)");
        run.Series.Add(short1);

        Analysis.Summarize(run, Iterations);
        return run;
    }

    private static List<ColumnBinding> Bindings(string text) => new()
    {
        new ColumnBinding { Ordinal = 1, Name = "id", SqlType = "INTEGER", Size = "10", Binding = "SQL_C_SLONG x 4 B" },
        new ColumnBinding { Ordinal = 2, Name = "customer", SqlType = "WVARCHAR", Size = "40", Binding = text },
        new ColumnBinding { Ordinal = 3, Name = "amount", SqlType = "NUMERIC", Size = "12,2", Binding = "SQL_C_WCHAR x 28 B" },
    };

    private static SeriesResult Series(string dsn, int batchSize, double baseMs, int effective, string checksum,
        List<ColumnBinding> columns, List<string>? warnings = null)
    {
        var s = new SeriesResult
        {
            DsnName = dsn,
            BlockSize = batchSize,
            EffectiveBlockSize = effective,
            Description = "test",
            Columns = columns,
            Warnings = warnings ?? new List<string>(),
        };
        s.Samples.Add(Sample(0, true, baseMs * 1.4, effective, checksum));
        double[] jitter = { 1.00, 1.02, 0.99, 1.01, 1.03 };
        for (int i = 0; i < Iterations; i++)
            s.Samples.Add(Sample(i + 1, false, baseMs * jitter[i], effective, checksum));
        return s;
    }

    private static IterationSample Sample(int index, bool warmup, double total, int effective, string checksum) => new()
    {
        Index = index,
        Warmup = warmup,
        StartedUtc = new DateTime(2026, 9, 28, 18, 0, index, DateTimeKind.Utc),
        ExecuteMs = total * 0.97,
        FirstBatchMs = total * 0.97 / Math.Ceiling((double)Rows / effective),
        CommitMs = total * 0.03,
        GenerateMs = 2.5,
        TotalMs = total,
        Rows = Rows,
        VerifiedRows = Rows,
        Batches = (long)Math.Ceiling((double)Rows / effective),
        Bytes = Rows * 90,
        Checksum = checksum,
        EffectiveBlockSize = effective,
        CpuMs = total * 0.4,
    };
}
