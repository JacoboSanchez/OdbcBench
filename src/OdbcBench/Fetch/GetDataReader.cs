using System.Runtime.InteropServices;
using System.Text;
using OdbcBench.Odbc;

namespace OdbcBench.Fetch;

/// <summary>
/// Chunked SQLGetData reader with one reusable scratch buffer. Used for unbound (long) columns in the benchmark and
/// for the validation sample fetch. Never trusts SQL_NO_TOTAL: byte counts come from the chunks actually received.
/// </summary>
public sealed unsafe class GetDataReader : IDisposable
{
    private byte* _scratch;
    private readonly int _scratchBytes;
    private const long MaxChunks = 4_000_000; // 128 GB with the default chunk size: a driver that never finishes

    public GetDataReader(int scratchBytes = 32768)
    {
        _scratchBytes = Math.Max(1024, scratchBytes) & ~1; // even: whole UTF-16 code units
        _scratch = (byte*)NativeMemory.Alloc((nuint)_scratchBytes);
    }

    /// <summary>
    /// Reads one value completely, folding its bytes into <paramref name="hash"/>. Returns the bytes read (0 for NULL).
    /// <paramref name="approx"/> is set when the driver gave no usable length and the byte count is an estimate.
    /// </summary>
    public long ReadAndHash(OdbcStatement statement, ColumnInfo column, ref ulong hash, out bool isNull, ref bool approx)
    {
        short cType = column.CType;
        bool variable = TypeMapper.IsVariableWidth(cType);
        int terminator = cType == Native.SQL_C_WCHAR ? 2 : cType == Native.SQL_C_CHAR ? 1 : 0;
        int maxData = _scratchBytes - terminator;
        long total = 0;
        isNull = false;

        for (long chunk = 0; ; chunk++)
        {
            nint indicator;
            short rc = statement.GetData(column.Ordinal, cType, _scratch, _scratchBytes, &indicator);
            if (rc == Native.SQL_NO_DATA) break;
            if (rc == Native.SQL_ERROR || rc == Native.SQL_INVALID_HANDLE)
                throw new OdbcException($"SQLGetData(column {column.Ordinal})", rc, statement.DrainDiagnostics());
            if (indicator == Native.SQL_NULL_DATA)
            {
                isNull = true;
                break;
            }

            long n;
            if (!variable)
            {
                int size = TypeMapper.FixedSize(cType);
                n = size > 0 ? size : indicator > 0 && indicator <= _scratchBytes ? (long)indicator : 0;
                hash = Fnv1a64.Add(hash, _scratch, n);
                total += n;
                break;
            }
            if (rc == Native.SQL_SUCCESS)
            {
                // Final (or only) piece: the indicator is the length of this piece.
                if (indicator >= 0) n = Math.Min((long)indicator, maxData);
                else
                {
                    n = cType == Native.SQL_C_WCHAR ? NulTerminatedBytes(maxData) : maxData;
                    approx = true;
                }
                hash = Fnv1a64.Add(hash, _scratch, n);
                total += n;
                break;
            }
            // SQL_SUCCESS_WITH_INFO: a truncated chunk (01004, more to come) unless everything fitted and the info was something else.
            if (indicator >= 0 && indicator <= maxData)
            {
                n = indicator;
                hash = Fnv1a64.Add(hash, _scratch, n);
                total += n;
                break;
            }
            n = maxData;
            hash = Fnv1a64.Add(hash, _scratch, n);
            total += n;
            if (chunk > MaxChunks)
                throw new InvalidOperationException($"SQLGetData on column {column.Ordinal} ('{column.Name}') did not terminate");
        }
        return total;
    }

    /// <summary>Reads one value as SQL_C_WCHAR text (chunked). Returns null for SQL NULL.</summary>
    public string? ReadText(OdbcStatement statement, int ordinal)
    {
        StringBuilder? builder = null;
        int maxData = _scratchBytes - 2;

        for (long chunk = 0; ; chunk++)
        {
            nint indicator;
            short rc = statement.GetData(ordinal, Native.SQL_C_WCHAR, _scratch, _scratchBytes, &indicator);
            if (rc == Native.SQL_NO_DATA) break;
            if (rc == Native.SQL_ERROR || rc == Native.SQL_INVALID_HANDLE)
                throw new OdbcException($"SQLGetData(column {ordinal})", rc, statement.DrainDiagnostics());
            if (indicator == Native.SQL_NULL_DATA) return null;

            int n;
            bool last;
            if (rc == Native.SQL_SUCCESS)
            {
                n = indicator < 0 ? NulTerminatedBytes(maxData) : (int)Math.Min((long)indicator, maxData);
                last = true;
            }
            else if (indicator >= 0 && indicator <= maxData)
            {
                n = (int)indicator;
                last = true;
            }
            else
            {
                n = maxData;
                last = false;
            }
            builder ??= new StringBuilder();
            builder.Append((char*)_scratch, n / 2);
            if (last) break;
            if (chunk > MaxChunks)
                throw new InvalidOperationException($"SQLGetData on column {ordinal} did not terminate");
        }
        return builder?.ToString() ?? "";
    }

    private int NulTerminatedBytes(int maxData)
    {
        char* chars = (char*)_scratch;
        int maxChars = maxData / 2;
        for (int i = 0; i < maxChars; i++)
            if (chars[i] == '\0') return i * 2;
        return maxData;
    }

    public void Dispose()
    {
        if (_scratch != null)
        {
            NativeMemory.Free(_scratch);
            _scratch = null;
        }
    }
}
