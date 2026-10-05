using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OdbcBench.Fetch;
using OdbcBench.Initialize;
using OdbcBench.Insert;
using OdbcBench.Odbc;
using OdbcBench.Validation;

namespace OdbcBench.Config;

public sealed class ConfigException : Exception
{
    public ConfigException(string message) : base(message) { }
}

/// <summary>What a run measures: reading the result of a query, or inserting generated rows in batches.</summary>
public enum Workload { Select, Insert }

public sealed class DsnConfig
{
    public string Name { get; set; } = "";
    public string? Dsn { get; set; }
    /// <summary>Full connection string instead of a DSN (UID/PWD are appended when given separately).</summary>
    public string? ConnectionString { get; set; }
    public string? Uid { get; set; }
    public string? Pwd { get; set; }
    /// <summary>Name of an environment variable holding the password.</summary>
    public string? PwdEnv { get; set; }
    /// <summary>Extra "key=value;" attributes appended to the connection string.</summary>
    public string? ExtraAttributes { get; set; }
    /// <summary>Per-DSN query override (dialect differences); null uses the global query.</summary>
    public string? Query { get; set; }
    /// <summary>Per-DSN target table of the insert benchmark (naming and quoting differences); null uses insert.table.</summary>
    public string? InsertTable { get; set; }
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string? ResolvedPassword { get; set; }

    public string BuildConnectionString()
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            sb.Append(ConnectionString.Trim().TrimEnd(';')).Append(';');
        }
        else
        {
            sb.Append("DSN=").Append(Quote(Dsn ?? "")).Append(';');
        }
        if (!string.IsNullOrEmpty(Uid)) sb.Append("UID=").Append(Quote(Uid)).Append(';');
        string? pwd = ResolvedPassword ?? Pwd;
        if (pwd != null) sb.Append("PWD=").Append(Quote(pwd)).Append(';');
        if (!string.IsNullOrWhiteSpace(ExtraAttributes)) sb.Append(ExtraAttributes.Trim().TrimEnd(';')).Append(';');
        return sb.ToString();
    }

    /// <summary>Brace-quotes a connection-string value when it contains characters the Driver Manager would misparse.</summary>
    public static string Quote(string value)
    {
        if (value.Length == 0) return value;
        bool needsQuoting = value.IndexOfAny(new[] { ';', '{', '}', '=' }) >= 0 || value[0] == ' ' || value[^1] == ' ';
        if (!needsQuoting) return value;
        return "{" + value.Replace("}", "}}") + "}";
    }

    /// <summary>What to show for this entry: the DSN name, or the redacted connection string.</summary>
    [JsonIgnore]
    public string Source => !string.IsNullOrWhiteSpace(Dsn) ? $"DSN={Dsn}" : BenchConfig.Redact(ConnectionString ?? "");
}

public sealed class ValidationConfig
{
    public bool Enabled { get; set; } = true;
    public int Rows { get; set; } = 10;
    public bool Strict { get; set; } = false;
    public NormalizationOptions Normalization { get; set; } = new();
}

/// <summary>The insert benchmark: generated rows written to one table with parameter arrays.</summary>
public sealed class InsertConfig
{
    /// <summary>Target table, written as it appears in SQL (qualified and quoted as the DBMS needs).</summary>
    public string? Table { get; set; }
    /// <summary>Columns to fill, written as they appear in SQL; null fills every column that accepts a value.</summary>
    public List<string>? Columns { get; set; }
    /// <summary>Rows inserted per iteration.</summary>
    public long Rows { get; set; } = 100_000;
    /// <summary>Statement run before every iteration, never timed: none, delete or truncate.</summary>
    public string Cleanup { get; set; } = "none";
    /// <summary>When the rows are committed: perIteration, perBatch or autocommit.</summary>
    public string Transaction { get; set; } = "perIteration";
    /// <summary>Count the rows of the table before and after every iteration (never timed).</summary>
    public bool Verify { get; set; } = true;
    /// <summary>Characters or bytes generated for text and binary columns, capped at the column size.</summary>
    public int ValueLength { get; set; } = 32;
}

/// <summary>Creates and populates the portable dataset used by read and insert benchmarks.</summary>
public sealed class InitializeConfig
{
    /// <summary>Schema that owns the generated objects. Empty means the connection's default schema.</summary>
    public string Schema { get; set; } = "odbcbench";
    /// <summary>What to do when a generated table already exists: fail or recreate.</summary>
    public string Existing { get; set; } = "fail";
    /// <summary>Rows in each copy of every selected read shape.</summary>
    public List<long> RowCounts { get; set; } = new() { 10_000, 1_000_000 };
    /// <summary>Built-in read shapes to create: narrow, wide, text and numeric.</summary>
    public List<string> Shapes { get; set; } = new() { "narrow", "wide", "text", "numeric" };
    /// <summary>ODBC parameter-array size used while populating read tables.</summary>
    public int BatchSize { get; set; } = 1000;
    /// <summary>Maximum generated character count for text columns (also caps binary bytes).</summary>
    public int ValueLength { get; set; } = 512;
    public bool CreateIndexes { get; set; } = true;
    public bool Analyze { get; set; } = true;
    public bool CreateInsertTable { get; set; } = true;
    public string InsertTable { get; set; } = "insert_target";
}

public sealed class OutputConfig
{
    public string Directory { get; set; } = "results";
    public string Prefix { get; set; } = "run";
    public bool Markdown { get; set; } = true;
    public bool Json { get; set; } = true;
}

public sealed partial class BenchConfig
{
    public string? Query { get; set; }
    public string? QueryFile { get; set; }
    /// <summary>Name of the DSN every other one is compared against; default: the first enabled DSN.</summary>
    public string? Baseline { get; set; }
    public List<DsnConfig> Dsns { get; set; } = new();

    public string OdbcVersion { get; set; } = "3.80";
    public int WarmupIterations { get; set; } = 3;
    public int Iterations { get; set; } = 20;
    public int ConnectSamples { get; set; } = 5;
    public List<int> BlockSizes { get; set; } = new() { 1000 };
    public string BindMode { get; set; } = "native";
    public bool ReuseStatement { get; set; } = true;
    public bool ConnectionPerIteration { get; set; } = false;
    public bool Interleave { get; set; } = true;
    public int PauseBetweenIterationsMs { get; set; } = 0;
    public bool CacheBuster { get; set; } = false;
    /// <summary>Select only: simulated client work per fetched row, spun after every row array (inside FetchMs and TotalMs).</summary>
    public double RowProcessingMicros { get; set; } = 0;
    public bool Calibrate { get; set; } = true;
    public string ProcessPriority { get; set; } = "normal";
    public int QueryTimeoutSeconds { get; set; } = 0;
    public int LoginTimeoutSeconds { get; set; } = 30;
    public int MaxConsecutiveErrors { get; set; } = 3;
    public string LongColumnMode { get; set; } = "rowByRow";
    public long LongColumnThresholdBytes { get; set; } = 8000;
    public int LongColumnCapBytes { get; set; } = 65536;
    public long MaxBoundBytes { get; set; } = 256L * 1024 * 1024;
    public int GetDataChunkBytes { get; set; } = 32768;
    /// <summary>Only needed by the insert benchmark.</summary>
    public InsertConfig? Insert { get; set; }
    /// <summary>Dataset created by the init command.</summary>
    public InitializeConfig? Initialize { get; set; }
    public ValidationConfig Validation { get; set; } = new();
    public OutputConfig Output { get; set; } = new();

    [JsonIgnore]
    public string ResolvedQuery { get; set; } = "";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static BenchConfig Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new ConfigException($"cannot read configuration file '{path}': {ex.Message}");
        }
        var config = Parse(json);
        config.ResolveQuery(Path.GetDirectoryName(Path.GetFullPath(path)));
        return config;
    }

    public static BenchConfig Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BenchConfig>(json, JsonOptions) ?? throw new ConfigException("the configuration is empty");
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"invalid configuration JSON: {ex.Message}");
        }
    }

    public void ResolveQuery(string? baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(Query))
        {
            ResolvedQuery = Query.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(QueryFile))
        {
            string path = Path.IsPathRooted(QueryFile) ? QueryFile : Path.Combine(baseDirectory ?? ".", QueryFile);
            try
            {
                ResolvedQuery = File.ReadAllText(path).Trim();
            }
            catch (Exception ex)
            {
                throw new ConfigException($"cannot read query file '{path}': {ex.Message}");
            }
        }
    }

    [JsonIgnore]
    public IEnumerable<DsnConfig> EnabledDsns => Dsns.Where(d => d.Enabled);

    public string QueryFor(DsnConfig dsn) => string.IsNullOrWhiteSpace(dsn.Query) ? ResolvedQuery : dsn.Query.Trim();

    public string TableFor(DsnConfig dsn) => (string.IsNullOrWhiteSpace(dsn.InsertTable) ? Insert?.Table ?? "" : dsn.InsertTable).Trim();

    [JsonIgnore]
    public bool HasQuery => !string.IsNullOrWhiteSpace(ResolvedQuery) || EnabledDsns.Any(d => !string.IsNullOrWhiteSpace(d.Query));

    [JsonIgnore]
    public CleanupMode CleanupModeValue => (Insert?.Cleanup ?? "none").Trim().ToLowerInvariant() switch
    {
        "none" => CleanupMode.None,
        "delete" => CleanupMode.Delete,
        "truncate" => CleanupMode.Truncate,
        _ => throw new ConfigException($"insert.cleanup must be 'none', 'delete' or 'truncate', not '{Insert?.Cleanup}'"),
    };

    [JsonIgnore]
    public TransactionMode TransactionModeValue => (Insert?.Transaction ?? "perIteration").Trim().ToLowerInvariant() switch
    {
        "periteration" => TransactionMode.PerIteration,
        "perbatch" => TransactionMode.PerBatch,
        "autocommit" => TransactionMode.Autocommit,
        _ => throw new ConfigException($"insert.transaction must be 'perIteration', 'perBatch' or 'autocommit', not '{Insert?.Transaction}'"),
    };

    [JsonIgnore]
    public BindMode BindModeValue => BindMode.Trim().ToLowerInvariant() switch
    {
        "native" => Odbc.BindMode.Native,
        "wchar" => Odbc.BindMode.WChar,
        _ => throw new ConfigException($"bindMode must be 'native' or 'wchar', not '{BindMode}'"),
    };

    [JsonIgnore]
    public LongColumnMode LongColumnModeValue => LongColumnMode.Trim().ToLowerInvariant() switch
    {
        "rowbyrow" => Fetch.LongColumnMode.RowByRow,
        "bindcapped" => Fetch.LongColumnMode.BindCapped,
        _ => throw new ConfigException($"longColumnMode must be 'rowByRow' or 'bindCapped', not '{LongColumnMode}'"),
    };

    /// <summary>Returns every problem found for the given workload; an empty list means the configuration is usable.</summary>
    public List<string> Validate(Workload workload = Workload.Select)
    {
        var errors = new List<string>();
        var enabled = EnabledDsns.ToList();
        if (enabled.Count == 0) errors.Add("at least one enabled DSN is required");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Dsns)
        {
            if (string.IsNullOrWhiteSpace(d.Name)) errors.Add("every DSN entry needs a name");
            else if (!names.Add(d.Name)) errors.Add($"DSN name '{d.Name}' is used more than once");
            if (string.IsNullOrWhiteSpace(d.Dsn) && string.IsNullOrWhiteSpace(d.ConnectionString))
                errors.Add($"DSN '{d.Name}': either dsn or connectionString is required");
            if (workload == Workload.Select && d.Enabled && string.IsNullOrWhiteSpace(d.Query) && string.IsNullOrWhiteSpace(ResolvedQuery))
                errors.Add($"DSN '{d.Name}': no query (set the global query/queryFile or a per-DSN query)");
            if (workload == Workload.Insert && d.Enabled && TableFor(d).Length == 0)
                errors.Add($"DSN '{d.Name}': no target table (set insert.table or a per-DSN insertTable)");
        }

        if (workload == Workload.Insert)
        {
            var insert = Insert ?? new InsertConfig();
            if (insert.Rows < 1) errors.Add("insert.rows must be at least 1");
            if (insert.Columns != null && insert.Columns.Any(string.IsNullOrWhiteSpace)) errors.Add("insert.columns contains an empty name");
            if (insert.Cleanup.Trim().ToLowerInvariant() is not ("none" or "delete" or "truncate")) errors.Add($"insert.cleanup must be 'none', 'delete' or 'truncate', not '{insert.Cleanup}'");
            if (insert.Transaction.Trim().ToLowerInvariant() is not ("periteration" or "perbatch" or "autocommit")) errors.Add($"insert.transaction must be 'perIteration', 'perBatch' or 'autocommit', not '{insert.Transaction}'");
            if (insert.ValueLength is < 1 or > 4000) errors.Add("insert.valueLength must be between 1 and 4000");
        }

        if (Iterations < 1) errors.Add("iterations must be at least 1");
        if (WarmupIterations < 0) errors.Add("warmupIterations cannot be negative");
        if (ConnectSamples < 0) errors.Add("connectSamples cannot be negative");
        if (RowProcessingMicros < 0) errors.Add("rowProcessingMicros cannot be negative");
        if (BlockSizes.Count == 0) errors.Add("blockSizes needs at least one value");
        foreach (var b in BlockSizes) if (b < 1) errors.Add($"blockSizes: {b} is not a valid row array size");
        if (BlockSizes.Distinct().Count() != BlockSizes.Count) errors.Add("blockSizes contains duplicates");
        if (BindMode.Trim().ToLowerInvariant() is not ("native" or "wchar")) errors.Add($"bindMode must be 'native' or 'wchar', not '{BindMode}'");
        if (LongColumnMode.Trim().ToLowerInvariant() is not ("rowbyrow" or "bindcapped")) errors.Add($"longColumnMode must be 'rowByRow' or 'bindCapped', not '{LongColumnMode}'");
        if (ProcessPriority.Trim().ToLowerInvariant() is not ("normal" or "high" or "abovenormal")) errors.Add($"processPriority must be 'normal', 'aboveNormal' or 'high', not '{ProcessPriority}'");
        if (OdbcVersion.Trim() is not ("3.80" or "3.8" or "3.0" or "3")) errors.Add($"odbcVersion must be '3.80' or '3.0', not '{OdbcVersion}'");
        if (LongColumnThresholdBytes < 16) errors.Add("longColumnThresholdBytes is too small");
        if (LongColumnCapBytes < 64) errors.Add("longColumnCapBytes is too small");
        if (MaxBoundBytes < 65536) errors.Add("maxBoundBytes is too small");
        if (GetDataChunkBytes < 1024) errors.Add("getDataChunkBytes must be at least 1024");
        if (Validation.Rows < 1) errors.Add("validation.rows must be at least 1");
        if (MaxConsecutiveErrors < 1) errors.Add("maxConsecutiveErrors must be at least 1");
        if (!string.IsNullOrWhiteSpace(Baseline) && !enabled.Any(d => string.Equals(d.Name, Baseline, StringComparison.OrdinalIgnoreCase)))
            errors.Add($"baseline '{Baseline}' does not match an enabled DSN name");
        return errors;
    }

    /// <summary>Returns every problem that prevents the init command from running.</summary>
    public List<string> ValidateInitialization()
    {
        var errors = new List<string>();
        var enabled = EnabledDsns.ToList();
        if (enabled.Count == 0) errors.Add("at least one enabled DSN is required");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Dsns)
        {
            if (string.IsNullOrWhiteSpace(d.Name)) errors.Add("every DSN entry needs a name");
            else if (!names.Add(d.Name)) errors.Add($"DSN name '{d.Name}' is used more than once");
            if (string.IsNullOrWhiteSpace(d.Dsn) && string.IsNullOrWhiteSpace(d.ConnectionString))
                errors.Add($"DSN '{d.Name}': either dsn or connectionString is required");
        }

        var init = Initialize ?? new InitializeConfig();
        if (!IsSimpleIdentifier(init.Schema, allowEmpty: true))
            errors.Add("initialize.schema must be empty or a simple SQL identifier (letters, digits and underscore; not starting with a digit)");
        if (!IsSimpleIdentifier(init.InsertTable, allowEmpty: false))
            errors.Add("initialize.insertTable must be a simple SQL identifier");
        // Case-insensitive: Oracle folds the generated names to upper case and SQL Server usually compares them that way.
        else if (init.CreateInsertTable && init.Shapes.Any(shape => init.RowCounts.Any(rows => string.Equals(
                     InitializationCatalog.ReadTableName(shape, rows), init.InsertTable.Trim(), StringComparison.OrdinalIgnoreCase))))
            errors.Add($"initialize.insertTable '{init.InsertTable.Trim()}' is also the name of a generated read table");
        if (init.Existing.Trim().ToLowerInvariant() is not ("fail" or "recreate"))
            errors.Add("initialize.existing must be 'fail' or 'recreate'");
        if (init.RowCounts.Count == 0) errors.Add("initialize.rowCounts needs at least one value");
        if (init.RowCounts.Any(n => n < 1)) errors.Add("initialize.rowCounts values must be at least 1");
        if (init.RowCounts.Distinct().Count() != init.RowCounts.Count) errors.Add("initialize.rowCounts contains duplicates");
        if (init.Shapes.Count == 0) errors.Add("initialize.shapes needs at least one shape");
        var validShapes = new HashSet<string>(new[] { "narrow", "wide", "text", "numeric" }, StringComparer.OrdinalIgnoreCase);
        foreach (string shape in init.Shapes)
            if (!validShapes.Contains(shape.Trim())) errors.Add($"initialize.shapes: unknown shape '{shape}'");
        if (init.Shapes.Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != init.Shapes.Count)
            errors.Add("initialize.shapes contains duplicates");
        if (init.BatchSize < 1) errors.Add("initialize.batchSize must be at least 1");
        if (init.ValueLength is < 1 or > 4000) errors.Add("initialize.valueLength must be between 1 and 4000");
        if (LoginTimeoutSeconds < 0) errors.Add("loginTimeoutSeconds cannot be negative");
        if (QueryTimeoutSeconds < 0) errors.Add("queryTimeoutSeconds cannot be negative");
        if (MaxBoundBytes < 65536) errors.Add("maxBoundBytes is too small");
        if (OdbcVersion.Trim() is not ("3.80" or "3.8" or "3.0" or "3")) errors.Add($"odbcVersion must be '3.80' or '3.0', not '{OdbcVersion}'");
        return errors;
    }

    private static bool IsSimpleIdentifier(string value, bool allowEmpty)
    {
        value = value.Trim();
        if (value.Length == 0) return allowEmpty;
        if (!(char.IsLetter(value[0]) || value[0] == '_')) return false;
        return value.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    /// <summary>Fills ResolvedPassword for every enabled DSN: pwd, then pwdEnv, then the prompt callback.</summary>
    public void ResolvePasswords(Func<DsnConfig, string?> prompt)
    {
        foreach (var d in EnabledDsns)
        {
            if (d.Pwd != null)
            {
                d.ResolvedPassword = d.Pwd;
            }
            else if (!string.IsNullOrWhiteSpace(d.PwdEnv))
            {
                d.ResolvedPassword = Environment.GetEnvironmentVariable(d.PwdEnv)
                    ?? throw new ConfigException($"DSN '{d.Name}': environment variable '{d.PwdEnv}' is not set");
            }
            else if (!string.IsNullOrEmpty(d.Uid) && !HasPassword(d.ConnectionString))
            {
                d.ResolvedPassword = prompt(d);
            }
        }
    }

    public DsnConfig BaselineDsn()
    {
        var enabled = EnabledDsns.ToList();
        if (!string.IsNullOrWhiteSpace(Baseline))
            return enabled.First(d => string.Equals(d.Name, Baseline, StringComparison.OrdinalIgnoreCase));
        return enabled[0];
    }

    /// <summary>A deep copy safe to persist: passwords replaced, connection strings scrubbed.</summary>
    public BenchConfig Redacted()
    {
        var copy = JsonSerializer.Deserialize<BenchConfig>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
        copy.ResolvedQuery = ResolvedQuery;
        foreach (var d in copy.Dsns)
        {
            if (d.Pwd != null) d.Pwd = "***";
            if (d.ConnectionString != null) d.ConnectionString = Redact(d.ConnectionString);
            if (d.ExtraAttributes != null) d.ExtraAttributes = Redact(d.ExtraAttributes);
        }
        return copy;
    }

    /// <summary>Replaces the value of every attribute whose key mentions a password, token or secret.</summary>
    public static string Redact(string connectionString) => SecretAttribute().Replace(connectionString, "${key}***");

    private static bool HasPassword(string? connectionString) =>
        !string.IsNullOrEmpty(connectionString) && PasswordKey().IsMatch(connectionString);

    [GeneratedRegex(@"(?<key>(?:^|;)\s*[^=;]*(?:pwd|password|passwd|token|secret)[^=;]*=)\s*(?:\{(?:[^}]|\}\})*\}|[^;]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretAttribute();

    [GeneratedRegex(@"(?:^|;)\s*(?:pwd|password)\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PasswordKey();

    public BlockFetchOptions FetchOptions(int blockSize) => new()
    {
        BlockSize = blockSize,
        BindMode = BindModeValue,
        ReuseStatement = ReuseStatement,
        LongColumnMode = LongColumnModeValue,
        LongColumnThresholdBytes = LongColumnThresholdBytes,
        LongColumnCapBytes = LongColumnCapBytes,
        MaxBoundBytes = MaxBoundBytes,
        QueryTimeoutSeconds = QueryTimeoutSeconds,
        GetDataChunkBytes = GetDataChunkBytes,
        CacheBuster = CacheBuster,
        RowProcessingMicros = RowProcessingMicros,
    };

    public InsertOptions InsertOptions(int batchSize) => new()
    {
        BatchSize = batchSize,
        Rows = Insert?.Rows ?? 100_000,
        BindMode = BindModeValue,
        ReuseStatement = ReuseStatement,
        Transaction = TransactionModeValue,
        Verify = Insert?.Verify ?? true,
        MaxBoundBytes = MaxBoundBytes,
        QueryTimeoutSeconds = QueryTimeoutSeconds,
    };

    /// <summary>The table one DSN writes to. It is described on first use, through that DSN's connection.</summary>
    public InsertTarget InsertTargetFor(DsnConfig dsn) =>
        new(TableFor(dsn), Insert?.Columns?.Select(c => c.Trim()).ToList(), BindModeValue, Insert?.ValueLength ?? 32, CleanupModeValue);

    [JsonIgnore]
    public uint OdbcVersionValue => OdbcVersion.Trim() is "3.0" or "3" ? 3u : 380u;
}
