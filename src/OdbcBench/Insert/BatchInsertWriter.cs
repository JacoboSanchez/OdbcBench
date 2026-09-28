using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using OdbcBench.Fetch;
using OdbcBench.Odbc;

namespace OdbcBench.Insert;

/// <summary>When the inserted rows are committed.</summary>
public enum TransactionMode
{
    /// <summary>Autocommit off, one SQLEndTran(SQL_COMMIT) at the end of the iteration.</summary>
    PerIteration,
    /// <summary>Autocommit off, SQLEndTran(SQL_COMMIT) after every SQLExecute.</summary>
    PerBatch,
    /// <summary>Autocommit on: the driver commits every SQLExecute by itself.</summary>
    Autocommit,
}

public sealed class InsertOptions
{
    /// <summary>Requested parameter array size (SQL_ATTR_PARAMSET_SIZE).</summary>
    public int BatchSize { get; init; } = 1000;
    /// <summary>Rows inserted per iteration.</summary>
    public long Rows { get; init; } = 100_000;
    public BindMode BindMode { get; init; } = BindMode.Native;
    /// <summary>Keep one HSTMT per series (prepare + bind once). False rebuilds the statement every iteration (ORM-style cost).</summary>
    public bool ReuseStatement { get; init; } = true;
    public TransactionMode Transaction { get; init; } = TransactionMode.PerIteration;
    /// <summary>Count the rows of the table before and after every iteration (never timed).</summary>
    public bool Verify { get; init; } = true;
    public long MaxBoundBytes { get; init; } = 256L * 1024 * 1024;
    public int QueryTimeoutSeconds { get; init; }
}

/// <summary>
/// The batch insert access path: SQLPrepareW, SQLBindParameter with column-wise arrays, SQL_ATTR_PARAMSET_SIZE = N,
/// one SQLExecute per array and SQLEndTran(SQL_COMMIT). Only those ODBC calls are timed: generating the values, emptying
/// the table and counting its rows happen outside the timers.
/// Each column keeps its own FNV-1a state fed in row order, so the combined checksum does not depend on the batch
/// size: the same values in the same order always give the same checksum.
/// </summary>
public sealed unsafe class BatchInsertWriter : IResultReader
{
    private struct Slot
    {
        public InsertColumn Column;
        public byte* Data;
        public nint* Indicators;
        public int ElementBytes;
        public ulong Hash;
    }

    private readonly OdbcConnection _connection;
    private readonly InsertTarget _target;
    private readonly InsertOptions _options;
    private readonly List<string> _warnings = new();

    private OdbcStatement? _statement;
    private OdbcStatement? _helper;   // cleanup and row counts: never timed
    private GetDataReader? _getData;
    private bool _bound;
    private int _driverArraySize;     // what the driver accepted for the requested batch size
    private int _currentArraySize;    // what is set on the statement right now
    private List<ColumnInfo>? _columns;
    private BindingPlan? _plan;
    private ColumnBuffer[] _buffers = Array.Empty<ColumnBuffer>();
    private Slot[] _slots = Array.Empty<Slot>();
    private nuint* _processed;
    private ushort* _status;
    private int _statusCapacity;
    private bool _processedPtrSet;
    private bool _statusPtrSet;
    private bool _ready;
    private bool _manualCommit;
    private long? _rowsBefore;

    public BatchInsertWriter(OdbcConnection connection, InsertTarget target, InsertOptions options)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.BatchSize < 1) throw new ArgumentOutOfRangeException(nameof(options), "batchSize must be at least 1");
        if (options.Rows < 1) throw new ArgumentOutOfRangeException(nameof(options), "rows must be at least 1");
        _processed = (nuint*)NativeMemory.AllocZeroed((nuint)sizeof(nuint));
    }

    public static string Describe(InsertOptions o) =>
        $"SQLPrepareW + SQLBindParameter (column-wise arrays) + SQLExecute, requested parameter array size {o.BatchSize}, " +
        $"{(o.BindMode == BindMode.WChar ? "every parameter as SQL_C_WCHAR" : "native C types")}, " +
        (o.ReuseStatement ? "statement reused across iterations, " : "new statement every iteration, ") +
        o.Transaction switch
        {
            TransactionMode.PerBatch => "commit after every SQLExecute",
            TransactionMode.Autocommit => "autocommit",
            _ => "one commit per iteration",
        };

    public string Description => Describe(_options);
    public IReadOnlyList<ColumnInfo>? Columns => _columns;
    public BindingPlan? Plan => _plan;
    public IReadOnlyList<string> Warnings => _warnings;
    public int CurrentArraySize => _currentArraySize;
    public InsertTarget Target => _target;
    /// <summary>True when this connection ends transactions with SQLEndTran; false when the driver commits by itself.</summary>
    public bool ManualCommit => _manualCommit;

    /// <summary>Describes the table on first use, empties it as configured and counts its rows. Nothing here is timed.</summary>
    public IterationSample? Prepare(int index, bool warmup)
    {
        _rowsBefore = null;
        try
        {
            Setup();
            if (_target.CleanupSql != null) RunHelper(_target.CleanupSql);
            if (_options.Verify) _rowsBefore = CountRows();
            return null;
        }
        catch (OdbcException ex)
        {
            var sample = new IterationSample { Index = index, Warmup = warmup, StartedUtc = DateTime.UtcNow };
            MarkFailed(sample, ex.SqlState, "before the iteration: " + ex.Message, ex.IsConnectionFailure);
            return sample;
        }
        catch (Exception ex)
        {
            var sample = new IterationSample { Index = index, Warmup = warmup, StartedUtc = DateTime.UtcNow };
            MarkFailed(sample, null, "before the iteration: " + ex.Message, false);
            return sample;
        }
    }

    public IterationSample Execute(int index, bool warmup, long? rowLimit = null)
    {
        var sample = new IterationSample { Index = index, Warmup = warmup, StartedUtc = DateTime.UtcNow };
        try
        {
            Setup();
            var statement = EnsureStatement();
            if (!_bound) PrepareAndBind(statement, sample);

            int arraySize = _plan!.ArraySize;
            var slots = _slots;
            for (int c = 0; c < slots.Length; c++) slots[c].Hash = Fnv1a64.Offset;
            bool commitPerBatch = _manualCommit && _options.Transaction == TransactionMode.PerBatch;
            // Diagnostic records are only read on dry runs and the first warmup: draining them allocates and costs
            // one SQLGetDiagRecW call per record (rejected rows are counted from the parameter status array instead).
            bool captureInfo = rowLimit.HasValue || (warmup && index <= 1);
            int infoDrains = 0;
            long wanted = Math.Min(_options.Rows, rowLimit ?? long.MaxValue);
            long sent = 0, accepted = 0, batches = 0, rowErrors = 0, unprocessed = 0, bytes = 0;
            long executeTicks = 0, commitTicks = 0, generateTicks = 0, firstTicks = 0;

            long allocated0 = GC.GetAllocatedBytesForCurrentThread();
            while (sent < wanted)
            {
                int count = (int)Math.Min(arraySize, wanted - sent);

                long g0 = Stopwatch.GetTimestamp();
                bytes += Fill(slots, sent + 1, count);
                *_processed = 0;
                if (_statusPtrSet) new Span<ushort>(_status, count).Fill(Native.SQL_PARAM_UNUSED);
                long t0 = Stopwatch.GetTimestamp();
                generateTicks += t0 - g0;

                // The last array of an iteration is usually shorter: the size change is part of what an application pays.
                if (count != _currentArraySize) SetArraySize(statement, count);
                short rc = statement.Execute();
                long t1 = Stopwatch.GetTimestamp();
                executeTicks += t1 - t0;
                if (batches == 0) firstTicks = t1 - t0;

                if (rc == Native.SQL_ERROR || rc == Native.SQL_INVALID_HANDLE)
                    throw new OdbcException("SQLExecute", rc, statement.DrainDiagnostics());
                if (rc == Native.SQL_NEED_DATA)
                    throw new InvalidOperationException("SQLExecute returned SQL_NEED_DATA: the driver asks for data-at-execution parameters, which were not bound");

                // A driver that leaves the counter at zero is taken to have processed the whole array; the row count
                // of the table settles it afterwards.
                long processed = _processedPtrSet && *_processed > 0 ? (long)Math.Min(*_processed, (nuint)count) : count;
                if (rc == Native.SQL_NO_DATA) processed = 0; // the statement affected no rows
                long errors = 0;
                if (_statusPtrSet)
                    for (int i = 0; i < processed; i++)
                        if (_status[i] == Native.SQL_PARAM_ERROR) errors++;

                if (rc == Native.SQL_SUCCESS_WITH_INFO && captureInfo && infoDrains < 3)
                {
                    infoDrains++;
                    foreach (var d in statement.DrainDiagnostics(10)) sample.AddWarning(d.ToString());
                }

                // SQLRowCount is not used: drivers without SQL_PARC_BATCH report only the last parameter set, so
                // the row count of the table is what settles how many rows arrived.
                if (commitPerBatch) commitTicks += Commit();

                sent += count;
                accepted += processed - errors;
                rowErrors += errors;
                unprocessed += count - processed;
                batches++;
            }
            if (_manualCommit && !commitPerBatch) commitTicks += Commit();
            sample.AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated0;

            ulong checksum = Fnv1a64.Offset;
            for (int c = 0; c < slots.Length; c++) checksum = Fnv1a64.Combine(checksum, slots[c].Hash);

            sample.ExecuteMs = Ms(executeTicks);
            sample.FirstBatchMs = Ms(firstTicks);
            sample.CommitMs = Ms(commitTicks);
            sample.GenerateMs = Ms(generateTicks);
            sample.TotalMs = sample.ExecuteMs + sample.DescribeMs + sample.CommitMs;
            sample.Rows = accepted;
            sample.Batches = batches;
            sample.Bytes = bytes;
            sample.RowErrors = rowErrors;
            sample.ChecksumValue = checksum;
            sample.Checksum = checksum.ToString("x16");
            sample.EffectiveBlockSize = arraySize;
            if (rowErrors > 0)
                sample.AddWarning(string.Create(CultureInfo.InvariantCulture, $"the driver rejected {rowErrors:N0} of {sent:N0} rows (SQL_PARAM_ERROR in the parameter status array)"));
            if (unprocessed > 0)
                sample.AddWarning(string.Create(CultureInfo.InvariantCulture, $"the driver left {unprocessed:N0} of {sent:N0} rows unprocessed (SQL_ATTR_PARAMS_PROCESSED_PTR)"));

            FinishStatement();
            return sample;
        }
        catch (OdbcException ex)
        {
            string hint = ex.SqlState is { } state && state.StartsWith("23", StringComparison.Ordinal) && _target.CleanupSql == null
                ? " (every iteration inserts the same rows: set insert.cleanup to 'delete' or 'truncate', or use a table without unique constraints)"
                : "";
            MarkFailed(sample, ex.SqlState, ex.Message + hint, ex.IsConnectionFailure);
            return sample;
        }
        catch (Exception ex)
        {
            MarkFailed(sample, null, ex.Message, false);
            return sample;
        }
    }

    /// <summary>Describes the table, prepares the INSERT and binds the parameter arrays without executing it (probe).</summary>
    public IterationSample Bind()
    {
        var sample = new IterationSample { Index = 0, Warmup = true, StartedUtc = DateTime.UtcNow };
        try
        {
            Setup();
            var statement = EnsureStatement();
            if (!_bound) PrepareAndBind(statement, sample);
            sample.EffectiveBlockSize = _plan!.ArraySize;
            FinishStatement();
        }
        catch (OdbcException ex)
        {
            MarkFailed(sample, ex.SqlState, ex.Message, ex.IsConnectionFailure);
        }
        catch (Exception ex)
        {
            MarkFailed(sample, null, ex.Message, false);
        }
        return sample;
    }

    /// <summary>Counts the rows the table gained during the iteration. A difference is a warning here and a finding in the report.</summary>
    public void Verify(IterationSample sample)
    {
        if (!sample.Ok || _rowsBefore is not long before) return;
        _rowsBefore = null;
        try
        {
            sample.VerifiedRows = CountRows() - before;
            if (sample.VerifiedRows != sample.Rows)
                sample.AddWarning(string.Create(CultureInfo.InvariantCulture,
                    $"the driver accepted {sample.Rows:N0} rows but the table gained {sample.VerifiedRows:N0} ({_target.CountSql})"));
        }
        catch (Exception ex)
        {
            _connection.TryRollback();
            sample.AddWarning($"the rows of the table could not be counted after the iteration: {ex.Message}");
        }
    }

    /// <summary>No value-reading loop to replay: generating the values is timed on its own in every iteration.</summary>
    public CalibrationSample? Calibrate(double minMs = 25) => null;

    /// <summary>
    /// Fills the first <paramref name="count"/> elements of every parameter array with the rows starting at
    /// <paramref name="firstRow"/>, column by column (the arrays are column-wise). Returns the data bytes written.
    /// </summary>
    private static long Fill(Slot[] slots, long firstRow, int count)
    {
        long bytes = 0;
        for (int c = 0; c < slots.Length; c++)
        {
            ref Slot s = ref slots[c];
            var column = s.Column;
            byte* data = s.Data;
            nint* indicators = s.Indicators;
            int stride = s.ElementBytes;
            ulong h = s.Hash;
            for (int i = 0; i < count; i++)
            {
                byte* value = data + (long)i * stride;
                int n = DataGenerator.Write(column, firstRow + i, value);
                indicators[i] = n;
                h = Fnv1a64.Add(h, value, n);
                bytes += n;
            }
            s.Hash = h;
        }
        return bytes;
    }

    /// <summary>Timed SQLEndTran(SQL_COMMIT); returns the ticks it took.</summary>
    private long Commit()
    {
        long t0 = Stopwatch.GetTimestamp();
        short rc = _connection.EndTransaction(Native.SQL_COMMIT);
        long ticks = Stopwatch.GetTimestamp() - t0;
        Diag.Check(rc, "SQLEndTran(SQL_COMMIT)", Native.SQL_HANDLE_DBC, _connection.Handle);
        return ticks;
    }

    // ---- setup and helper statements (never timed)

    private void Setup()
    {
        _target.Resolve(_connection);
        if (_ready) return;
        _ready = true;
        foreach (var note in _target.Notes) AddWarningOnce(note);
        if (_options.Transaction == TransactionMode.Autocommit) return;
        _manualCommit = _connection.TrySetManualCommit();
        if (!_manualCommit)
            AddWarningOnce($"autocommit could not be turned off ({_connection.ManualCommitRefusal}): the driver commits every SQLExecute by itself and no SQLEndTran is sent");
    }

    private OdbcStatement Helper() => _helper ??= new OdbcStatement(_connection);

    private void RunHelper(string sql)
    {
        var statement = Helper();
        try
        {
            statement.ExecDirect(sql); // SQL_NO_DATA: nothing to delete
        }
        finally
        {
            statement.TryCloseCursor();
            statement.Info.Clear();
        }
        if (_manualCommit) _connection.Commit();
    }

    private long CountRows()
    {
        var statement = Helper();
        try
        {
            if (statement.ExecDirect(_target.CountSql) == Native.SQL_NO_DATA)
                throw new InvalidOperationException($"'{_target.CountSql}' produced no result set");
            short rc = statement.Fetch();
            if (rc == Native.SQL_ERROR || rc == Native.SQL_INVALID_HANDLE)
                throw new OdbcException("SQLFetch", rc, statement.DrainDiagnostics());
            if (rc == Native.SQL_NO_DATA)
                throw new InvalidOperationException($"'{_target.CountSql}' returned no row");
            _getData ??= new GetDataReader(1024);
            string? text = _getData.ReadText(statement, 1);
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal rows))
                throw new InvalidOperationException($"'{_target.CountSql}' returned '{text}', which is not a number");
            return (long)rows;
        }
        finally
        {
            statement.TryCloseCursor();
            statement.Info.Clear();
            _connection.TryRollback(); // nothing was changed: only ends the transaction the SELECT opened
        }
    }

    // ---- statement lifecycle

    private OdbcStatement EnsureStatement()
    {
        if (_statement != null && _options.ReuseStatement) return _statement;

        _statement?.Dispose();
        _statement = null;
        _bound = false;

        var statement = new OdbcStatement(_connection);
        try
        {
            TrySetAttribute(statement, Native.SQL_ATTR_QUERY_TIMEOUT, (nuint)Math.Max(0, _options.QueryTimeoutSeconds), "SQL_ATTR_QUERY_TIMEOUT");
            _driverArraySize = ApplyArraySize(statement, _options.BatchSize);
            // A driver without parameter arrays is left alone: some accept the array attributes and then fail SQLExecute.
            if (_driverArraySize > 1)
                TrySetAttribute(statement, Native.SQL_ATTR_PARAM_BIND_TYPE, Native.SQL_PARAM_BIND_BY_COLUMN, "SQL_ATTR_PARAM_BIND_TYPE");
        }
        catch
        {
            statement.Dispose();
            throw;
        }
        _statement = statement;
        return statement;
    }

    /// <summary>After a completed iteration: keep the statement (default) or drop it so the next iteration rebuilds everything.</summary>
    private void FinishStatement()
    {
        if (_options.ReuseStatement) return;
        _statement?.Dispose();
        _statement = null;
        _bound = false;
    }

    private void TrySetAttribute(OdbcStatement statement, int attribute, nuint value, string name)
    {
        try
        {
            if (!statement.SetUIntAttribute(attribute, value, name))
                AddWarningOnce($"{name}={value} was changed by the driver (01S02)");
        }
        catch (OdbcException ex)
        {
            AddWarningOnce($"{name}={value} not accepted by the driver: [{ex.SqlState}] {FirstMessage(ex)}");
        }
    }

    private bool TrySetPointer(OdbcStatement statement, int attribute, void* pointer, string name)
    {
        try
        {
            statement.SetPointerAttribute(attribute, pointer, name);
            return true;
        }
        catch (OdbcException ex)
        {
            AddWarningOnce($"{name} not accepted by the driver: [{ex.SqlState}] {FirstMessage(ex)}");
            return false;
        }
    }

    /// <summary>
    /// Sets SQL_ATTR_PARAMSET_SIZE and reads it back: the effective value is what the driver says, not what we asked.
    /// A driver without parameter arrays gets one row per SQLExecute.
    /// </summary>
    private int ApplyArraySize(OdbcStatement statement, int size)
    {
        int effective;
        try
        {
            bool exact = statement.SetUIntAttribute(Native.SQL_ATTR_PARAMSET_SIZE, (nuint)size, "SQL_ATTR_PARAMSET_SIZE");
            nuint actual = statement.GetULenAttribute(Native.SQL_ATTR_PARAMSET_SIZE, "SQL_ATTR_PARAMSET_SIZE");
            effective = actual == 0 || actual > int.MaxValue ? size : (int)actual;
            if (!exact || effective != size)
                AddWarningOnce($"driver changed SQL_ATTR_PARAMSET_SIZE from {size} to {effective}");
        }
        catch (OdbcException ex) when (size > 1 && !ex.IsConnectionFailure)
        {
            AddWarningOnce($"SQL_ATTR_PARAMSET_SIZE={size} not accepted by the driver: [{ex.SqlState}] {FirstMessage(ex)}; every SQLExecute sends one row");
            effective = 1;
        }
        _currentArraySize = effective;
        return effective;
    }

    /// <summary>Changes the array size between executions (the shorter last array and back). Raw: the caller is being timed.</summary>
    private void SetArraySize(OdbcStatement statement, int size)
    {
        short rc = Native.SQLSetStmtAttrW(statement.Handle, Native.SQL_ATTR_PARAMSET_SIZE, size, Native.SQL_IS_UINTEGER);
        if (rc != Native.SQL_SUCCESS)
            throw new OdbcException($"SQLSetStmtAttrW(SQL_ATTR_PARAMSET_SIZE={size})", rc, statement.DrainDiagnostics());
        _currentArraySize = size;
    }

    /// <summary>
    /// Binds every parameter array, then prepares the INSERT. Binding first is valid ODBC and fails cleanly on a driver
    /// without SQLBindParameter; some such drivers crash inside SQLPrepareW on DML instead of returning an error.
    /// </summary>
    private void PrepareAndBind(OdbcStatement statement, IterationSample sample)
    {
        long t0 = Stopwatch.GetTimestamp();

        var columns = _target.Columns;
        var fallbacks = new List<string>();
        for (int attempt = 0; ; attempt++)
        {
            var plan = BuildPlan(columns);
            if (plan.ArraySize != _currentArraySize)
            {
                int effective = ApplyArraySize(statement, plan.ArraySize);
                if (effective != plan.ArraySize)
                {
                    AddWarningOnce($"driver refused parameter array size {plan.ArraySize} after the memory limit lowered it; using {effective}");
                    plan.ArraySize = Math.Min(effective, Math.Max(1, plan.ArraySize));
                }
            }

            EnsureBuffers(plan);
            // With one row per SQLExecute the return code says everything the two arrays would.
            bool arrays = plan.ArraySize > 1;
            _processedPtrSet = arrays && TrySetPointer(statement, Native.SQL_ATTR_PARAMS_PROCESSED_PTR, _processed, "SQL_ATTR_PARAMS_PROCESSED_PTR");
            _statusPtrSet = arrays && TrySetPointer(statement, Native.SQL_ATTR_PARAM_STATUS_PTR, _status, "SQL_ATTR_PARAM_STATUS_PTR");

            try
            {
                for (int i = 0; i < _buffers.Length; i++)
                {
                    var b = _buffers[i];
                    var c = b.Column;
                    statement.BindParameter(i + 1, c.CType, c.SqlType, c.ColumnSize, c.DecimalDigits, b.Data, b.ElementBytes, b.Indicators);
                }
            }
            catch (OdbcException ex) when (ex.SqlState is "07006" or "HY003" or "HY004" or "HYC00" && attempt < columns.Count)
            {
                int parameter = ParseOrdinal(ex.Function);
                var column = parameter >= 1 && parameter <= columns.Count ? columns[parameter - 1] : null;
                if (column == null) throw;
                string rejected = column.Column.CTypeName;
                if (!ParameterMapper.ForceText(column)) throw;
                // Reported only once the text binding is accepted: a driver without SQLBindParameter rejects that too.
                fallbacks.Add($"column '{column.Column.Name}' ({column.Column.SqlTypeName}): driver rejected {rejected} ({ex.SqlState}); bound as SQL_C_WCHAR instead");
                statement.ResetParameters();
                continue;
            }
            statement.Prepare(_target.InsertSql);

            _plan = plan;
            _columns = plan.Columns.ToList();
            _slots = BuildSlots(columns);
            foreach (var w in fallbacks) AddWarningOnce(w);
            foreach (var w in plan.Warnings) AddWarningOnce(w);
            _bound = true;
            break;
        }

        sample.DescribeMs = Ms(Stopwatch.GetTimestamp() - t0);
    }

    /// <summary>Every column is bound; the array size is what the driver accepted, lowered to fit the memory limit.</summary>
    private BindingPlan BuildPlan(IReadOnlyList<InsertColumn> columns)
    {
        var bound = columns.Select(c => c.Column).ToList();
        var warnings = new List<string>();
        int arraySize = Math.Max(1, _driverArraySize);

        long rowBytes = 0;
        foreach (var c in bound) rowBytes += c.ElementBytes + IntPtr.Size;
        if ((long)arraySize * rowBytes > _options.MaxBoundBytes)
        {
            int lowered = (int)Math.Max(1, Math.Min(int.MaxValue, _options.MaxBoundBytes / rowBytes));
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"parameter array size lowered from {arraySize} to {lowered}: one row needs {rowBytes:N0} bytes of bound buffers and maxBoundBytes is {_options.MaxBoundBytes:N0}"));
            arraySize = lowered;
        }

        return new BindingPlan
        {
            RequestedBlockSize = _options.BatchSize,
            DriverBlockSize = _driverArraySize,
            ArraySize = arraySize,
            Columns = bound,
            Bound = bound,
            Warnings = warnings,
            RowBytes = rowBytes,
        };
    }

    private Slot[] BuildSlots(IReadOnlyList<InsertColumn> columns)
    {
        var slots = new Slot[columns.Count];
        for (int i = 0; i < slots.Length; i++)
        {
            var buffer = _buffers[i];
            slots[i] = new Slot { Column = columns[i], Data = buffer.Data, Indicators = buffer.Indicators, ElementBytes = buffer.ElementBytes };
        }
        return slots;
    }

    private void EnsureBuffers(BindingPlan plan)
    {
        int rows = plan.ArraySize;
        bool reusable = _buffers.Length == plan.Bound.Count;
        if (reusable)
            for (int i = 0; i < _buffers.Length && reusable; i++)
                reusable = _buffers[i].Matches(plan.Bound[i], rows);

        if (!reusable)
        {
            foreach (var b in _buffers) b.Dispose();
            _buffers = Array.Empty<ColumnBuffer>();
            var buffers = new ColumnBuffer[plan.Bound.Count];
            for (int i = 0; i < buffers.Length; i++) buffers[i] = new ColumnBuffer(plan.Bound[i], rows);
            _buffers = buffers;
        }

        if (_status == null || _statusCapacity < rows)
        {
            if (_status != null) NativeMemory.Free(_status);
            _statusCapacity = Math.Max(rows, 1);
            _status = (ushort*)NativeMemory.AllocZeroed((nuint)(sizeof(ushort) * _statusCapacity));
        }
    }

    private void MarkFailed(IterationSample sample, string? sqlState, string message, bool connectionLost)
    {
        sample.Ok = false;
        sample.ErrorSqlState = sqlState;
        sample.ErrorMessage = message;
        sample.ConnectionLost = connectionLost;
        sample.EffectiveBlockSize = _currentArraySize;
        _connection.TryRollback(); // rows of the failed iteration do not stay behind
        if (!_options.ReuseStatement)
        {
            _statement?.Dispose();
            _statement = null;
            _bound = false;
        }
    }

    private void AddWarningOnce(string message)
    {
        if (!_warnings.Contains(message)) _warnings.Add(message);
    }

    private static string FirstMessage(OdbcException ex) => ex.Diagnostics.Count > 0 ? ex.Diagnostics[0].Message : ex.Message;

    private static int ParseOrdinal(string function)
    {
        // Function names look like "SQLBindParameter(parameter 7)".
        int start = function.IndexOf("parameter ", StringComparison.Ordinal);
        if (start < 0) return -1;
        start += "parameter ".Length;
        int end = start;
        while (end < function.Length && char.IsDigit(function[end])) end++;
        return int.TryParse(function.AsSpan(start, end - start), out int ordinal) ? ordinal : -1;
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        _statement?.Dispose(); // SQLFreeHandle(STMT) first: the driver must not hold our buffers any more
        _statement = null;
        _helper?.Dispose();
        _helper = null;
        foreach (var b in _buffers) b.Dispose();
        _buffers = Array.Empty<ColumnBuffer>();
        _slots = Array.Empty<Slot>();
        _getData?.Dispose();
        _getData = null;
        if (_processed != null)
        {
            NativeMemory.Free(_processed);
            _processed = null;
        }
        if (_status != null)
        {
            NativeMemory.Free(_status);
            _status = null;
        }
    }
}
