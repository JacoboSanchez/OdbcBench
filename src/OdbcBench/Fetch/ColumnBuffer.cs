using System.Runtime.InteropServices;
using OdbcBench.Odbc;

namespace OdbcBench.Fetch;

/// <summary>
/// Unmanaged column-wise array for one bound column: <c>Rows</c> elements of <c>ElementBytes</c> plus one SQLLEN indicator per row.
/// Allocated once per series and reused by every iteration; never touched by the GC.
/// </summary>
public sealed unsafe class ColumnBuffer : IDisposable
{
    public ColumnInfo Column { get; }
    public int ElementBytes { get; }
    /// <summary>Largest indicator value that fits without truncation (element minus the terminator for character types).</summary>
    public int MaxDataBytes { get; }
    public int Rows { get; }
    public byte* Data { get; private set; }
    public nint* Indicators { get; private set; }

    public ColumnBuffer(ColumnInfo column, int rows)
    {
        if (rows < 1) throw new ArgumentOutOfRangeException(nameof(rows));
        if (column.ElementBytes < 1) throw new ArgumentException($"column '{column.Name}' has no bound width", nameof(column));

        Column = column;
        ElementBytes = column.ElementBytes;
        Rows = rows;
        MaxDataBytes = column.CType switch
        {
            Native.SQL_C_WCHAR => ElementBytes - 2,
            Native.SQL_C_CHAR => ElementBytes - 1,
            _ => ElementBytes,
        };

        nuint dataBytes = (nuint)ElementBytes * (nuint)rows;
        Data = (byte*)NativeMemory.AlignedAlloc(dataBytes, 64);
        NativeMemory.Clear(Data, dataBytes);

        nuint indicatorBytes = (nuint)sizeof(nint) * (nuint)rows;
        Indicators = (nint*)NativeMemory.AlignedAlloc(indicatorBytes, 64);
        NativeMemory.Clear(Indicators, indicatorBytes);
    }

    /// <summary>True when this buffer can serve the given column at the given rowset size without reallocation.</summary>
    public bool Matches(ColumnInfo column, int rows) =>
        Rows == rows && Column.Ordinal == column.Ordinal && Column.CType == column.CType && ElementBytes == column.ElementBytes;

    public long TotalDataBytes => (long)ElementBytes * Rows;

    public void Dispose()
    {
        if (Data != null)
        {
            NativeMemory.AlignedFree(Data);
            Data = null;
        }
        if (Indicators != null)
        {
            NativeMemory.AlignedFree(Indicators);
            Indicators = null;
        }
    }
}
