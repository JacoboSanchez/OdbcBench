using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OdbcBench.Bench;
using OdbcBench.Config;
using OdbcBench.Fetch;
using OdbcBench.Insert;
using OdbcBench.Odbc;
using OdbcBench.Report;

namespace OdbcBench;

internal static class Program
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch (IOException)
        {
            // no console attached
        }

        CliOptions cli;
        try
        {
            cli = CliOptions.Parse(args);
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine("Run 'OdbcBench --help' for usage.");
            return 3;
        }

        if (cli.Help || args.Length == 0)
        {
            Console.WriteLine(CliOptions.Usage);
            return args.Length == 0 ? 3 : 0;
        }
        if (cli.Version)
        {
            Console.WriteLine($"OdbcBench {ToolInfo.Version}");
            return 0;
        }

        if (!Environment.Is64BitProcess && cli.Command != "report")
        {
            Console.Error.WriteLine("error: OdbcBench must run as a 64-bit process (it declares SQLLEN as 8 bytes).");
            return 3;
        }

        try
        {
            return cli.Command switch
            {
                "probe" => Probe(cli),
                "report" => Report(cli),
                "insert" => Run(cli, Workload.Insert),
                _ => Run(cli, Workload.Select),
            };
        }
        catch (Exception ex) when (ex is ConfigException or CliException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 3;
        }
        catch (DllNotFoundException ex)
        {
            Console.Error.WriteLine($"error: the ODBC Driver Manager could not be loaded: {ex.Message}");
            return 3;
        }
    }

    // ---------------------------------------------------------------- run

    private static int Run(CliOptions cli, Workload workload)
    {
        var config = LoadConfig(cli, workload);
        config.ResolvePasswords(PromptPassword);

        string outputDirectory = Path.GetFullPath(config.Output.Directory);
        Directory.CreateDirectory(outputDirectory);

        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            if (cancel.IsCancellationRequested) return; // second Ctrl+C: let the process end
            e.Cancel = true;
            cancel.Cancel();
            Console.Error.WriteLine("Stopping after the current iteration; partial results will be written. Press Ctrl+C again to abort immediately.");
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var options = new RunOptions
            {
                Workload = workload,
                Strict = config.Validation.Strict,
                Validate = config.Validation.Enabled,
                Quiet = cli.Quiet,
                ConfigPath = Path.GetFullPath(cli.ConfigPath!),
                ConfigSha256 = Sha256(cli.ConfigPath!),
                CommandLine = CommandLine(),
            };
            var run = new BenchmarkRunner(config, options, Console.Error, cancel.Token).Run();

            string prefix = config.Output.Prefix + (workload == Workload.Insert ? "-insert" : "");
            string stem = Path.Combine(outputDirectory, $"{prefix}-{run.StartedUtc.ToLocalTime():yyyyMMdd-HHmmss}");
            var written = new List<string>();
            if (config.Output.Json)
            {
                JsonResultWriter.Write(run, stem + ".json");
                written.Add(stem + ".json");
            }
            if (config.Output.Markdown)
            {
                MarkdownReportWriter.Write(run, stem + ".md");
                written.Add(stem + ".md");
            }

            PrintSummary(run, written);
            return run.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <param name="workload">Null checks whatever the file configures: the query, the insert section, or both (probe).</param>
    private static BenchConfig LoadConfig(CliOptions cli, Workload? workload)
    {
        if (cli.ConfigPath == null) throw new CliException("--config FILE is required");
        var config = BenchConfig.Load(cli.ConfigPath);
        cli.ApplyTo(config);
        var errors = workload is Workload w ? config.Validate(w)
            : config.Insert == null ? config.Validate(Workload.Select)
            : config.HasQuery ? config.Validate(Workload.Select).Union(config.Validate(Workload.Insert)).ToList()
            : config.Validate(Workload.Insert);
        if (errors.Count > 0)
            throw new ConfigException("invalid configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "  - " + e)));
        return config;
    }

    private static void PrintSummary(RunResult run, List<string> written)
    {
        var o = Console.Out;
        o.WriteLine();
        o.WriteLine($"Outcome    : {run.Outcome} (exit code {run.ExitCode})");
        o.WriteLine($"Validation : {run.Validation?.Status ?? (run.Config.Validation.Enabled ? "not run" : "skipped")}");
        bool insert = run.Workload == Workload.Insert;
        if (run.Series.Any(s => s.OkCount > 0))
            o.WriteLine($"Row counts : {(run.Consistency.RowCountsMatch ? string.Create(Inv, $"match ({run.Consistency.Rows:N0} rows)") : "DIFFER between DSNs or iterations")}");
        if (insert && run.Series.Any(s => s.OkCount > 0))
            o.WriteLine($"Table      : {(!run.Consistency.RowsAccepted ? "the driver did NOT ACCEPT every row"
                : !run.Consistency.TableRowsMatch ? "its row count DIFFERS from the rows the driver accepted"
                : run.Consistency.TableChecks > 0 ? "gained the rows that were sent in every iteration"
                : "not counted")}");
        foreach (var group in run.Series.GroupBy(s => s.BlockSize))
        {
            o.WriteLine(string.Create(Inv, $"{(insert ? "Batch" : "Block")} size {group.Key:N0}, p50 total per iteration:"));
            foreach (var s in group)
            {
                string time = s.Total == null ? s.Status : $"{MarkdownReportWriter.Ms(s.Total.P50),12} ms";
                string ratio = s.IsBaseline ? "baseline" : s.RatioToBaseline is double r ? $"{r.ToString("0.00", Inv)}x" : "";
                string cv = s.Total == null ? "" : $"CV {s.Total.Cv.ToString("0.0", Inv)}%";
                o.WriteLine($"  {s.DsnName,-24} {time,15}  {ratio,-9} {cv}");
            }
        }
        foreach (var d in run.Dsns.Where(d => d.Status != "connected"))
            o.WriteLine($"  {d.Name}: {d.Status}{(d.ErrorSqlState != null ? $" [{d.ErrorSqlState}]" : "")}");
        foreach (var path in written) o.WriteLine($"Written    : {path}");
    }

    // ---------------------------------------------------------------- probe

    private static int Probe(CliOptions cli)
    {
        var config = LoadConfig(cli, workload: null);
        config.ResolvePasswords(PromptPassword);
        var o = Console.Out;
        int blockSize = config.BlockSizes[0];

        using var environment = new OdbcEnvironment(config.OdbcVersionValue);
        o.WriteLine($"OdbcBench {ToolInfo.Version} probe: {(Environment.Is64BitProcess ? "64" : "32")}-bit process, " +
                    $"Driver Manager {SystemInfo.DriverManagerFileVersion()}, ODBC {environment.OdbcVersionText} behaviour, connection pooling off");

        int failures = 0;
        foreach (var d in config.EnabledDsns)
        {
            o.WriteLine();
            o.WriteLine($"{d.Name}  ({d.Source})");
            if (!string.IsNullOrWhiteSpace(d.Dsn))
            {
                var locations = OdbcDataSources.FindDsn(d.Dsn);
                o.WriteLine($"  defined in   : {(locations.Count == 0 ? "not found in the ODBC configuration (system or user DSNs)" : string.Join("; ", locations))}");
            }

            OdbcConnection connection;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                connection = OdbcConnection.Open(environment, d.BuildConnectionString(), config.LoginTimeoutSeconds);
            }
            catch (OdbcException ex)
            {
                o.WriteLine($"  connect      : FAILED {ex.Message}");
                var hint = OdbcDataSources.Hint(d.Dsn, ex.SqlState);
                if (hint != null) o.WriteLine($"  hint         : {hint}");
                failures++;
                continue;
            }
            double connectMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            using (connection)
            {
                var di = connection.Driver!;
                o.WriteLine($"  connect      : {MarkdownReportWriter.Ms(connectMs)} ms (first connection, includes loading the driver)");
                o.WriteLine($"  driver       : {di.DriverName} {di.DriverVersion}, ODBC {di.DriverOdbcVersion}, Unicode entry points: {(di.UnicodeNative switch { true => "yes", false => "NO (the Driver Manager converts every string)", _ => "unknown" })}");
                o.WriteLine($"  DBMS         : {di.DbmsName} {di.DbmsVersion}{(di.ServerName.Length > 0 ? $" on {di.ServerName}" : "")}");
                o.WriteLine($"  SQLGetData   : {di.GetDataExtensionsText}");
                foreach (var info in connection.Info) o.WriteLine($"  connect info : {info}");

                if (config.QueryFor(d).Length > 0 && !ProbeQuery(o, config, d, connection, blockSize)) failures++;
                if (config.Insert != null && config.TableFor(d).Length > 0 && !ProbeInsert(o, config, d, connection, blockSize)) failures++;
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static bool ProbeQuery(TextWriter o, BenchConfig config, DsnConfig d, OdbcConnection connection, int blockSize)
    {
        using var reader = new BlockFetchReader(connection, config.QueryFor(d), config.FetchOptions(blockSize));
        var sample = reader.Execute(1, warmup: true, rowLimit: 1);
        if (!sample.Ok)
        {
            o.WriteLine($"  query        : FAILED {sample.ErrorMessage}");
            return false;
        }
        o.WriteLine(string.Create(Inv, $"  query        : SQLExecDirectW {MarkdownReportWriter.Ms(sample.ExecuteMs)} ms, first SQLFetchScroll {MarkdownReportWriter.Ms(sample.FirstBatchMs)} ms returned {sample.Rows:N0} rows"));
        o.WriteLine(string.Create(Inv, $"  row array    : requested {blockSize:N0}, effective {reader.CurrentArraySize:N0}"));
        if (reader.Columns is { } columns)
        {
            o.WriteLine($"  columns      : {columns.Count}");
            o.WriteLine($"    {"#",3}  {"name",-28} {"SQL type",-16} {"size",-10} {"type name",-18} binding");
            foreach (var c in columns)
            {
                bool unbound = reader.Plan?.Unbound.Contains(c) == true;
                o.WriteLine($"    {c.Ordinal,3}  {Clip(c.Name, 28),-28} {c.SqlTypeName,-16} {c.SizeText,-10} {Clip(c.TypeName, 18),-18} {(unbound ? $"SQLGetData as {c.CTypeName}" : c.BindingText)}");
            }
        }
        foreach (var w in reader.Warnings) o.WriteLine($"  note         : {w}");
        foreach (var w in sample.Warnings ?? new List<string>()) o.WriteLine($"  note         : {w}");
        return true;
    }

    /// <summary>Describes the target table, prepares the INSERT and binds the parameter arrays. Nothing is executed, so no row is written.</summary>
    private static bool ProbeInsert(TextWriter o, BenchConfig config, DsnConfig d, OdbcConnection connection, int batchSize)
    {
        var target = config.InsertTargetFor(d);
        using var writer = new BatchInsertWriter(connection, target, config.InsertOptions(batchSize));
        var sample = writer.Bind();
        if (!sample.Ok)
        {
            o.WriteLine($"  insert       : FAILED {sample.ErrorMessage}");
            foreach (var w in writer.Warnings) o.WriteLine($"  note         : {w}");
            return false;
        }
        o.WriteLine($"  insert       : {target.InsertSql}");
        o.WriteLine($"  prepare+bind : SQLPrepareW + SQLBindParameter {MarkdownReportWriter.Ms(sample.DescribeMs)} ms; nothing was executed");
        o.WriteLine(string.Create(Inv, $"  param array  : requested {batchSize:N0}, effective {sample.EffectiveBlockSize:N0} (SQL_ATTR_PARAMSET_SIZE)"));
        o.WriteLine($"  transactions : {(config.TransactionModeValue == TransactionMode.Autocommit ? "autocommit on, as configured"
            : writer.ManualCommit ? "autocommit off accepted"
            : "autocommit could NOT be turned off")}" +
            (connection.GetInfoUInt(Native.SQL_TXN_CAPABLE) == Native.SQL_TC_NONE ? "; the driver reports no transaction support (SQL_TXN_CAPABLE)" : ""));
        if (target.CleanupSql != null) o.WriteLine($"  cleanup      : {target.CleanupSql} (before every iteration)");
        o.WriteLine($"  parameters   : {target.Columns.Count}");
        o.WriteLine($"    {"#",3}  {"name",-28} {"SQL type",-16} {"size",-10} {"type name",-18} {"binding",-26} value of row 1");
        for (int i = 0; i < target.Columns.Count; i++)
        {
            var c = target.Columns[i].Column;
            o.WriteLine($"    {i + 1,3}  {Clip(c.Name, 28),-28} {c.SqlTypeName,-16} {c.SizeText,-10} {Clip(c.TypeName, 18),-18} {c.BindingText,-26} {Clip(DataGenerator.Text(target.Columns[i], 1), 40)}");
        }
        foreach (var w in writer.Warnings) o.WriteLine($"  note         : {w}");
        return true;
    }

    // ---------------------------------------------------------------- report

    private static int Report(CliOptions cli)
    {
        if (cli.JsonPath == null) throw new CliException("--json FILE is required");
        var run = JsonResultWriter.Read(cli.JsonPath);
        string directory = cli.Output != null ? Path.GetFullPath(cli.Output) : Path.GetDirectoryName(Path.GetFullPath(cli.JsonPath))!;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Path.GetFileNameWithoutExtension(cli.JsonPath) + ".md");
        MarkdownReportWriter.Write(run, path);
        Console.WriteLine($"Written    : {path}");
        return 0;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Masked prompt. An empty answer means "use the password stored in the DSN".</summary>
    private static string? PromptPassword(DsnConfig d)
    {
        if (Console.IsInputRedirected)
            throw new ConfigException($"DSN '{d.Name}' has a uid but no password: set pwd or pwdEnv (input is redirected, so the password cannot be prompted)");
        Console.Error.Write($"Password for {d.Uid} on {d.Name} (Enter = use the DSN's stored password): ");
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            if (key.KeyChar != '\0') sb.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string Sha256(string path)
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static string CommandLine()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a);
        return string.Join(' ', new[] { "OdbcBench" }.Concat(args));
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "~";
}
