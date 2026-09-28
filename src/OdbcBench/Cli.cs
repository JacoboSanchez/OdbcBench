using System.Globalization;
using OdbcBench.Config;

namespace OdbcBench;

internal sealed class CliException : Exception
{
    public CliException(string message) : base(message) { }
}

/// <summary>Hand-rolled argument parsing: a handful of flags, no dependency.</summary>
internal sealed class CliOptions
{
    public string Command { get; private set; } = "run";
    public string? ConfigPath { get; private set; }
    public string? JsonPath { get; private set; }
    public string? QueryFile { get; private set; }
    public int? Iterations { get; private set; }
    public int? Warmup { get; private set; }
    public List<int>? BlockSizes { get; private set; }
    public long? Rows { get; private set; }
    public string? Table { get; private set; }
    public List<string> Dsns { get; } = new();
    public string? Output { get; private set; }
    public bool Strict { get; private set; }
    public bool NoValidate { get; private set; }
    public bool Quiet { get; private set; }
    public bool Help { get; private set; }
    public bool Version { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        int i = 0;
        if (args.Length > 0 && !args[0].StartsWith('-') && args[0] != "/?")
        {
            o.Command = args[0].ToLowerInvariant();
            i = 1;
            if (o.Command == "help")
            {
                o.Help = true;
                return o;
            }
            if (o.Command is not ("run" or "insert" or "probe" or "report"))
                throw new CliException($"unknown command '{args[0]}' (expected run, insert, probe or report)");
        }

        for (; i < args.Length; i++)
        {
            string a = args[i];
            string Value() => i + 1 < args.Length ? args[++i] : throw new CliException($"{a} needs a value");
            switch (a)
            {
                case "-c": case "--config": o.ConfigPath = Value(); break;
                case "--json": o.JsonPath = Value(); break;
                case "--query-file": o.QueryFile = Value(); break;
                case "-n": case "--iterations": o.Iterations = Int(a, Value(), 1); break;
                case "-w": case "--warmup": o.Warmup = Int(a, Value(), 0); break;
                case "-b": case "--block-size": case "--block-sizes": case "--batch-size": case "--batch-sizes":
                    o.BlockSizes = Split(Value()).Select(v => Int(a, v, 1)).ToList();
                    break;
                case "--rows": o.Rows = Long(a, Value(), 1); break;
                case "--table": o.Table = Value(); break;
                case "-d": case "--dsn": o.Dsns.AddRange(Split(Value())); break;
                case "-o": case "--output": o.Output = Value(); break;
                case "--strict": o.Strict = true; break;
                case "--no-validate": o.NoValidate = true; break;
                case "--quiet": o.Quiet = true; break;
                case "-h": case "--help": case "/?": o.Help = true; break;
                case "--version": o.Version = true; break;
                default: throw new CliException($"unknown option '{a}'");
            }
        }
        return o;
    }

    /// <summary>Applies command-line overrides to a loaded configuration (before validation).</summary>
    public void ApplyTo(BenchConfig config)
    {
        if (Iterations is int n) config.Iterations = n;
        if (Warmup is int w) config.WarmupIterations = w;
        if (BlockSizes != null) config.BlockSizes = BlockSizes.Distinct().ToList();
        if (Rows is long rows) (config.Insert ??= new InsertConfig()).Rows = rows;
        if (Table != null)
        {
            (config.Insert ??= new InsertConfig()).Table = Table;
            foreach (var d in config.Dsns) d.InsertTable = null; // the command line names the table of every DSN
        }
        if (Output != null) config.Output.Directory = Output;
        if (Strict) config.Validation.Strict = true;
        if (NoValidate) config.Validation.Enabled = false;
        if (QueryFile != null)
        {
            config.Query = null;
            config.QueryFile = Path.GetFullPath(QueryFile);
            config.ResolveQuery(null);
        }
        if (Dsns.Count > 0)
        {
            var unknown = Dsns.Where(n => !config.Dsns.Any(d => string.Equals(d.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
            if (unknown.Count > 0)
                throw new CliException($"--dsn: no DSN entry named {string.Join(", ", unknown.Select(u => $"'{u}'"))} in the configuration");
            foreach (var d in config.Dsns)
                d.Enabled = Dsns.Any(n => string.Equals(d.Name, n, StringComparison.OrdinalIgnoreCase));
            if (config.Baseline != null && !config.EnabledDsns.Any(d => string.Equals(d.Name, config.Baseline, StringComparison.OrdinalIgnoreCase)))
                config.Baseline = null; // the configured baseline was filtered out: use the first selected DSN
        }
    }

    private static IEnumerable<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int Int(string option, string value, int min)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < min)
            throw new CliException($"{option}: '{value}' is not a whole number >= {min}");
        return n;
    }

    private static long Long(string option, string value, long min)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) || n < min)
            throw new CliException($"{option}: '{value}' is not a whole number >= {min}");
        return n;
    }

    public const string Usage = """
        OdbcBench - compare ODBC drivers on the same query or batch insert through the raw ODBC API (odbc32.dll).

        Usage:
          OdbcBench run    --config FILE [options]   validate the first rows on every DSN, benchmark the query, write the report
          OdbcBench insert --config FILE [options]   benchmark batch inserts of generated rows into a table (parameter arrays)
          OdbcBench probe  --config FILE [--dsn N]   connect, show driver identity, column types and chosen bindings
          OdbcBench report --json FILE [--output DIR]  re-render the Markdown report from a saved JSON result

        Options for run and insert (override the configuration file):
          -c, --config FILE        configuration file (JSON)
          -n, --iterations N       measured iterations per series
          -w, --warmup N           warmup iterations per series (not in the statistics)
          -b, --block-size N[,N]   row array sizes to benchmark (SQL_ATTR_ROW_ARRAY_SIZE); for insert, the
              --batch-size N[,N]   parameter array sizes (SQL_ATTR_PARAMSET_SIZE)
          -d, --dsn NAME[,NAME]    only these DSN entries (by name); repeatable
              --query-file FILE    read the query from FILE instead of the configuration
              --table NAME         insert: target table of every DSN
              --rows N             insert: rows inserted per iteration
          -o, --output DIR         output directory for the .md and .json files
              --strict             stop before benchmarking when validation fails
              --no-validate        skip the first-rows validation and the dry run
              --quiet              no progress output
              --version            print the version
          -h, --help               this help

        Exit codes: 0 success; 1 completed with problems (failed DSN or series, row counts differ,
        validation FAIL, interrupted); 2 validation failed in strict mode; 3 usage or configuration
        error, or no DSN could be connected.
        """;
}
