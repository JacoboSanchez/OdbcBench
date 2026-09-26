namespace OdbcBench.Odbc;

/// <summary>A statement handle. Thin wrapper: hot-path calls (SQLFetchScroll, SQLGetData) return the raw return code.</summary>
public sealed unsafe class OdbcStatement : IDisposable
{
    public nint Handle { get; private set; }
    public OdbcConnection Connection { get; }
    /// <summary>SQL_SUCCESS_WITH_INFO records collected by the checked calls.</summary>
    public List<OdbcDiagnostic> Info { get; } = new();

    public OdbcStatement(OdbcConnection connection)
    {
        nint handle;
        short rc = Native.SQLAllocHandle(Native.SQL_HANDLE_STMT, connection.Handle, &handle);
        Diag.Check(rc, "SQLAllocHandle(SQL_HANDLE_STMT)", Native.SQL_HANDLE_DBC, connection.Handle);
        Handle = handle;
        Connection = connection;
    }

    /// <summary>Sets an integer statement attribute. Returns false when the driver substituted another value (SQLSTATE 01S02).</summary>
    public bool SetUIntAttribute(int attribute, nuint value, string attributeName)
    {
        int before = Info.Count;
        short rc = Native.SQLSetStmtAttrW(Handle, attribute, (nint)value, Native.SQL_IS_UINTEGER);
        Diag.Check(rc, $"SQLSetStmtAttrW({attributeName})", Native.SQL_HANDLE_STMT, Handle, Info);
        if (rc == Native.SQL_SUCCESS_WITH_INFO)
            for (int i = before; i < Info.Count; i++)
                if (Info[i].SqlState == "01S02") return false;
        return true;
    }

    public void SetPointerAttribute(int attribute, void* pointer, string attributeName)
    {
        short rc = Native.SQLSetStmtAttrW(Handle, attribute, (nint)pointer, Native.SQL_IS_POINTER);
        Diag.Check(rc, $"SQLSetStmtAttrW({attributeName})", Native.SQL_HANDLE_STMT, Handle, Info);
    }

    public nuint GetULenAttribute(int attribute, string attributeName)
    {
        nuint value = 0;
        int length = 0;
        short rc = Native.SQLGetStmtAttrW(Handle, attribute, &value, 0, &length);
        Diag.Check(rc, $"SQLGetStmtAttrW({attributeName})", Native.SQL_HANDLE_STMT, Handle, Info);
        return value;
    }

    /// <summary>Executes the statement text. Returns SQL_SUCCESS, SQL_SUCCESS_WITH_INFO or SQL_NO_DATA; throws on error.</summary>
    public short ExecDirect(string sql)
    {
        short rc;
        fixed (char* text = sql)
        {
            rc = Native.SQLExecDirectW(Handle, text, Native.SQL_NTS);
        }
        return Diag.Check(rc, "SQLExecDirectW", Native.SQL_HANDLE_STMT, Handle, Info);
    }

    public short NumResultCols()
    {
        short count;
        short rc = Native.SQLNumResultCols(Handle, &count);
        Diag.Check(rc, "SQLNumResultCols", Native.SQL_HANDLE_STMT, Handle, Info);
        return count;
    }

    public ColumnInfo DescribeColumn(int ordinal)
    {
        const int NameChars = 512;
        char* name = stackalloc char[NameChars];
        short nameLength, dataType, decimalDigits, isNullable;
        nuint columnSize;
        short rc = Native.SQLDescribeColW(Handle, (ushort)ordinal, name, NameChars, &nameLength, &dataType, &columnSize, &decimalDigits, &isNullable);
        Diag.Check(rc, "SQLDescribeColW", Native.SQL_HANDLE_STMT, Handle, Info);
        int n = nameLength < 0 ? 0 : Math.Min((int)nameLength, NameChars - 1);

        var column = new ColumnInfo
        {
            Ordinal = ordinal,
            Name = new string(name, 0, n).TrimEnd('\0'),
            SqlType = dataType,
            ColumnSize = columnSize,
            DecimalDigits = decimalDigits,
            Nullable = isNullable,
        };
        column.Length = NumericAttribute(ordinal, Native.SQL_DESC_LENGTH);
        column.OctetLength = NumericAttribute(ordinal, Native.SQL_DESC_OCTET_LENGTH);
        column.DisplaySize = NumericAttribute(ordinal, Native.SQL_DESC_DISPLAY_SIZE);
        column.Unsigned = NumericAttribute(ordinal, Native.SQL_DESC_UNSIGNED) == Native.SQL_TRUE;
        column.TypeName = StringAttribute(ordinal, Native.SQL_DESC_TYPE_NAME);
        return column;
    }

    /// <summary>Numeric column attribute; -1 when the driver does not provide it.</summary>
    private long NumericAttribute(int ordinal, ushort field)
    {
        nint value = 0;
        short stringLength;
        short rc = Native.SQLColAttributeW(Handle, (ushort)ordinal, field, null, 0, &stringLength, &value);
        return Native.Succeeded(rc) ? (long)value : -1;
    }

    private string StringAttribute(int ordinal, ushort field)
    {
        const int Chars = 256;
        char* buffer = stackalloc char[Chars];
        short lengthBytes;
        nint numeric;
        short rc = Native.SQLColAttributeW(Handle, (ushort)ordinal, field, buffer, (short)(Chars * sizeof(char)), &lengthBytes, &numeric);
        if (!Native.Succeeded(rc)) return "";
        int n = lengthBytes <= 0 ? 0 : Math.Min(lengthBytes / sizeof(char), Chars - 1);
        var text = new string(buffer, 0, n);
        int nul = text.IndexOf('\0');
        return (nul >= 0 ? text[..nul] : text).Trim();
    }

    public void BindCol(int ordinal, short cType, void* buffer, nint elementBytes, nint* indicators)
    {
        short rc = Native.SQLBindCol(Handle, (ushort)ordinal, cType, buffer, elementBytes, indicators);
        Diag.Check(rc, $"SQLBindCol(column {ordinal})", Native.SQL_HANDLE_STMT, Handle, Info);
    }

    /// <summary>Raw SQLFetchScroll(SQL_FETCH_NEXT). The caller interprets the return code.</summary>
    public short FetchScroll() => Native.SQLFetchScroll(Handle, Native.SQL_FETCH_NEXT, 0);

    /// <summary>Raw SQLFetch. The caller interprets the return code.</summary>
    public short Fetch() => Native.SQLFetch(Handle);

    /// <summary>Raw SQLGetData. The caller interprets the return code.</summary>
    public short GetData(int ordinal, short cType, void* buffer, nint bufferBytes, nint* indicator)
        => Native.SQLGetData(Handle, (ushort)ordinal, cType, buffer, bufferBytes, indicator);

    /// <summary>SQLFreeStmt(SQL_CLOSE): closes the cursor, keeps bindings and attributes. No-op when no cursor is open.</summary>
    public void CloseCursor()
    {
        short rc = Native.SQLFreeStmt(Handle, Native.SQL_CLOSE);
        Diag.Check(rc, "SQLFreeStmt(SQL_CLOSE)", Native.SQL_HANDLE_STMT, Handle, Info);
    }

    /// <summary>Best-effort cursor close used on error paths; never throws.</summary>
    public void TryCloseCursor()
    {
        if (Handle != Native.SQL_NULL_HANDLE) Native.SQLFreeStmt(Handle, Native.SQL_CLOSE);
    }

    public void Unbind()
    {
        short rc = Native.SQLFreeStmt(Handle, Native.SQL_UNBIND);
        Diag.Check(rc, "SQLFreeStmt(SQL_UNBIND)", Native.SQL_HANDLE_STMT, Handle, Info);
    }

    /// <summary>Raw SQLMoreResults: SQL_SUCCESS[_WITH_INFO] means another result set exists, SQL_NO_DATA means none.</summary>
    public short MoreResults() => Native.SQLMoreResults(Handle);

    public List<OdbcDiagnostic> DrainDiagnostics(int maxRecords = 200) => Diag.Drain(Native.SQL_HANDLE_STMT, Handle, maxRecords);

    public void Dispose()
    {
        if (Handle == Native.SQL_NULL_HANDLE) return;
        Native.SQLFreeHandle(Native.SQL_HANDLE_STMT, Handle); // implicitly SQL_CLOSE + SQL_UNBIND
        Handle = Native.SQL_NULL_HANDLE;
    }
}
