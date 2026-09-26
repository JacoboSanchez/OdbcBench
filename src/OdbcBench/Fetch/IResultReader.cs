using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using OdbcBench.Odbc;

namespace OdbcBench.Fetch;

/// <summary>Everything measured or counted in one execute-fetch-close cycle.</summary>
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
    /// <summary>SQLExecDirectW.</summary>
    public double ExecuteMs { get; set; }
    /// <summary>Describe + bind; non-zero only when the statement was (re)built in this iteration.</summary>
    public double DescribeMs { get; set; }
    /// <summary>First SQLFetchScroll call (the first row array, not the first row).</summary>
    public double FirstBatchMs { get; set; }
    /// <summary>Every SQLFetchScroll call plus reading every value.</summary>
    public double FetchMs { get; set; }
    /// <summary>SQLFreeStmt(SQL_CLOSE).</summary>
    public double CloseMs { get; set; }
    /// <summary>ExecuteMs + DescribeMs + FetchMs + CloseMs.</summary>
    public double TotalMs { get; set; }

    public long Rows { get; set; }
    public long Batches { get; set; }
    /// <summary>Sum of the lengths of every non-NULL value read (data bytes, not wire bytes).</summary>
    public long Bytes { get; set; }
    /// <summary>True when truncation or SQL_NO_TOTAL made the byte count a lower bound.</summary>
    public bool BytesApprox { get; set; }
    public long Nulls { get; set; }
    public long Truncations { get; set; }
    public long RowErrors { get; set; }

    /// <summary>FNV-1a fingerprint of every value read, per column in row order, combined in column order.</summary>
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
    /// <summary>Managed bytes allocated by the fetch loop (expected 0).</summary>
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

/// <summary>One access path (execute + read every value + close). Only block fetch exists today; others plug in here.</summary>
public interface IResultReader : IDisposable
{
    string Description { get; }
    IReadOnlyList<ColumnInfo>? Columns { get; }
    BindingPlan? Plan { get; }
    IReadOnlyList<string> Warnings { get; }
    int CurrentArraySize { get; }

    /// <summary>Runs one complete execute-fetch-close cycle reading every value. <paramref name="rowLimit"/> stops early (dry runs, probe).</summary>
    IterationSample Execute(int index, bool warmup, long? rowLimit = null);

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
