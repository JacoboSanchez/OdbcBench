using OdbcBench.Bench;
using OdbcBench.Config;
using OdbcBench.Fetch;
using OdbcBench.Odbc;
using OdbcBench.Report;
using OdbcBench.Validation;

namespace OdbcBench.Tests;

/// <summary>A deterministic run with two connected DSNs, one failed DSN and two block sizes.</summary>
internal static class SampleRun
{
    public const int Iterations = 5;

    public static RunResult Create()
    {
        var config = BenchConfig.Parse("""
            {
              "query": "SELECT id, name, amount FROM sales ORDER BY id",
              "baseline": "legacy",
              "dsns": [
                { "name": "legacy", "dsn": "SalesLegacy", "uid": "bench", "pwd": "***" },
                { "name": "flight", "connectionString": "Driver={Arrow Flight SQL ODBC Driver};Host=h;token=***" },
                { "name": "broken", "dsn": "Nope" }
              ],
              "warmupIterations": 1,
              "iterations": 5,
              "blockSizes": [ 1000, 1 ]
            }
            """);
        config.ResolveQuery(null);

        var driverA = new DriverInfo("sqlsrv32.dll", "10.00.19041", "03.52", "Microsoft SQL Server", "16.00.1000", "db01", "SalesLegacy", "03.81.19041.0000", 3, true);
        var driverB = new DriverInfo("arrow-flight-sql-odbc.dll", "0.9.1", "03.80", "Dremio", "25.0.0", "h", "", "03.81.19041.0000", 1, true);

        var run = new RunResult
        {
            ToolVersion = "0.1.0",
            StartedUtc = new DateTime(2026, 9, 25, 18, 0, 0, DateTimeKind.Utc),
            FinishedUtc = new DateTime(2026, 9, 25, 18, 2, 30, DateTimeKind.Utc),
            Host = "BENCHBOX",
            ProcessBitness = "64-bit",
            ConfigPath = @"C:\bench\bench.json",
            ConfigSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            CommandLine = "OdbcBench run --config bench.json",
            BaselineDsn = "legacy",
            Config = config.Redacted(),
            Query = config.ResolvedQuery,
            Environment = new EnvironmentInfo
            {
                Os = "Microsoft Windows 10.0.19045 (X64)", Cpu = "Test CPU", LogicalProcessors = 8, MemoryGb = 32,
                PowerPlan = "High performance", Runtime = ".NET 8.0.8 X64", DriverManagerVersion = "03.81.19041.0000",
                OdbcVersion = "3.80", TieredCompilation = false, HighResolutionTimer = true,
            },
            Dsns =
            {
                new DsnResult { Name = "legacy", Source = "DSN=SalesLegacy", Status = "connected", Driver = driverA, FirstConnectMs = 40.5, ConnectSamplesMs = { 12, 13, 11 }, Connect = SeriesStats.From(new double[] { 12, 13, 11 }) },
                new DsnResult { Name = "flight", Source = "Driver={Arrow Flight SQL ODBC Driver};Host=h;token=***", Status = "connected", Driver = driverB, FirstConnectMs = 95.25, ConnectSamplesMs = { 30, 31, 29 }, Connect = SeriesStats.From(new double[] { 30, 31, 29 }) },
                new DsnResult { Name = "broken", Source = "DSN=Nope", Status = "connect failed", Error = "SQLDriverConnectW failed (SQL_ERROR): [IM002] (0) Data source name not found", ErrorSqlState = "IM002", Hint = "DSN 'Nope' is not defined in any ODBC administrator." },
            },
            Validation = new ValidationResult
            {
                RowsCompared = 10,
                DsnsCompared = 2,
                BaselineDsn = "legacy",
                Metadata =
                {
                    new ColumnMetadataRow { Ordinal = 1, Name = "id", ComparedAs = "integer", TypeByDsn = { ["legacy"] = "INTEGER(10) int", ["flight"] = "BIGINT(19) BIGINT" } },
                    new ColumnMetadataRow { Ordinal = 2, Name = "name", ComparedAs = "text", TypeByDsn = { ["legacy"] = "VARCHAR(40) varchar", ["flight"] = "WVARCHAR(65536) VARCHAR" } },
                },
                Issues =
                {
                    new ValidationIssue(Severity.Info, "column 'id': INTEGER(10) int on legacy vs BIGINT(19) BIGINT on flight", "id", Dsn: "flight"),
                    new ValidationIssue(Severity.Fail, "row 4, column 'name': 'Ana ' on legacy vs 'Ana|' on flight", "name", 4, "flight", true, "Ana ", "Ana|"),
                },
            },
        };

        run.Series.Add(Series("legacy", 1000, driverRows: 100_000, baseMs: 800, checksum: "00000000000000aa", columns: Bindings("SQL_C_SLONG x 4 B", "SQL_C_WCHAR x 82 B")));
        run.Series.Add(Series("flight", 1000, driverRows: 100_000, baseMs: 600, checksum: "00000000000000bb", columns: Bindings("SQL_C_SBIGINT x 8 B", "SQL_C_WCHAR x 131074 B"),
            warnings: new List<string> { "driver changed SQL_ATTR_ROW_ARRAY_SIZE from 1000 to 500" }, effective: 500));
        run.Series.Add(Series("legacy", 1, driverRows: 100_000, baseMs: 2400, checksum: "00000000000000aa", columns: Bindings("SQL_C_SLONG x 4 B", "SQL_C_WCHAR x 82 B")));
        var failing = Series("flight", 1, driverRows: 100_000, baseMs: 1900, checksum: "00000000000000bb", columns: Bindings("SQL_C_SBIGINT x 8 B", "SQL_C_WCHAR x 131074 B"));
        failing.Samples[3].Ok = false;
        failing.Samples[3].ErrorSqlState = "08S01";
        failing.Samples[3].ErrorMessage = "SQLFetchScroll failed (SQL_ERROR): [08S01] (0) Communication link failure";
        run.Series.Add(failing);

        run.Series[0].Harness = new HarnessCost { BoundColumns = 2, ValuesPerPass = 2000, NsPerValue = 4.5 };
        run.Messages.Add(new RunMessage("connect", "error", "SQLDriverConnectW failed (SQL_ERROR): [IM002] (0) Data source name not found", "broken", SqlState: "IM002"));
        run.Messages.Add(new RunMessage("benchmark", "error", failing.Samples[3].ErrorMessage!, "flight", "flight @1", 3, "08S01"));

        Analysis.Summarize(run, Iterations);
        return run;
    }

    private static List<ColumnBinding> Bindings(string first, string second) => new()
    {
        new ColumnBinding { Ordinal = 1, Name = "id", SqlType = "INTEGER", Size = "10", Binding = first },
        new ColumnBinding { Ordinal = 2, Name = "name", SqlType = "VARCHAR", Size = "40", Binding = second },
    };

    private static SeriesResult Series(string dsn, int blockSize, long driverRows, double baseMs, string checksum,
        List<ColumnBinding> columns, List<string>? warnings = null, int? effective = null)
    {
        var s = new SeriesResult
        {
            DsnName = dsn,
            BlockSize = blockSize,
            EffectiveBlockSize = effective ?? blockSize,
            Description = "test",
            Columns = columns,
            Warnings = warnings ?? new List<string>(),
        };
        s.Samples.Add(Sample(0, true, baseMs * 1.5, driverRows, checksum, effective ?? blockSize));
        double[] jitter = { 1.00, 1.02, 0.99, 1.01, 1.03 };
        for (int i = 0; i < Iterations; i++)
            s.Samples.Add(Sample(i + 1, false, baseMs * jitter[i], driverRows, checksum, effective ?? blockSize));
        return s;
    }

    private static IterationSample Sample(int index, bool warmup, double total, long rows, string checksum, int effective) => new()
    {
        Index = index,
        Warmup = warmup,
        StartedUtc = new DateTime(2026, 9, 25, 18, 0, index, DateTimeKind.Utc),
        ExecuteMs = total * 0.25,
        FirstBatchMs = total * 0.01,
        FetchMs = total * 0.74,
        CloseMs = total * 0.01,
        TotalMs = total,
        Rows = rows,
        Batches = rows / Math.Max(1, effective),
        Bytes = rows * 30,
        Checksum = checksum,
        EffectiveBlockSize = effective,
        CpuMs = total * 0.9,
    };
}
