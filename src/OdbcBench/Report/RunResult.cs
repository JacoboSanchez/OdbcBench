using System.Text.Json.Serialization;
using OdbcBench.Bench;
using OdbcBench.Config;
using OdbcBench.Fetch;
using OdbcBench.Odbc;
using OdbcBench.Validation;

namespace OdbcBench.Report;

/// <summary>Everything a run produced. Serialised as the JSON result; the Markdown report is a projection of it.</summary>
public sealed class RunResult
{
    public string ToolVersion { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public string Host { get; set; } = "";
    public string ProcessBitness { get; set; } = "";
    public string ConfigPath { get; set; } = "";
    public string ConfigSha256 { get; set; } = "";
    public string CommandLine { get; set; } = "";
    public bool Interrupted { get; set; }
    public bool Fatal { get; set; }
    public string Outcome { get; set; } = "";
    public int ExitCode { get; set; }
    public string BaselineDsn { get; set; } = "";
    public EnvironmentInfo Environment { get; set; } = new();
    /// <summary>The configuration with passwords and secrets redacted, after command-line overrides.</summary>
    public BenchConfig Config { get; set; } = new();
    public string Query { get; set; } = "";
    public List<DsnResult> Dsns { get; set; } = new();
    public ValidationResult? Validation { get; set; }
    public List<SeriesResult> Series { get; set; } = new();
    public ConsistencyResult Consistency { get; set; } = new();
    public List<RunMessage> Messages { get; set; } = new();
}

public sealed class EnvironmentInfo
{
    public string Os { get; set; } = "";
    public string Cpu { get; set; } = "";
    public int LogicalProcessors { get; set; }
    public double MemoryGb { get; set; }
    public string PowerPlan { get; set; } = "";
    public string Runtime { get; set; } = "";
    public string DriverManagerVersion { get; set; } = "";
    public string OdbcVersion { get; set; } = "";
    public string ConnectionPooling { get; set; } = "off";
    public bool TieredCompilation { get; set; }
    public bool HighResolutionTimer { get; set; }
}

public sealed class DsnResult
{
    public string Name { get; set; } = "";
    /// <summary>DSN name or redacted connection string.</summary>
    public string Source { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string? Error { get; set; }
    public string? ErrorSqlState { get; set; }
    public string? Hint { get; set; }
    public DriverInfo? Driver { get; set; }
    /// <summary>The main connection (includes loading the driver DLL the first time).</summary>
    public double FirstConnectMs { get; set; }
    /// <summary>Extra connections opened and closed after the first one, to measure connect cost.</summary>
    public List<double> ConnectSamplesMs { get; set; } = new();
    public SeriesStats? Connect { get; set; }
    public List<string> ConnectInfo { get; set; } = new();
    public bool QueryOverridden { get; set; }
    public string? Query { get; set; }
    public int Reconnects { get; set; }
}

public sealed class ColumnBinding
{
    public int Ordinal { get; set; }
    public string Name { get; set; } = "";
    public string SqlType { get; set; } = "";
    public string Size { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string Binding { get; set; } = "";
    public bool Long { get; set; }
    public bool Capped { get; set; }
}

public sealed class HarnessCost
{
    public int BoundColumns { get; set; }
    public long ValuesPerPass { get; set; }
    public double NsPerValue { get; set; }
    /// <summary>Median rows x bound columns x ns per value.</summary>
    public double EstimatedMsPerIteration { get; set; }
    /// <summary>Estimated harness time as a share of the p50 total time.</summary>
    public double PercentOfTotal { get; set; }
}

public sealed class SeriesResult
{
    public string DsnName { get; set; } = "";
    public int BlockSize { get; set; }
    public int EffectiveBlockSize { get; set; }
    public string Description { get; set; } = "";
    /// <summary>ok, partial, failed, abandoned, incomplete or not run.</summary>
    public string Status { get; set; } = "pending";
    public bool IsBaseline { get; set; }
    public List<ColumnBinding> Columns { get; set; } = new();
    public string? BlockFetchDisabledBy { get; set; }
    public long BoundBytes { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<IterationSample> Samples { get; set; } = new();

    public int OkCount { get; set; }
    public int FailedCount { get; set; }
    public long Rows { get; set; }
    public bool RowsVary { get; set; }
    public double MedianBytes { get; set; }
    public SeriesStats? Total { get; set; }
    public SeriesStats? Execute { get; set; }
    public SeriesStats? Describe { get; set; }
    public SeriesStats? FirstBatch { get; set; }
    public SeriesStats? Fetch { get; set; }
    public SeriesStats? Close { get; set; }
    public SeriesStats? Connect { get; set; }
    public SeriesStats? Cpu { get; set; }
    public double RowsPerSecond { get; set; }
    public double MegabytesPerSecond { get; set; }
    public double CpuPercent { get; set; }
    public long Truncations { get; set; }
    public int GcCollections { get; set; }
    public long AllocatedBytes { get; set; }
    public bool Noisy { get; set; }
    public bool ChecksumStable { get; set; } = true;
    public string? Checksum { get; set; }
    public double? RatioToBaseline { get; set; }
    public double? PercentVsBaseline { get; set; }
    public HarnessCost? Harness { get; set; }

    [JsonIgnore]
    public string Key => $"{DsnName} @{BlockSize}";
}

public sealed class ConsistencyResult
{
    public bool RowCountsMatch { get; set; } = true;
    public long? Rows { get; set; }
    public bool ChecksumsStable { get; set; } = true;
    public bool BlockSizeChecksumsEqual { get; set; } = true;
    public bool CrossDsnChecksumsEqual { get; set; } = true;
    public List<string> Notes { get; set; } = new();
}

public sealed record RunMessage(
    string Phase,
    string Severity,
    string Message,
    string? Dsn = null,
    string? Series = null,
    int? Iteration = null,
    string? SqlState = null);
