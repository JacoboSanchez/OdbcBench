using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using OdbcBench.Odbc;

namespace OdbcBench.Fetch;

/// <summary>
/// Everything measured or counted in one iteration: an execute-fetch-close cycle of the query, or one pass of the
/// batch insert (every SQLExecute of the parameter arrays plus the commit).
/// </summary>
public sealed class IterationSample
{
    /// <summary>Iteration number within its phase (warmup or measured), starting at 1. -1 for the validation dry run.</summary>
    public int Index { get; set; }
    public bool Warmup { get; set; }
    public DateTime StartedUtc { get; set; }
    public bool Ok { get; set; } = true;
    public string? ErrorSqlState { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Only with connectionPerIteration; never part of TotalMs.</summary>
    public double ConnectMs { get; set; }
    /// <summary>SQLExecDirectW; for the insert benchmark, every SQLExecute call.</summary>
    public double ExecuteMs { get; set; }
    /// <summary>Describe + bind (insert: prepare + bind); non-zero only when the statement was (re)built in this iteration.</summary>
    public double DescribeMs { get; set; }
    /// <summary>First SQLFetchScroll call (the first row array, not the first row); for the insert benchmark, the first SQLExecute.</summary>
    public double FirstBatchMs { get; set; }
    /// <summary>Every SQLFetchScroll call plus reading every value.</summary>
    public double FetchMs { get; set; }
    /// <summary>SQLFreeStmt(SQL_CLOSE).</summary>
    public double CloseMs { get; set; }
    /// <summary>Insert benchmark: every SQLEndTran(SQL_COMMIT) call.</summary>
    public double CommitMs { get; set; }
    /// <summary>Insert benchmark: filling the parameter arrays with generated values; never part of TotalMs.</summary>
    public double GenerateMs { get; set; }
    /// <summary>ExecuteMs + DescribeMs + FetchMs + CloseMs + CommitMs.</summary>
    public double TotalMs { get; set; }

    /// <summary>Rows read; for the insert benchmark, rows the driver accepted.</summary>
    public long Rows { get; set; }
    public long Batches { get; set; }
    /// <summary>Sum of the lengths of every non-NULL value read or sent (data bytes, not wire bytes).</summary>
    public long Bytes { get; set; }
    /// <summary>True when truncation or SQL_NO_TOTAL made the byte count a lower bound.</summary>
    public bool BytesApprox { get; set; }
    public long Nulls { get; set; }
    public long Truncations { get; set; }
    public long RowErrors { get; set; }
    /// <summary>Insert benchmark: rows the table gained during the iteration, counted with SELECT COUNT(*); null when not checked.</summary>
    public long? VerifiedRows { get; set; }

    /// <summary>FNV-1a fingerprint of every value read or sent, per column in row order, combined in column order.</summary>
    [JsonIgnore]
    public ulong ChecksumValue { get; set; }
    /// <summary>Hex form of <see cref="ChecksumValue"/>; equal across iterations when the data and its order are stable.</summary>
    public string Checksum { get; set; } = "";

    public int EffectiveBlockSize { get; set; }
    /// <summary>Process user + kernel CPU time spent during execute, fetch and close.</summary>
    public double CpuMs { get; set; }
    public int Gc0 { get; set; }
    public int Gc1 { get; set; }
    public int Gc2 { get; set; }
    /// <summary>Managed bytes allocated by the fetch or insert loop (expected 0).</summary>
    public long AllocatedBytes { get; set; }
    public List<string>? Warnings { get; set; }

    /// <summary>The failure was a lost connection (SQLSTATE class 08 or an invalid handle).</summary>
    [JsonIgnore]
    public bool ConnectionLost { get; set; }

    public void AddWarning(string message)
    {
        Warnings ??= new List<string>();
        if (Warnings.Count < 20 && !Warnings.Contains(message)) Warnings.Add(message);
    }
}

/// <summary>Cost of the harness's own value-touching loop, measured by replaying it over the last row array without any ODBC call.</summary>
public sealed record CalibrationSample(long ValuesPerPass, long BytesPerPass, double MsPerPass, int BoundColumns);

/// <summary>
/// One access path. Block fetch (execute + read every value + close) and batch insert (execute the parameter arrays +
/// commit) exist today; others plug in here.
/// </summary>
public interface IResultReader : IDisposable
{
    string Description { get; }
    IReadOnlyList<ColumnInfo>? Columns { get; }
    BindingPlan? Plan { get; }
    IReadOnlyList<string> Warnings { get; }
    int CurrentArraySize { get; }

    /// <summary>
    /// Untimed work an iteration needs before it starts (the insert path empties and counts the table here).
    /// Returns the failed sample when that work fails, null when the iteration can run.
    /// </summary>
    IterationSample? Prepare(int index, bool warmup) => null;

    /// <summary>Runs one complete iteration touching every value. <paramref name="rowLimit"/> stops early (dry runs, probe).</summary>
    IterationSample Execute(int index, bool warmup, long? rowLimit = null);

    /// <summary>Untimed checks after an iteration (the insert path counts the rows the table gained).</summary>
    void Verify(IterationSample sample) { }

    /// <summary>Replays the value-touching loop over the bound buffers for at least <paramref name="minMs"/>; null when nothing was fetched.</summary>
    CalibrationSample? Calibrate(double minMs = 25);
}

/// <summary>64-bit FNV-1a over raw value bytes. Cheap, allocation-free, and its result is kept so the JIT cannot drop the loads.</summary>
public static unsafe class Fnv1a64
{
    public const ulong Offset = 14695981039346656037UL;
    public const ulong Prime = 1099511628211UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Add(ulong hash, byte* data, long length)
    {
        long i = 0;
        for (; i + 8 <= length; i += 8)
        {
            hash ^= Unsafe.ReadUnaligned<ulong>(data + i);
            hash *= Prime;
        }
        for (; i < length; i++)
        {
            hash ^= data[i];
            hash *= Prime;
        }
        return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong AddNull(ulong hash) => (hash ^ 0xA5A5A5A5A5A5A5A5UL) * Prime;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Combine(ulong hash, ulong value) => (hash ^ value) * Prime;
}
