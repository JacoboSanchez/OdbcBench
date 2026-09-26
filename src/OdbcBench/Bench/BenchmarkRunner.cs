using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using OdbcBench.Config;
using OdbcBench.Fetch;
using OdbcBench.Odbc;
using OdbcBench.Report;
using OdbcBench.Validation;

namespace OdbcBench.Bench;

public sealed class RunOptions
{
    public bool Strict { get; init; }
    public bool Validate { get; init; } = true;
    public bool Quiet { get; init; }
    public string ConfigPath { get; init; } = "";
    public string ConfigSha256 { get; init; } = "";
    public string CommandLine { get; init; } = "";
}

/// <summary>
/// Runs the three phases: connect every DSN, validate the first rows, then benchmark every (DSN, block size) series with
/// warmup and measured iterations. Never throws for DSN-level problems: they are recorded and the other DSNs continue.
/// </summary>
public sealed class BenchmarkRunner
{
    private const int MaxMessagesPerSeries = 50;

    private sealed class DsnState
    {
        public required DsnConfig Config { get; init; }
        public required DsnResult Result { get; init; }
        public required string Query { get; init; }
        public required string ConnectionString { get; init; }
        public OdbcConnection? Connection { get; set; }
        public bool ReconnectUsed { get; set; }
    }

    private sealed class SeriesState
    {
        public required DsnState Dsn { get; init; }
        public required int BlockSize { get; init; }
        public required SeriesResult Result { get; init; }
        public required BlockFetchOptions Options { get; init; }
        public BlockFetchReader? Reader { get; set; }
        public int ConsecutiveErrors { get; set; }
        public int Messages { get; set; }
        public bool Stopped { get; set; }
    }

    private readonly BenchConfig _config;
    private readonly RunOptions _options;
    private readonly TextWriter _log;
    private readonly CancellationToken _cancel;
    private readonly RunResult _run = new();
    private readonly List<DsnState> _dsns = new();
    private readonly List<SeriesState> _series = new();
    private OdbcEnvironment? _environment;

    public BenchmarkRunner(BenchConfig config, RunOptions options, TextWriter log, CancellationToken cancel)
    {
        _config = config;
        _options = options;
        _log = log;
        _cancel = cancel;
    }

    public RunResult Run()
    {
        _run.ToolVersion = ToolInfo.Version;
        _run.StartedUtc = DateTime.UtcNow;
        _run.Host = Environment.MachineName;
        _run.ProcessBitness = Environment.Is64BitProcess ? "64-bit" : "32-bit";
        _run.ConfigPath = _options.ConfigPath;
        _run.ConfigSha256 = _options.ConfigSha256;
        _run.CommandLine = _options.CommandLine;
        _run.Config = _config.Redacted();
        _run.Query = _config.ResolvedQuery;
        _run.Environment = SystemInfo.Collect();

        ProcessPriorityClass? previousPriority = null;
        try
        {
            _environment = new OdbcEnvironment(_config.OdbcVersionValue);
            _run.Environment.OdbcVersion = _environment.OdbcVersionText;
            foreach (var d in _environment.Info) Message("setup", "info", d.ToString());

            ConnectAll();
            var connected = _dsns.Where(d => d.Connection != null).ToList();
            if (connected.Count > 0 && !_cancel.IsCancellationRequested)
            {
                ResolveBaseline(connected);
                CreateSeries(connected);
                if (_config.WarmupIterations == 0 && !_options.Validate)
                    Message("setup", "warn", "no warmup and no validation dry run: the first measured iteration of each series includes describe and bind and any first-execution cost");

                if (_options.Validate) Validate(connected);

                if (_run.Validation?.StrictAbort != true && !_cancel.IsCancellationRequested)
                {
                    previousPriority = ApplyPriority();
                    Benchmark();
                    Calibrate();
                }
            }
        }
        catch (Exception ex) when (ex is OdbcException or DllNotFoundException or EntryPointNotFoundException)
        {
            _run.Fatal = true;
            _run.Outcome = $"failed: {ex.Message}";
            Message("setup", "error", ex.Message, sqlState: (ex as OdbcException)?.SqlState);
        }
        finally
        {
            RestorePriority(previousPriority);
            foreach (var s in _series)
            {
                if (s.Reader == null) continue;
                CaptureReaderInfo(s, s.Reader);
                s.Reader.Dispose();
                s.Reader = null;
            }
            foreach (var d in _dsns)
            {
                d.Connection?.Dispose();
                d.Connection = null;
            }
            _environment?.Dispose();
            _environment = null;
        }

        _run.Interrupted = _cancel.IsCancellationRequested;
        Analysis.Summarize(_run, _config.Iterations);
        _run.FinishedUtc = DateTime.UtcNow;
        return _run;
    }

    // ---------------------------------------------------------------- connect

    private void ConnectAll()
    {
        Phase("Connecting");
        foreach (var cfg in _config.EnabledDsns)
        {
            var result = new DsnResult
            {
                Name = cfg.Name,
                Source = cfg.Source,
                QueryOverridden = !string.IsNullOrWhiteSpace(cfg.Query),
                Query = string.IsNullOrWhiteSpace(cfg.Query) ? null : cfg.Query.Trim(),
            };
            var state = new DsnState { Config = cfg, Result = result, Query = _config.QueryFor(cfg), ConnectionString = cfg.BuildConnectionString() };
            _run.Dsns.Add(result);
            _dsns.Add(state);

            if (_cancel.IsCancellationRequested)
            {
                result.Status = "not run";
                continue;
            }

            try
            {
                long t0 = Stopwatch.GetTimestamp();
                state.Connection = OdbcConnection.Open(_environment!, state.ConnectionString, _config.LoginTimeoutSeconds, readDriverInfo: false);
                result.FirstConnectMs = Ms(t0, Stopwatch.GetTimestamp());
                result.Driver = state.Connection.ReadDriverInfo();
                result.ConnectInfo.AddRange(state.Connection.Info.Select(i => i.ToString()));
                foreach (var info in result.ConnectInfo) Message("connect", "info", info, cfg.Name);
                result.Status = "connected";
                var d = result.Driver;
                Log($"  {cfg.Name}: connected in {Fmt(result.FirstConnectMs)} ms; {d.DriverName} {d.DriverVersion} -> {d.DbmsName} {d.DbmsVersion}");
                if (d.UnicodeNative == false)
                    Message("connect", "warn", $"{d.DriverName} is not a Unicode driver: the Driver Manager converts every string between ANSI and UTF-16, and that cost is part of this DSN's timings", cfg.Name);
            }
            catch (OdbcException ex)
            {
                result.Status = "connect failed";
                result.Error = ex.Message;
                result.ErrorSqlState = ex.SqlState;
                result.Hint = OdbcRegistry.Hint(cfg.Dsn, ex.SqlState);
                Message("connect", "error", ex.Message + (result.Hint != null ? " " + result.Hint : ""), cfg.Name, sqlState: ex.SqlState);
                Log($"  {cfg.Name}: CONNECT FAILED {ex.Message}");
                if (result.Hint != null) Log($"  {cfg.Name}: hint: {result.Hint}");
                continue;
            }

            for (int i = 0; i < _config.ConnectSamples && !_cancel.IsCancellationRequested; i++)
            {
                try
                {
                    long t0 = Stopwatch.GetTimestamp();
                    using var extra = OdbcConnection.Open(_environment!, state.ConnectionString, _config.LoginTimeoutSeconds, readDriverInfo: false);
                    result.ConnectSamplesMs.Add(Ms(t0, Stopwatch.GetTimestamp()));
                    extra.Disconnect();
                }
                catch (OdbcException ex)
                {
                    Message("connect", "warn", $"connect sample {i + 1} failed: {ex.Message}", cfg.Name, sqlState: ex.SqlState);
                    break;
                }
            }
            result.Connect = SeriesStats.From(result.ConnectSamplesMs);
        }

        var dm = _dsns.Select(d => d.Result.Driver?.DriverManagerVersion).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        if (dm != null) _run.Environment.DriverManagerVersion = dm;
    }

    private void ResolveBaseline(List<DsnState> connected)
    {
        var wanted = _config.BaselineDsn();
        var baseline = connected.FirstOrDefault(d => ReferenceEquals(d.Config, wanted)) ?? connected[0];
        if (!ReferenceEquals(baseline.Config, wanted))
            Message("setup", "warn", $"baseline '{wanted.Name}' is not available; ratios use '{baseline.Config.Name}' instead");
        _run.BaselineDsn = baseline.Config.Name;
    }

    /// <summary>Series ordered by block size, then DSN, so the DSNs being compared at one block size run next to each other.</summary>
    private void CreateSeries(List<DsnState> connected)
    {
        foreach (int blockSize in _config.BlockSizes)
        {
            foreach (var d in connected)
            {
                var options = _config.FetchOptions(blockSize);
                var result = new SeriesResult
                {
                    DsnName = d.Config.Name,
                    BlockSize = blockSize,
                    Description = BlockFetchReader.Describe(options) + (_config.ConnectionPerIteration ? ", new connection every iteration" : ""),
                };
                var state = new SeriesState { Dsn = d, BlockSize = blockSize, Result = result, Options = options };
                if (!_config.ConnectionPerIteration) state.Reader = new BlockFetchReader(d.Connection!, d.Query, options);
                _series.Add(state);
                _run.Series.Add(result);
            }
        }
    }

    // ---------------------------------------------------------------- validate

    private void Validate(List<DsnState> connected)
    {
        Phase($"Validating (first {_config.Validation.Rows} rows)");
        var samples = new List<SampleResult>();
        foreach (var d in connected)
        {
            if (_cancel.IsCancellationRequested) return;
            var sample = SampleReader.Read(d.Connection!, d.Config.Name, d.Query, _config.Validation.Rows, _config.QueryTimeoutSeconds);
            samples.Add(sample);
            foreach (var info in sample.Info) Message("validation", "info", info, d.Config.Name);
            Log(sample.Ok
                ? $"  {d.Config.Name}: {sample.Rows.Count} rows x {sample.Columns.Count} columns"
                : $"  {d.Config.Name}: FAILED {sample.Error}");
        }

        var queries = connected.Select(d => d.Query).Distinct().ToList();
        var validation = ResultComparer.Compare(samples, queries, _config.Validation.Normalization);

        // Dry run of every series through the real block-fetch path: surfaces 07006 fallbacks, truncation and
        // row array size changes before any timing starts (and absorbs describe + bind).
        foreach (var s in _series)
        {
            if (_cancel.IsCancellationRequested) return;
            var sample = ExecuteOnce(s, -1, warmup: true, rowLimit: _config.Validation.Rows);
            if (s.Reader != null) CaptureReaderInfo(s, s.Reader);
            MergeSampleWarnings(s, sample);
            bool capped = s.Result.Columns.Any(c => c.Capped);
            if (!sample.Ok)
                validation.Issues.Add(new ValidationIssue(Severity.Fail, $"dry run {s.Result.Key} failed: {sample.ErrorMessage}"));
            else if (sample.Truncations > 0 && capped)
                validation.Issues.Add(new ValidationIssue(Severity.Info, string.Create(CultureInfo.InvariantCulture,
                    $"dry run {s.Result.Key}: {sample.Truncations} value(s) in the first row array were longer than the {_config.LongColumnCapBytes:N0}-byte cap and were truncated, as configured (longColumnMode = bindCapped)")));
            else if (sample.Truncations > 0)
                validation.Issues.Add(new ValidationIssue(Severity.Warn,
                    $"dry run {s.Result.Key}: {sample.Truncations} value(s) truncated in the first row array: the driver reports column sizes smaller than the data (raise longColumnThresholdBytes, or use bindMode wchar)"));
        }

        validation.StrictAbort = _options.Strict && validation.Status == "FAIL";
        _run.Validation = validation;
        Log($"  validation: {validation.Status} ({validation.FailCount} fail, {validation.WarnCount} warn)");
        if (validation.StrictAbort) Log("  strict mode: stopping before the benchmark");
    }

    // ---------------------------------------------------------------- benchmark

    private void Benchmark()
    {
        int warmup = _config.WarmupIterations;
        int measured = _config.Iterations;
        int total = (warmup + measured) * _series.Count;
        Phase($"Benchmarking: {_series.Count} series x ({warmup} warmup + {measured} measured) = {total} executions" +
              (_config.Interleave ? ", interleaved" : ", one series after another"));

        var progress = new Progress(total, Stopwatch.StartNew());
        var previousLatency = GCSettings.LatencyMode;
        try
        {
            if (_config.Interleave)
            {
                int round = 0;
                for (int w = 1; w <= warmup; w++, round++) RunRound(round, w, warmup: true, progress);
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                for (int k = 1; k <= measured; k++, round++) RunRound(round, k, warmup: false, progress);
            }
            else
            {
                foreach (var s in _series)
                {
                    GCSettings.LatencyMode = previousLatency;
                    for (int w = 1; w <= warmup; w++) RunOne(s, w, warmup: true, progress);
                    GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                    for (int k = 1; k <= measured; k++) RunOne(s, k, warmup: false, progress);
                }
            }
        }
        finally
        {
            GCSettings.LatencyMode = previousLatency;
        }
    }

    /// <summary>One execution of every series. Even rounds run forward, odd rounds backward (ABBA), so no DSN is always first.</summary>
    private void RunRound(int round, int index, bool warmup, Progress progress)
    {
        if (round % 2 == 0)
            for (int i = 0; i < _series.Count; i++) RunOne(_series[i], index, warmup, progress);
        else
            for (int i = _series.Count - 1; i >= 0; i--) RunOne(_series[i], index, warmup, progress);
    }

    private void RunOne(SeriesState s, int index, bool warmup, Progress progress)
    {
        if (_cancel.IsCancellationRequested) return;
        if (s.Stopped)
        {
            progress.Skip();
            return;
        }

        BetweenIterations();
        var sample = ExecuteOnce(s, index, warmup, rowLimit: null);
        s.Result.Samples.Add(sample);
        MergeSampleWarnings(s, sample);
        progress.Done(sample.TotalMs);

        string phase = warmup ? "warmup  " : "measured";
        int of = warmup ? _config.WarmupIterations : _config.Iterations;
        if (sample.Ok)
        {
            s.ConsecutiveErrors = 0;
            if (!_options.Quiet)
                _log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  [{progress.Count,5}/{progress.Total}] {phase} {index,3}/{of,-3} {s.Result.Key,-28} {Fmt(sample.TotalMs),12} ms {sample.Rows,14:N0} rows   ETA {progress.Eta}"));
            return;
        }

        s.ConsecutiveErrors++;
        if (!_options.Quiet)
            _log.WriteLine($"  [{progress.Count,5}/{progress.Total}] {phase} {index,3}/{of,-3} {s.Result.Key,-28} FAILED {sample.ErrorMessage}");
        if (s.Messages++ < MaxMessagesPerSeries)
            Message(warmup ? "warmup" : "benchmark", "error", sample.ErrorMessage ?? "iteration failed", s.Dsn.Config.Name, s.Result.Key, index, sample.ErrorSqlState);

        if (sample.ConnectionLost && !_config.ConnectionPerIteration && !s.Dsn.ReconnectUsed)
            Reconnect(s.Dsn);

        if (!s.Stopped && s.ConsecutiveErrors >= _config.MaxConsecutiveErrors)
        {
            s.Stopped = true;
            s.Result.Status = "abandoned";
            Message("benchmark", "error", $"series abandoned after {s.ConsecutiveErrors} consecutive failed iterations", s.Dsn.Config.Name, s.Result.Key);
            Log($"  {s.Result.Key}: abandoned after {s.ConsecutiveErrors} consecutive failures");
        }
    }

    /// <summary>
    /// Executes one iteration. CPU and GC are measured around execute-fetch-close only. In connection-per-iteration mode
    /// the connect is timed separately and the disconnect is not timed.
    /// </summary>
    private IterationSample ExecuteOnce(SeriesState s, int index, bool warmup, long? rowLimit)
    {
        if (!_config.ConnectionPerIteration)
            return Measure(s.Reader!, index, warmup, rowLimit);

        OdbcConnection connection;
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            connection = OdbcConnection.Open(_environment!, s.Dsn.ConnectionString, _config.LoginTimeoutSeconds, readDriverInfo: false);
        }
        catch (OdbcException ex)
        {
            return new IterationSample
            {
                Index = index, Warmup = warmup, StartedUtc = DateTime.UtcNow, Ok = false,
                ErrorSqlState = ex.SqlState, ErrorMessage = "connect failed: " + ex.Message,
                ConnectMs = Ms(t0, Stopwatch.GetTimestamp()),
            };
        }
        double connectMs = Ms(t0, Stopwatch.GetTimestamp());
        try
        {
            connection.UseDriverInfo(s.Dsn.Result.Driver);
            using var reader = new BlockFetchReader(connection, s.Dsn.Query, s.Options);
            var sample = Measure(reader, index, warmup, rowLimit);
            sample.ConnectMs = connectMs;
            CaptureReaderInfo(s, reader);
            return sample;
        }
        finally
        {
            connection.Dispose();
        }
    }

    private static IterationSample Measure(BlockFetchReader reader, int index, bool warmup, long? rowLimit)
    {
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        double cpu0 = ProcessCpu.NowMs();
        var sample = reader.Execute(index, warmup, rowLimit);
        sample.CpuMs = ProcessCpu.NowMs() - cpu0;
        sample.Gc0 = GC.CollectionCount(0) - gc0;
        sample.Gc1 = GC.CollectionCount(1) - gc1;
        sample.Gc2 = GC.CollectionCount(2) - gc2;
        return sample;
    }

    private void BetweenIterations()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();
        if (_config.PauseBetweenIterationsMs > 0) Thread.Sleep(_config.PauseBetweenIterationsMs);
    }

    private void Reconnect(DsnState d)
    {
        d.ReconnectUsed = true;
        d.Result.Reconnects++;
        var affected = _series.Where(x => ReferenceEquals(x.Dsn, d)).ToList();
        foreach (var s in affected)
        {
            if (s.Reader != null) CaptureReaderInfo(s, s.Reader);
            s.Reader?.Dispose();
            s.Reader = null;
        }
        d.Connection?.Dispose();
        d.Connection = null;

        try
        {
            d.Connection = OdbcConnection.Open(_environment!, d.ConnectionString, _config.LoginTimeoutSeconds, readDriverInfo: false);
            d.Connection.UseDriverInfo(d.Result.Driver);
            foreach (var s in affected) s.Reader = new BlockFetchReader(d.Connection, d.Query, s.Options);
            Message("benchmark", "warn", "connection lost; reconnected once (the next iteration of each series repeats describe and bind)", d.Config.Name);
            Log($"  {d.Config.Name}: connection lost, reconnected");
        }
        catch (OdbcException ex)
        {
            foreach (var s in affected)
            {
                s.Stopped = true;
                s.Result.Status = "abandoned";
            }
            Message("benchmark", "error", $"connection lost and reconnect failed: {ex.Message}", d.Config.Name, sqlState: ex.SqlState);
            Log($"  {d.Config.Name}: reconnect FAILED {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- calibration and bookkeeping

    private void Calibrate()
    {
        if (!_config.Calibrate || _config.ConnectionPerIteration || _cancel.IsCancellationRequested) return;
        foreach (var s in _series)
        {
            var c = s.Reader?.Calibrate();
            if (c == null || c.ValuesPerPass == 0) continue;
            s.Result.Harness = new HarnessCost
            {
                BoundColumns = c.BoundColumns,
                ValuesPerPass = c.ValuesPerPass,
                NsPerValue = c.MsPerPass * 1e6 / c.ValuesPerPass,
            };
        }
    }

    private static void CaptureReaderInfo(SeriesState s, IResultReader reader)
    {
        var r = s.Result;
        if (reader.Plan is { } plan && reader.Columns is { } columns)
        {
            r.Columns = columns.Select(c => new ColumnBinding
            {
                Ordinal = c.Ordinal,
                Name = c.Name,
                SqlType = c.SqlTypeName,
                Size = c.SizeText,
                TypeName = c.TypeName,
                Binding = plan.Unbound.Contains(c) ? $"SQLGetData as {c.CTypeName}" : c.BindingText,
                Long = plan.Unbound.Contains(c),
                Capped = c.Capped,
            }).ToList();
            r.BlockFetchDisabledBy = plan.BlockFetchDisabledBy;
            r.BoundBytes = plan.BoundBytes;
            r.EffectiveBlockSize = plan.ArraySize;
        }
        foreach (var w in reader.Warnings)
            if (!r.Warnings.Contains(w)) r.Warnings.Add(w);
    }

    private static void MergeSampleWarnings(SeriesState s, IterationSample sample)
    {
        if (sample.Warnings == null) return;
        foreach (var w in sample.Warnings)
            if (s.Result.Warnings.Count < 50 && !s.Result.Warnings.Contains(w)) s.Result.Warnings.Add(w);
    }

    private ProcessPriorityClass? ApplyPriority()
    {
        var wanted = _config.ProcessPriority.Trim().ToLowerInvariant() switch
        {
            "high" => ProcessPriorityClass.High,
            "abovenormal" => ProcessPriorityClass.AboveNormal,
            _ => (ProcessPriorityClass?)null,
        };
        if (wanted == null) return null;
        try
        {
            using var process = Process.GetCurrentProcess();
            var previous = process.PriorityClass;
            process.PriorityClass = wanted.Value;
            return previous;
        }
        catch (Exception ex)
        {
            Message("setup", "warn", $"could not raise the process priority: {ex.Message}");
            return null;
        }
    }

    private static void RestorePriority(ProcessPriorityClass? previous)
    {
        if (previous == null) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = previous.Value;
        }
        catch
        {
            // best effort
        }
    }

    private void Message(string phase, string severity, string message, string? dsn = null, string? series = null, int? iteration = null, string? sqlState = null)
        => _run.Messages.Add(new RunMessage(phase, severity, message, dsn, series, iteration, sqlState));

    private void Phase(string text)
    {
        if (!_options.Quiet) _log.WriteLine(text);
    }

    private void Log(string text)
    {
        if (!_options.Quiet) _log.WriteLine(text);
    }

    private static double Ms(long t0, long t1) => (t1 - t0) * 1000.0 / Stopwatch.Frequency;

    private static string Fmt(double ms) => ms.ToString(ms < 100 ? "N3" : "N1", CultureInfo.InvariantCulture);

    private sealed class Progress
    {
        private readonly Stopwatch _clock;
        public int Total { get; private set; }
        public int Count { get; private set; }

        public Progress(int total, Stopwatch clock)
        {
            Total = total;
            _clock = clock;
        }

        public void Done(double _) => Count++;

        public void Skip() => Total = Math.Max(Count, Total - 1);

        public string Eta
        {
            get
            {
                if (Count == 0) return "--:--:--";
                double remaining = _clock.Elapsed.TotalSeconds / Count * (Total - Count);
                return TimeSpan.FromSeconds(Math.Max(0, remaining)).ToString(@"hh\:mm\:ss");
            }
        }
    }
}
