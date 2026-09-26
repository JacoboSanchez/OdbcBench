using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace OdbcBench.Odbc;

/// <summary>
/// Raw ODBC 3.x entry points of the Windows Driver Manager (odbc32.dll), Unicode (W) variants only.
/// Every ODBC function the tool ever calls is declared here, so the access path is explicit.
/// Type widths follow the x64 ABI: SQLLEN/SQLULEN are 8 bytes (nint/nuint), SQLINTEGER 4 (int),
/// SQLSMALLINT 2 (short), SQLRETURN 2 (short), handles and SQLPOINTER are pointer-sized (nint).
/// </summary>
internal static unsafe partial class Native
{
    private const string Dll = "odbc32.dll";

    // ---- return codes
    public const short SQL_SUCCESS = 0;
    public const short SQL_SUCCESS_WITH_INFO = 1;
    public const short SQL_STILL_EXECUTING = 2;
    public const short SQL_NEED_DATA = 99;
    public const short SQL_NO_DATA = 100;
    public const short SQL_ERROR = -1;
    public const short SQL_INVALID_HANDLE = -2;

    public static bool Succeeded(short rc) => rc == SQL_SUCCESS || rc == SQL_SUCCESS_WITH_INFO;

    // ---- handle types
    public const short SQL_HANDLE_ENV = 1;
    public const short SQL_HANDLE_DBC = 2;
    public const short SQL_HANDLE_STMT = 3;
    public const short SQL_HANDLE_DESC = 4;
    public const nint SQL_NULL_HANDLE = 0;

    // ---- environment attributes
    public const int SQL_ATTR_ODBC_VERSION = 200;
    public const int SQL_ATTR_CONNECTION_POOLING = 201;
    public const uint SQL_OV_ODBC3 = 3;
    public const uint SQL_OV_ODBC3_80 = 380;
    public const uint SQL_CP_OFF = 0;

    // ---- connection attributes
    public const int SQL_ATTR_AUTOCOMMIT = 102;
    public const int SQL_ATTR_LOGIN_TIMEOUT = 103;

    // ---- statement attributes
    public const int SQL_ATTR_QUERY_TIMEOUT = 0;
    public const int SQL_ATTR_MAX_ROWS = 1;
    public const int SQL_ATTR_ROW_BIND_TYPE = 5;
    public const int SQL_ATTR_CURSOR_TYPE = 6;
    public const int SQL_ATTR_CONCURRENCY = 7;
    public const int SQL_ATTR_ROW_STATUS_PTR = 25;
    public const int SQL_ATTR_ROWS_FETCHED_PTR = 26;
    public const int SQL_ATTR_ROW_ARRAY_SIZE = 27;
    public const uint SQL_CURSOR_FORWARD_ONLY = 0;
    public const uint SQL_CONCUR_READ_ONLY = 1;
    public const uint SQL_BIND_BY_COLUMN = 0;

    // ---- attribute length codes
    public const int SQL_IS_POINTER = -4;
    public const int SQL_IS_UINTEGER = -5;
    public const int SQL_IS_INTEGER = -6;

    // ---- fetch
    public const short SQL_FETCH_NEXT = 1;
    public const ushort SQL_ROW_SUCCESS = 0;
    public const ushort SQL_ROW_NOROW = 3;
    public const ushort SQL_ROW_ERROR = 5;
    public const ushort SQL_ROW_SUCCESS_WITH_INFO = 6;

    // ---- misc
    public const int SQL_NTS = -3;
    public const nint SQL_NULL_DATA = -1;
    public const nint SQL_NO_TOTAL = -4;
    public const ushort SQL_DRIVER_NOPROMPT = 0;
    public const ushort SQL_CLOSE = 0;
    public const ushort SQL_UNBIND = 2;
    public const ushort SQL_RESET_PARAMS = 3;
    public const short SQL_TRUE = 1;
    public const short SQL_FALSE = 0;

    // ---- SQLGetInfo information types
    public const ushort SQL_DATA_SOURCE_NAME = 2;
    public const ushort SQL_DRIVER_NAME = 6;
    public const ushort SQL_DRIVER_VER = 7;
    public const ushort SQL_SERVER_NAME = 13;
    public const ushort SQL_DBMS_NAME = 17;
    public const ushort SQL_DBMS_VER = 18;
    public const ushort SQL_DRIVER_HLIB = 76;
    public const ushort SQL_DRIVER_ODBC_VER = 77;
    public const ushort SQL_GETDATA_EXTENSIONS = 81;
    public const ushort SQL_DM_VER = 171;
    public const uint SQL_GD_ANY_COLUMN = 0x1;
    public const uint SQL_GD_ANY_ORDER = 0x2;
    public const uint SQL_GD_BLOCK = 0x4;
    public const uint SQL_GD_BOUND = 0x8;

    // ---- SQLColAttribute field identifiers
    public const ushort SQL_DESC_CONCISE_TYPE = 2;
    public const ushort SQL_DESC_DISPLAY_SIZE = 6;
    public const ushort SQL_DESC_UNSIGNED = 8;
    public const ushort SQL_DESC_TYPE_NAME = 14;
    public const ushort SQL_DESC_TYPE = 1002;
    public const ushort SQL_DESC_LENGTH = 1003;
    public const ushort SQL_DESC_PRECISION = 1005;
    public const ushort SQL_DESC_SCALE = 1006;
    public const ushort SQL_DESC_NULLABLE = 1008;
    public const ushort SQL_DESC_OCTET_LENGTH = 1013;

    // ---- diagnostic fields
    public const short SQL_DIAG_ROW_NUMBER = -1248;
    public const short SQL_DIAG_COLUMN_NUMBER = -1247;

    // ---- SQL data types (concise codes as returned by SQLDescribeCol)
    public const short SQL_UNKNOWN_TYPE = 0;
    public const short SQL_CHAR = 1;
    public const short SQL_NUMERIC = 2;
    public const short SQL_DECIMAL = 3;
    public const short SQL_INTEGER = 4;
    public const short SQL_SMALLINT = 5;
    public const short SQL_FLOAT = 6;
    public const short SQL_REAL = 7;
    public const short SQL_DOUBLE = 8;
    public const short SQL_DATE = 9;          // ODBC 2.x legacy code (also the SQL_DATETIME verbose type)
    public const short SQL_TIME = 10;         // ODBC 2.x legacy code
    public const short SQL_TIMESTAMP = 11;    // ODBC 2.x legacy code
    public const short SQL_VARCHAR = 12;
    public const short SQL_TYPE_DATE = 91;
    public const short SQL_TYPE_TIME = 92;
    public const short SQL_TYPE_TIMESTAMP = 93;
    public const short SQL_LONGVARCHAR = -1;
    public const short SQL_BINARY = -2;
    public const short SQL_VARBINARY = -3;
    public const short SQL_LONGVARBINARY = -4;
    public const short SQL_BIGINT = -5;
    public const short SQL_TINYINT = -6;
    public const short SQL_BIT = -7;
    public const short SQL_WCHAR = -8;
    public const short SQL_WVARCHAR = -9;
    public const short SQL_WLONGVARCHAR = -10;
    public const short SQL_GUID = -11;
    public const short SQL_INTERVAL_YEAR = 101;
    public const short SQL_INTERVAL_MINUTE_TO_SECOND = 113;
    // SQL Server driver specific codes (sqlncli.h / msodbcsql.h)
    public const short SQL_SS_VARIANT = -150;
    public const short SQL_SS_UDT = -151;
    public const short SQL_SS_XML = -152;
    public const short SQL_SS_TABLE = -153;
    public const short SQL_SS_TIME2 = -154;
    public const short SQL_SS_TIMESTAMPOFFSET = -155;

    // ---- C data types (SQL_C_*)
    public const short SQL_C_CHAR = 1;
    public const short SQL_C_WCHAR = -8;
    public const short SQL_C_SSHORT = -15;
    public const short SQL_C_USHORT = -17;
    public const short SQL_C_SLONG = -16;
    public const short SQL_C_ULONG = -18;
    public const short SQL_C_STINYINT = -26;
    public const short SQL_C_UTINYINT = -28;
    public const short SQL_C_SBIGINT = -25;
    public const short SQL_C_UBIGINT = -27;
    public const short SQL_C_FLOAT = 7;
    public const short SQL_C_DOUBLE = 8;
    public const short SQL_C_BIT = -7;
    public const short SQL_C_BINARY = -2;
    public const short SQL_C_TYPE_DATE = 91;
    public const short SQL_C_TYPE_TIME = 92;
    public const short SQL_C_TYPE_TIMESTAMP = 93;
    public const short SQL_C_GUID = -11;
    public const short SQL_C_NUMERIC = 2;
    public const short SQL_C_DEFAULT = 99;

    // ---- entry points (all arguments blittable: no marshalling stubs, no allocations per call)

    [LibraryImport(Dll)]
    public static partial short SQLAllocHandle(short handleType, nint inputHandle, nint* outputHandle);

    [LibraryImport(Dll)]
    public static partial short SQLFreeHandle(short handleType, nint handle);

    [LibraryImport(Dll)]
    public static partial short SQLSetEnvAttr(nint environment, int attribute, nint value, int stringLength);

    [LibraryImport(Dll)]
    public static partial short SQLSetConnectAttrW(nint connection, int attribute, nint value, int stringLength);

    /// <remarks>inLength and outBufferChars are in CHARACTERS.</remarks>
    [LibraryImport(Dll)]
    public static partial short SQLDriverConnectW(nint connection, nint windowHandle, char* inConnectionString, short inLength,
        char* outConnectionString, short outBufferChars, short* outLength, ushort driverCompletion);

    [LibraryImport(Dll)]
    public static partial short SQLDisconnect(nint connection);

    /// <remarks>bufferLength and stringLength are in BYTES.</remarks>
    [LibraryImport(Dll)]
    public static partial short SQLGetInfoW(nint connection, ushort infoType, void* infoValue, short bufferLength, short* stringLength);

    [LibraryImport(Dll)]
    public static partial short SQLExecDirectW(nint statement, char* statementText, int textLength);

    [LibraryImport(Dll)]
    public static partial short SQLNumResultCols(nint statement, short* columnCount);

    /// <remarks>nameBufferChars is in CHARACTERS; columnSize is SQLULEN (8 bytes on x64).</remarks>
    [LibraryImport(Dll)]
    public static partial short SQLDescribeColW(nint statement, ushort columnNumber, char* columnName, short nameBufferChars, short* nameLength,
        short* dataType, nuint* columnSize, short* decimalDigits, short* nullable);

    /// <remarks>bufferLength and stringLength are in BYTES; numericAttribute is SQLLEN (8 bytes on x64).</remarks>
    [LibraryImport(Dll)]
    public static partial short SQLColAttributeW(nint statement, ushort columnNumber, ushort fieldIdentifier, void* characterAttribute,
        short bufferLength, short* stringLength, nint* numericAttribute);

    [LibraryImport(Dll)]
    public static partial short SQLSetStmtAttrW(nint statement, int attribute, nint value, int stringLength);

    [LibraryImport(Dll)]
    public static partial short SQLGetStmtAttrW(nint statement, int attribute, void* value, int bufferLength, int* stringLength);

    /// <remarks>bufferLength is in BYTES (the stride of column-wise arrays); indicators are SQLLEN (8 bytes each).</remarks>
    [LibraryImport(Dll)]
    public static partial short SQLBindCol(nint statement, ushort columnNumber, short targetType, void* targetValue, nint bufferLength, nint* strLenOrInd);

    [LibraryImport(Dll)]
    public static partial short SQLFetchScroll(nint statement, short fetchOrientation, nint fetchOffset);

    [LibraryImport(Dll)]
    public static partial short SQLFetch(nint statement);

    [LibraryImport(Dll)]
    public static partial short SQLGetData(nint statement, ushort columnNumber, short targetType, void* targetValue, nint bufferLength, nint* strLenOrInd);

    [LibraryImport(Dll)]
    public static partial short SQLFreeStmt(nint statement, ushort option);

    [LibraryImport(Dll)]
    public static partial short SQLMoreResults(nint statement);

    /// <remarks>messageBufferChars is in CHARACTERS.</remarks>
    [LibraryImport(Dll)]
    public static partial short SQLGetDiagRecW(short handleType, nint handle, short recordNumber, char* sqlState, int* nativeError,
        char* messageText, short messageBufferChars, short* textLength);

    [LibraryImport(Dll)]
    public static partial short SQLGetDiagFieldW(short handleType, nint handle, short recordNumber, short diagIdentifier, void* diagInfo,
        short bufferLength, short* stringLength);
}
