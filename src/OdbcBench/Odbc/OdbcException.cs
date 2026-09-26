namespace OdbcBench.Odbc;

/// <summary>One diagnostic record as returned by SQLGetDiagRecW (plus row/column when the driver reports them).</summary>
public sealed record OdbcDiagnostic(string SqlState, int NativeError, string Message, long? RowNumber = null, int? ColumnNumber = null)
{
    public override string ToString()
    {
        var where = RowNumber is { } r && r >= 0
            ? (ColumnNumber is { } c && c > 0 ? $" row {r}, column {c}:" : $" row {r}:")
            : "";
        return $"[{SqlState}] ({NativeError}){where} {Message}";
    }
}

/// <summary>Raised when an ODBC call returns SQL_ERROR or SQL_INVALID_HANDLE. Carries every diagnostic record of the handle.</summary>
public sealed class OdbcException : Exception
{
    public string Function { get; }
    public short ReturnCode { get; }
    public IReadOnlyList<OdbcDiagnostic> Diagnostics { get; }
    public string? SqlState => Diagnostics.Count > 0 ? Diagnostics[0].SqlState : null;
    public int? NativeError => Diagnostics.Count > 0 ? Diagnostics[0].NativeError : null;

    public OdbcException(string function, short returnCode, IReadOnlyList<OdbcDiagnostic> diagnostics)
        : base(Format(function, returnCode, diagnostics))
    {
        Function = function;
        ReturnCode = returnCode;
        Diagnostics = diagnostics;
    }

    /// <summary>True for connection-level failures (class 08 SQLSTATEs or an invalid handle) after which the connection should be re-established.</summary>
    public bool IsConnectionFailure =>
        ReturnCode == Native.SQL_INVALID_HANDLE ||
        (SqlState is { Length: 5 } s && s.StartsWith("08", StringComparison.Ordinal));

    private static string Format(string function, short rc, IReadOnlyList<OdbcDiagnostic> diagnostics)
    {
        var name = ReturnCodeName(rc);
        if (diagnostics.Count == 0)
            return $"{function} failed ({name}) with no diagnostic records";
        return $"{function} failed ({name}): {string.Join(" | ", diagnostics)}";
    }

    public static string ReturnCodeName(short rc) => rc switch
    {
        Native.SQL_SUCCESS => "SQL_SUCCESS",
        Native.SQL_SUCCESS_WITH_INFO => "SQL_SUCCESS_WITH_INFO",
        Native.SQL_STILL_EXECUTING => "SQL_STILL_EXECUTING",
        Native.SQL_NEED_DATA => "SQL_NEED_DATA",
        Native.SQL_NO_DATA => "SQL_NO_DATA",
        Native.SQL_ERROR => "SQL_ERROR",
        Native.SQL_INVALID_HANDLE => "SQL_INVALID_HANDLE",
        _ => $"rc={rc}",
    };
}

/// <summary>
/// Diagnostic helpers. Records are per handle and overwritten by the next call on that handle,
/// so they are always drained immediately after the call that produced them.
/// </summary>
internal static unsafe class Diag
{
    public static List<OdbcDiagnostic> Drain(short handleType, nint handle, int maxRecords = 200)
    {
        var list = new List<OdbcDiagnostic>();
        if (handle == Native.SQL_NULL_HANDLE) return list;

        const int MessageChars = 2048;
        char* state = stackalloc char[6];
        char* message = stackalloc char[MessageChars];

        for (short record = 1; record <= Math.Min(maxRecords, 1000); record++)
        {
            int nativeError;
            short textLength;
            short rc = Native.SQLGetDiagRecW(handleType, handle, record, state, &nativeError, message, MessageChars, &textLength);
            if (!Native.Succeeded(rc)) break;

            int n = textLength < 0 ? 0 : Math.Min((int)textLength, MessageChars - 1);
            string text = new string(message, 0, n).TrimEnd('\0', ' ', '\r', '\n');
            string sqlState = new string(state, 0, 5).TrimEnd('\0');

            long? row = null;
            int? column = null;
            if (handleType == Native.SQL_HANDLE_STMT)
            {
                long rowNumber = -1;   // SQLLEN
                int columnNumber = -1; // SQLINTEGER
                short ignored;
                if (Native.Succeeded(Native.SQLGetDiagFieldW(handleType, handle, record, Native.SQL_DIAG_ROW_NUMBER, &rowNumber, sizeof(long), &ignored)) && rowNumber >= 0)
                    row = rowNumber;
                if (Native.Succeeded(Native.SQLGetDiagFieldW(handleType, handle, record, Native.SQL_DIAG_COLUMN_NUMBER, &columnNumber, sizeof(int), &ignored)) && columnNumber > 0)
                    column = columnNumber;
            }
            list.Add(new OdbcDiagnostic(sqlState, nativeError, text, row, column));
        }
        return list;
    }

    /// <summary>
    /// Throws <see cref="OdbcException"/> for SQL_ERROR / SQL_INVALID_HANDLE. For SQL_SUCCESS_WITH_INFO the records are
    /// appended to <paramref name="info"/> when given. Returns the return code unchanged otherwise (SQL_NO_DATA included).
    /// </summary>
    public static short Check(short rc, string function, short handleType, nint handle, ICollection<OdbcDiagnostic>? info = null)
    {
        if (rc == Native.SQL_ERROR || rc == Native.SQL_INVALID_HANDLE)
            throw new OdbcException(function, rc, Drain(handleType, handle));
        if (rc == Native.SQL_SUCCESS_WITH_INFO && info != null)
            foreach (var d in Drain(handleType, handle)) info.Add(d);
        return rc;
    }
}
