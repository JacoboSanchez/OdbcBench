using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OdbcBench.Odbc;

namespace OdbcBench.Fetch;

public sealed class BlockFetchOptions
{
    public int BlockSize { get; init; } = 1000;
    public BindMode BindMode { get; init; } = BindMode.Native;
    /// <summary>Keep one HSTMT per series (describe + bind once). False rebuilds the statement every iteration (ORM-style cost).</summary>
    public bool ReuseStatement { get; init; } = true;
    public LongColumnMode LongColumnMode { get; init; } = LongColumnMode.RowByRow;
    public long LongColumnThresholdBytes { get; init; } = 8000;
    public int LongColumnCapBytes { get; init; } = 65536;
    public long MaxBoundBytes { get; init; } = 256L * 1024 * 1024;
    public int QueryTimeoutSeconds { get; init; }
    public int GetDataChunkBytes { get; init; } = 32768;
    /// <summary>Append a unique comment per iteration to defeat result caches (also defeats plan-cache reuse).</summary>
    public bool CacheBuster { get; init; }
    /// <summary>Simulated client work: after every row array, spin for this many microseconds per fetched row.</summary>
    public double RowProcessingMicros { get; init; }
}

/// <summary>
/// The block-fetch access path: SQLExecDirectW, SQLBindCol with column-wise arrays, SQL_ATTR_ROW_ARRAY_SIZE = N,
/// SQLFetchScroll(SQL_FETCH_NEXT) until SQL_NO_DATA, every value touched through raw pointers, SQLFreeStmt(SQL_CLOSE).
/// Long columns fall back to chunked SQLGetData according to <see cref="LongColumnMode"/>.
/// Each column keeps its own FNV-1a state fed in row order, so the combined checksum does not depend on the block size
/// or on which columns ended up bound: the same values in the same order always give the same checksum.
/// </summary>
public sealed unsafe class BlockFetchReader : IResultReader
{
    private struct Slot
    {
        public byte* Data;
        public nint* Indicators;
        public int ElementBytes;
        public int MaxDataBytes;
        public bool Fixed;
        public ulong Hash;
        public ColumnInfo? Unbound; // non-null: read with SQLGetData instead of from the buffer
    }

    private struct Counters
    {
        public long Bytes;
        public long Nulls;
        public long Truncations;
        public bool Approx;
    }

    private readonly OdbcConnection _connection;
    private readonly string _query;
    private readonly BlockFetchOptions _options;
    private readonly List<string> _warnings = new();

    private OdbcStatement? _statement;
    private bool _bound;
    private int _driverArraySize;     // what the driver accepted for the requested block size
    private int _currentArraySize;    // what is set on the statement right now
    private List<ColumnInfo>? _columns;
    private BindingPlan? _plan;
    private ColumnBuffer[] _buffers = Array.Empty<ColumnBuffer>();
    private Slot[] _slots = Array.Empty<Slot>();
    private GetDataReader? _getData;
    private nuint* _rowsFetched;
    private ushort* _rowStatus;
    private int _rowStatusCapacity;
    private bool _rowsFetchedPtrSet;
    private bool _rowStatusPtrSet;
    private bool _rowsFetchedUnreliableWarned;
    private long _maxBatchRows;

    public BlockFetchReader(OdbcConnection connection, string query, BlockFetchOptions options)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.BlockSize < 1) throw new ArgumentOutOfRangeException(nameof(options), "blockSize must be at least 1");
        _rowsFetched = (nuint*)NativeMemory.AllocZeroed((nuint)sizeof(nuint));
    }

    public static string Describe(BlockFetchOptions o) =>
        $"SQLExecDirectW + SQLBindCol (column-wise arrays) + SQLFetchScroll, requested row array size {o.BlockSize}, " +
        $"{(o.BindMode == BindMode.WChar ? "every column as SQL_C_WCHAR" : "native C types")}, " +
        (o.ReuseStatement ? "statement reused across iterations" : "new statement every iteration");

    public string Description => Describe(_options);
    public IReadOnlyList<ColumnInfo>? Columns => _columns;
    public BindingPlan? Plan => _plan;
    public IReadOnlyList<string> Warnings => _warnings;
    public int CurrentArraySize => _currentArraySize;

    public IterationSample Execute(int index, bool warmup, long? rowLimit = null)
    {
        var sample = new IterationSample { Index = index, Warmup = warmup, StartedUtc = DateTime.UtcNow };
        string sql = _options.CacheBuster ? $"{_query} /* odbcbench {Guid.NewGuid():N} */" : _query;
        try
        {
            var statement = EnsureStatement();

            long t0 = Stopwatch.GetTimestamp();
            short rc = statement.ExecDirect(sql);
            long t1 = Stopwatch.GetTimestamp();
            sample.ExecuteMs = Ms(t0, t1);

            if (rc == Native.SQL_NO_DATA)
            {
                sample.AddWarning("SQLExecDirectW returned SQL_NO_DATA: the statement produced no result set");
                statement.CloseCursor();
                sample.CloseMs = Ms(t1, Stopwatch.GetTimestamp());
                sample.TotalMs = sample.ExecuteMs + sample.CloseMs;
                sample.EffectiveBlockSize = _currentArraySize;
                sample.ChecksumValue = Fnv1a64.Offset;
                sample.Checksum = sample.ChecksumValue.ToString("x16");
                FinishStatement();
                return sample;
            }

            if (!_bound)
            {
                DescribeAndBind(statement, sample);
            }
            else
            {
                short n = statement.NumResultCols();
                if (n != _columns!.Count)
                    throw new InvalidOperationException($"result set shape changed between iterations: {n} columns now, {_columns.Count} when bound");
            }

            int arraySize = _plan!.ArraySize;
            var slots = _slots;
            for (int c = 0; c < slots.Length; c++) slots[c].Hash = Fnv1a64.Offset;
            bool hasUnbound = _plan.Unbound.Count > 0;
            var getData = _getData;
            var counters = new Counters();
            long rows = 0, batches = 0, rowErrors = 0;
            // Diagnostic records are only read on dry runs and the first warmup: draining them allocates and costs
            // one SQLGetDiagRecW call per record, which would distort measured iterations (truncation and row
            // errors are counted from the indicators and the row status array instead).
            bool captureInfo = rowLimit.HasValue || (warmup && index <= 1);
            int infoDrains = 0;
            bool first = true;
            double processingTicksPerRow = _options.RowProcessingMicros * Stopwatch.Frequency / 1_000_000.0;
            long processingTicks = 0;

            long allocated0 = GC.GetAllocatedBytesForCurrentThread();
            long tFetch0 = Stopwatch.GetTimestamp();
            while (true)
            {
                *_rowsFetched = 0;
                if (_rowStatusPtrSet) new Span<ushort>(_rowStatus, arraySize).Fill(Native.SQL_ROW_NOROW);

                rc = statement.FetchScroll();
                if (first)
                {
                    sample.FirstBatchMs = Ms(tFetch0, Stopwatch.GetTimestamp());
                    first = false;
                }
                if (rc == Native.SQL_NO_DATA) break;
                if (rc == Native.SQL_ERROR || rc == Native.SQL_INVALID_HANDLE)
                    throw new OdbcException("SQLFetchScroll", rc, statement.DrainDiagnostics());

                long fetched = _rowsFetchedPtrSet ? (long)*_rowsFetched : 0;
                if (fetched > arraySize) fetched = arraySize;
                if (fetched <= 0)
                {
                    fetched = arraySize == 1 ? 1 : CountRowsFromStatus(arraySize);
                    if (_rowsFetchedPtrSet && !_rowsFetchedUnreliableWarned)
                    {
                        _rowsFetchedUnreliableWarned = true;
                        AddWarningOnce("driver did not update SQL_ATTR_ROWS_FETCHED_PTR; row counts were derived from the row status array");
                    }
                }
                batches++;

                if (rc == Native.SQL_SUCCESS_WITH_INFO)
                {
                    if (_rowStatusPtrSet)
                        for (long i = 0; i < fetched; i++)
                            if (_rowStatus[i] == Native.SQL_ROW_ERROR) rowErrors++;
                    if (captureInfo && infoDrains < 3)
                    {
                        infoDrains++;
                        foreach (var d in statement.DrainDiagnostics(10))
                            if (d.SqlState != "01004") sample.AddWarning(d.ToString());
                    }
                }

                TouchBound(slots, fetched, ref counters);
                if (hasUnbound) ReadUnbound(statement, getData!, slots, ref counters); // row array size is 1 here

                if (processingTicksPerRow > 0)
                {
                    // Busy-wait like CPU-bound client code; Thread.Sleep oversleeps by more than a 1,000-row array's worth.
                    long tWork0 = Stopwatch.GetTimestamp();
                    long until = tWork0 + (long)(processingTicksPerRow * fetched);
                    while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(20);
                    processingTicks += Stopwatch.GetTimestamp() - tWork0;
                }

                rows += fetched;
                if (fetched > _maxBatchRows) _maxBatchRows = fetched;
                if (rowLimit.HasValue && rows >= rowLimit.Value) break;
            }
            long tFetch1 = Stopwatch.GetTimestamp();
            sample.AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated0;
            sample.FetchMs = Ms(tFetch0, tFetch1);
            sample.ProcessingMs = processingTicks * 1000.0 / Stopwatch.Frequency;

            statement.CloseCursor();
            sample.CloseMs = Ms(tFetch1, Stopwatch.GetTimestamp());

            ulong checksum = Fnv1a64.Offset;
            for (int c = 0; c < slots.Length; c++) checksum = Fnv1a64.Combine(checksum, slots[c].Hash);

            sample.TotalMs = sample.ExecuteMs + sample.DescribeMs + sample.FetchMs + sample.CloseMs;
            sample.Rows = rows;
            sample.Batches = batches;
            sample.Bytes = counters.Bytes;
            sample.BytesApprox = counters.Approx;
            sample.Nulls = counters.Nulls;
            sample.Truncations = counters.Truncations;
            sample.RowErrors = rowErrors;
            sample.ChecksumValue = checksum;
            sample.Checksum = checksum.ToString("x16");
            sample.EffectiveBlockSize = arraySize;

            FinishStatement();
            return sample;
        }
        catch (OdbcException ex)
        {
            MarkFailed(sample, ex.SqlState, ex.Message, ex.IsConnectionFailure);
            return sample;
        }
        catch (Exception ex)
        {
            MarkFailed(sample, null, ex.Message, false);
            return sample;
        }
    }

    /// <summary>
    /// Reads every value of every bound column for the fetched rows, column by column (the arrays are column-wise).
    /// Fixed-width values are hashed at their C size; variable-width values at their indicator length, capped at the
    /// buffer (SQL_NO_TOTAL and 01004 truncations are counted).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void TouchBound(Slot[] slots, long rows, ref Counters counters)
    {
        long bytes = 0, nulls = 0, truncations = 0;
        bool approx = false;
        for (int c = 0; c < slots.Length; c++)
        {
            ref Slot s = ref slots[c];
            if (s.Unbound != null) continue;
            byte* data = s.Data;
            nint* indicators = s.Indicators;
            int stride = s.ElementBytes;
            ulong h = s.Hash;
            if (s.Fixed)
            {
                for (long i = 0; i < rows; i++)
                {
                    if (indicators[i] == Native.SQL_NULL_DATA)
                    {
                        nulls++;
                        h = Fnv1a64.AddNull(h);
                    }
                    else
                    {
                        h = Fnv1a64.Add(h, data + i * stride, stride);
                        bytes += stride;
                    }
                }
            }
            else
            {
                int max = s.MaxDataBytes;
                for (long i = 0; i < rows; i++)
                {
                    nint length = indicators[i];
                    if (length == Native.SQL_NULL_DATA)
                    {
                        nulls++;
                        h = Fnv1a64.AddNull(h);
                        continue;
                    }
                    long n;
                    if (length < 0)
                    {
                        n = max; // SQL_NO_TOTAL: the buffer is full and the driver could not say how much more there was
                        approx = true;
                    }
                    else if (length > max)
                    {
                        n = max; // 01004 right truncation: the remainder is not recoverable in block mode
                        truncations++;
                        approx = true;
                    }
                    else
                    {
                        n = length;
                    }
                    h = Fnv1a64.Add(h, data + i * stride, n);
                    bytes += n;
                }
            }
            s.Hash = h;
        }
        counters.Bytes += bytes;
        counters.Nulls += nulls;
        counters.Truncations += truncations;
        counters.Approx |= approx;
    }

    /// <summary>Reads the unbound (long) columns of the current row with chunked SQLGetData, in ascending ordinal order.</summary>
    private static void ReadUnbound(OdbcStatement statement, GetDataReader getData, Slot[] slots, ref Counters counters)
    {
        for (int c = 0; c < slots.Length; c++)
        {
            ref Slot s = ref slots[c];
            if (s.Unbound == null) continue;
            ulong h = s.Hash;
            long n = getData.ReadAndHash(statement, s.Unbound, ref h, out bool isNull, ref counters.Approx);
            if (isNull)
            {
                counters.Nulls++;
                h = Fnv1a64.AddNull(h);
            }
            else
            {
                counters.Bytes += n;
            }
            s.Hash = h;
        }
    }

    public CalibrationSample? Calibrate(double minMs = 25)
    {
        long rows = _maxBatchRows;
        if (rows <= 0 || _slots.Length == 0) return null;
        int boundColumns = 0;
        foreach (var s in _slots) if (s.Unbound == null) boundColumns++;
        if (boundColumns == 0) return null;

        // Every row of the arrays holds a value from some batch (the last batch only overwrote a prefix), so replaying the
        // largest batch size reads valid indicators and data.
        var counters = new Counters();
        long passes = 0;
        long t0 = Stopwatch.GetTimestamp();
        long deadline = t0 + (long)(minMs * Stopwatch.Frequency / 1000.0);
        long now;
        do
        {
            TouchBound(_slots, rows, ref counters);
            passes++;
            now = Stopwatch.GetTimestamp();
        } while (now < deadline);

        return new CalibrationSample(rows * boundColumns, counters.Bytes / passes, Ms(t0, now) / passes, boundColumns);
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
            TrySetAttribute(statement, Native.SQL_ATTR_CURSOR_TYPE, Native.SQL_CURSOR_FORWARD_ONLY, "SQL_ATTR_CURSOR_TYPE");
            TrySetAttribute(statement, Native.SQL_ATTR_CONCURRENCY, Native.SQL_CONCUR_READ_ONLY, "SQL_ATTR_CONCURRENCY");
            TrySetAttribute(statement, Native.SQL_ATTR_MAX_ROWS, 0, "SQL_ATTR_MAX_ROWS");
            TrySetAttribute(statement, Native.SQL_ATTR_QUERY_TIMEOUT, (nuint)Math.Max(0, _options.QueryTimeoutSeconds), "SQL_ATTR_QUERY_TIMEOUT");
            TrySetAttribute(statement, Native.SQL_ATTR_ROW_BIND_TYPE, Native.SQL_BIND_BY_COLUMN, "SQL_ATTR_ROW_BIND_TYPE");
            _driverArraySize = ApplyArraySize(statement, _options.BlockSize);
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

    /// <summary>Sets SQL_ATTR_ROW_ARRAY_SIZE and reads it back: the effective value is what the driver says, not what we asked.</summary>
    private int ApplyArraySize(OdbcStatement statement, int size)
    {
        bool exact = statement.SetUIntAttribute(Native.SQL_ATTR_ROW_ARRAY_SIZE, (nuint)size, "SQL_ATTR_ROW_ARRAY_SIZE");
        nuint actual = statement.GetULenAttribute(Native.SQL_ATTR_ROW_ARRAY_SIZE, "SQL_ATTR_ROW_ARRAY_SIZE");
        int effective = actual == 0 || actual > int.MaxValue ? size : (int)actual;
        if (!exact || effective != size)
            AddWarningOnce($"driver changed SQL_ATTR_ROW_ARRAY_SIZE from {size} to {effective}");
        _currentArraySize = effective;
        return effective;
    }

    private void DescribeAndBind(OdbcStatement statement, IterationSample sample)
    {
        long t0 = Stopwatch.GetTimestamp();

        short count = statement.NumResultCols();
        if (count <= 0) throw new InvalidOperationException("the statement returned no result columns");

        var columns = new List<ColumnInfo>(count);
        for (int i = 1; i <= count; i++)
        {
            var column = statement.DescribeColumn(i);
            TypeMapper.Apply(column, _options.BindMode, _options.LongColumnThresholdBytes);
            columns.Add(column);
        }

        bool anyColumn = _connection.Driver?.SupportsGetDataAnyColumn ?? false;

        for (int attempt = 0; ; attempt++)
        {
            var plan = BindingPlan.Build(columns, _options.BlockSize, _driverArraySize, anyColumn,
                _options.LongColumnMode, _options.LongColumnCapBytes, _options.MaxBoundBytes);

            if (plan.ArraySize != _currentArraySize)
            {
                int effective = ApplyArraySize(statement, plan.ArraySize);
                if (effective != plan.ArraySize)
                {
                    AddWarningOnce($"driver refused row array size {plan.ArraySize} after the binding plan lowered it; using {effective}");
                    plan.ArraySize = Math.Min(effective, Math.Max(1, plan.ArraySize));
                }
            }

            EnsureBuffers(plan);
            _rowsFetchedPtrSet = TrySetPointer(statement, Native.SQL_ATTR_ROWS_FETCHED_PTR, _rowsFetched, "SQL_ATTR_ROWS_FETCHED_PTR");
            _rowStatusPtrSet = TrySetPointer(statement, Native.SQL_ATTR_ROW_STATUS_PTR, _rowStatus, "SQL_ATTR_ROW_STATUS_PTR");
            if (!_rowsFetchedPtrSet && !_rowStatusPtrSet && plan.ArraySize > 1)
            {
                AddWarningOnce("the driver accepts neither SQL_ATTR_ROWS_FETCHED_PTR nor SQL_ATTR_ROW_STATUS_PTR: rows fetched per call cannot be known, so the row array size is forced to 1");
                plan.ArraySize = ApplyArraySize(statement, 1);
            }

            try
            {
                foreach (var b in _buffers)
                    statement.BindCol(b.Column.Ordinal, b.Column.CType, b.Data, b.ElementBytes, b.Indicators);
            }
            catch (OdbcException ex) when (ex.SqlState == "07006" && attempt < columns.Count)
            {
                int ordinal = ParseOrdinal(ex.Function);
                var column = columns.FirstOrDefault(c => c.Ordinal == ordinal && !c.FellBackToText);
                if (column == null) throw;
                string rejected = column.CTypeName;
                TypeMapper.ForceText(column, _options.LongColumnThresholdBytes);
                AddWarningOnce($"column '{column.Name}' ({column.SqlTypeName}): driver rejected {rejected} (07006); bound as SQL_C_WCHAR instead");
                statement.Unbind();
                continue;
            }

            _plan = plan;
            _columns = columns;
            _slots = BuildSlots(plan);
            foreach (var w in plan.Warnings) AddWarningOnce(w);
            if (plan.Unbound.Count > 0) _getData ??= new GetDataReader(_options.GetDataChunkBytes);
            _bound = true;
            break;
        }

        sample.DescribeMs = Ms(t0, Stopwatch.GetTimestamp());
    }

    private Slot[] BuildSlots(BindingPlan plan)
    {
        var slots = new Slot[plan.Columns.Count];
        for (int i = 0; i < slots.Length; i++)
        {
            var column = plan.Columns[i];
            var buffer = Array.Find(_buffers, b => b.Column.Ordinal == column.Ordinal);
            slots[i] = buffer != null
                ? new Slot
                {
                    Data = buffer.Data,
                    Indicators = buffer.Indicators,
                    ElementBytes = buffer.ElementBytes,
                    MaxDataBytes = buffer.MaxDataBytes,
                    Fixed = !TypeMapper.IsVariableWidth(column.CType),
                }
                : new Slot { Unbound = column };
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

        if (_rowStatus == null || _rowStatusCapacity < rows)
        {
            if (_rowStatus != null) NativeMemory.Free(_rowStatus);
            _rowStatusCapacity = Math.Max(rows, 1);
            _rowStatus = (ushort*)NativeMemory.AllocZeroed((nuint)(sizeof(ushort) * _rowStatusCapacity));
        }
    }

    private long CountRowsFromStatus(int arraySize)
    {
        if (!_rowStatusPtrSet) return arraySize;
        long n = 0;
        for (int i = 0; i < arraySize; i++)
            if (_rowStatus[i] != Native.SQL_ROW_NOROW) n++;
        return n;
    }

    private void MarkFailed(IterationSample sample, string? sqlState, string message, bool connectionLost)
    {
        sample.Ok = false;
        sample.ErrorSqlState = sqlState;
        sample.ErrorMessage = message;
        sample.ConnectionLost = connectionLost;
        sample.EffectiveBlockSize = _currentArraySize;
        _statement?.TryCloseCursor();
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
        // Function names look like "SQLBindCol(column 7)".
        int start = function.IndexOf("column ", StringComparison.Ordinal);
        if (start < 0) return -1;
        start += "column ".Length;
        int end = start;
        while (end < function.Length && char.IsDigit(function[end])) end++;
        return int.TryParse(function.AsSpan(start, end - start), out int ordinal) ? ordinal : -1;
    }

    private static double Ms(long t0, long t1) => (t1 - t0) * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        _statement?.Dispose(); // SQLFreeHandle(STMT) first: the driver must not hold our buffers any more
        _statement = null;
        foreach (var b in _buffers) b.Dispose();
        _buffers = Array.Empty<ColumnBuffer>();
        _slots = Array.Empty<Slot>();
        _getData?.Dispose();
        _getData = null;
        if (_rowsFetched != null)
        {
            NativeMemory.Free(_rowsFetched);
            _rowsFetched = null;
        }
        if (_rowStatus != null)
        {
            NativeMemory.Free(_rowStatus);
            _rowStatus = null;
        }
    }
}
