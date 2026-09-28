using System.Runtime.InteropServices;

namespace OdbcBench.Odbc;

/// <summary>Driver, DBMS and Driver Manager identity read through SQLGetInfoW after connecting.</summary>
public sealed record DriverInfo(
    string DriverName,
    string DriverVersion,
    string DriverOdbcVersion,
    string DbmsName,
    string DbmsVersion,
    string ServerName,
    string DataSourceName,
    string DriverManagerVersion,
    uint GetDataExtensions,
    bool? UnicodeNative)
{
    public bool SupportsGetDataAnyColumn => (GetDataExtensions & Native.SQL_GD_ANY_COLUMN) != 0;
    public bool SupportsGetDataAnyOrder => (GetDataExtensions & Native.SQL_GD_ANY_ORDER) != 0;
    public bool SupportsGetDataBlock => (GetDataExtensions & Native.SQL_GD_BLOCK) != 0;
    public bool SupportsGetDataBound => (GetDataExtensions & Native.SQL_GD_BOUND) != 0;

    public string GetDataExtensionsText
    {
        get
        {
            var parts = new List<string>(4);
            if (SupportsGetDataAnyColumn) parts.Add("ANY_COLUMN");
            if (SupportsGetDataAnyOrder) parts.Add("ANY_ORDER");
            if (SupportsGetDataBlock) parts.Add("BLOCK");
            if (SupportsGetDataBound) parts.Add("BOUND");
            return parts.Count == 0 ? "none" : string.Join("|", parts);
        }
    }
}

/// <summary>
/// An ODBC connection handle opened with SQLDriverConnectW.
/// The completed connection string returned by the driver is wiped immediately: it can contain the password.
/// </summary>
public sealed unsafe class OdbcConnection : IDisposable
{
    public nint Handle { get; private set; }
    public bool IsConnected { get; private set; }
    public DriverInfo? Driver { get; private set; }
    /// <summary>SQL_SUCCESS_WITH_INFO records collected while connecting (context changes, unknown attributes, ...).</summary>
    public List<OdbcDiagnostic> Info { get; } = new();

    private OdbcConnection(nint handle) => Handle = handle;

    public static OdbcConnection Open(OdbcEnvironment environment, string connectionString, int loginTimeoutSeconds, bool readDriverInfo = true)
    {
        nint handle;
        short rc = Native.SQLAllocHandle(Native.SQL_HANDLE_DBC, environment.Handle, &handle);
        Diag.Check(rc, "SQLAllocHandle(SQL_HANDLE_DBC)", Native.SQL_HANDLE_ENV, environment.Handle);

        var connection = new OdbcConnection(handle);
        try
        {
            if (loginTimeoutSeconds > 0)
            {
                rc = Native.SQLSetConnectAttrW(handle, Native.SQL_ATTR_LOGIN_TIMEOUT, (nint)loginTimeoutSeconds, Native.SQL_IS_UINTEGER);
                Diag.Check(rc, "SQLSetConnectAttrW(SQL_ATTR_LOGIN_TIMEOUT)", Native.SQL_HANDLE_DBC, handle, connection.Info);
            }

            const int OutChars = 4096;
            char* outBuffer = (char*)NativeMemory.AllocZeroed((nuint)(OutChars * sizeof(char)));
            try
            {
                short outLength;
                fixed (char* input = connectionString)
                {
                    rc = Native.SQLDriverConnectW(handle, 0, input, Native.SQL_NTS, outBuffer, OutChars, &outLength, Native.SQL_DRIVER_NOPROMPT);
                }
                Diag.Check(rc, "SQLDriverConnectW", Native.SQL_HANDLE_DBC, handle, connection.Info);
            }
            finally
            {
                NativeMemory.Clear(outBuffer, (nuint)(OutChars * sizeof(char))); // wipe: may contain PWD=
                NativeMemory.Free(outBuffer);
            }
            connection.IsConnected = true;

            if (readDriverInfo)
                connection.Driver = connection.ReadDriverInfo();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public DriverInfo ReadDriverInfo()
    {
        bool? unicodeNative = null;
        nint driverModule = GetInfoPointer(Native.SQL_DRIVER_HLIB);
        if (driverModule != 0)
        {
            // The Driver Manager's own criterion for a Unicode driver: it exports SQLConnectW.
            unicodeNative = NativeLibrary.TryGetExport(driverModule, "SQLConnectW", out _)
                            || NativeLibrary.TryGetExport(driverModule, "SQLDriverConnectW", out _);
        }

        var info = new DriverInfo(
            DriverName: GetInfoString(Native.SQL_DRIVER_NAME),
            DriverVersion: GetInfoString(Native.SQL_DRIVER_VER),
            DriverOdbcVersion: GetInfoString(Native.SQL_DRIVER_ODBC_VER),
            DbmsName: GetInfoString(Native.SQL_DBMS_NAME),
            DbmsVersion: GetInfoString(Native.SQL_DBMS_VER),
            ServerName: GetInfoString(Native.SQL_SERVER_NAME),
            DataSourceName: GetInfoString(Native.SQL_DATA_SOURCE_NAME),
            DriverManagerVersion: GetInfoString(Native.SQL_DM_VER),
            GetDataExtensions: GetInfoUInt(Native.SQL_GETDATA_EXTENSIONS),
            UnicodeNative: unicodeNative);
        Driver = info;
        return info;
    }

    /// <summary>Reuses identity already read on another connection to the same DSN (keeps SQLGetInfo out of timed connects).</summary>
    public void UseDriverInfo(DriverInfo? info) => Driver = info;

    public string GetInfoString(ushort infoType)
    {
        const int Chars = 1024;
        char* buffer = stackalloc char[Chars];
        short lengthBytes;
        short rc = Native.SQLGetInfoW(Handle, infoType, buffer, (short)(Chars * sizeof(char)), &lengthBytes);
        if (!Native.Succeeded(rc)) return "";
        int n = lengthBytes <= 0 ? 0 : Math.Min(lengthBytes / sizeof(char), Chars - 1);
        var text = new string(buffer, 0, n);
        int nul = text.IndexOf('\0');
        return (nul >= 0 ? text[..nul] : text).Trim();
    }

    public uint GetInfoUInt(ushort infoType)
    {
        uint value = 0;
        short length;
        short rc = Native.SQLGetInfoW(Handle, infoType, &value, sizeof(uint), &length);
        return Native.Succeeded(rc) ? value : 0;
    }

    public nint GetInfoPointer(ushort infoType)
    {
        nint value = 0;
        short length;
        short rc = Native.SQLGetInfoW(Handle, infoType, &value, (short)sizeof(nint), &length);
        return Native.Succeeded(rc) ? value : 0;
    }

    /// <summary>False after <see cref="TrySetManualCommit"/> succeeded: transactions end with <see cref="Commit"/> or <see cref="Rollback"/>.</summary>
    public bool AutoCommit { get; private set; } = true;
    /// <summary>Why the driver refused to turn autocommit off; null when it was never refused.</summary>
    public string? ManualCommitRefusal { get; private set; }

    /// <summary>Turns autocommit off. Returns false, and remembers why, when the driver does not support transactions.</summary>
    public bool TrySetManualCommit()
    {
        if (!AutoCommit) return true;
        if (ManualCommitRefusal != null) return false;
        short rc = Native.SQLSetConnectAttrW(Handle, Native.SQL_ATTR_AUTOCOMMIT, (nint)Native.SQL_AUTOCOMMIT_OFF, Native.SQL_IS_UINTEGER);
        if (Native.Succeeded(rc))
        {
            AutoCommit = false;
            return true;
        }
        var diagnostics = Diag.Drain(Native.SQL_HANDLE_DBC, Handle);
        ManualCommitRefusal = diagnostics.Count > 0 ? $"[{diagnostics[0].SqlState}] {diagnostics[0].Message}" : OdbcException.ReturnCodeName(rc);
        return false;
    }

    /// <summary>Raw SQLEndTran(SQL_COMMIT). The caller interprets the return code.</summary>
    public short EndTransaction(short completionType) => Native.SQLEndTran(Native.SQL_HANDLE_DBC, Handle, completionType);

    public void Commit()
    {
        short rc = EndTransaction(Native.SQL_COMMIT);
        Diag.Check(rc, "SQLEndTran(SQL_COMMIT)", Native.SQL_HANDLE_DBC, Handle, Info);
    }

    /// <summary>Best-effort rollback used on error paths and before disconnecting; never throws.</summary>
    public void TryRollback()
    {
        if (Handle != Native.SQL_NULL_HANDLE && IsConnected && !AutoCommit) EndTransaction(Native.SQL_ROLLBACK);
    }

    public void Disconnect()
    {
        if (!IsConnected) return;
        TryRollback(); // SQLDisconnect fails with 25000 while a transaction is open
        short rc = Native.SQLDisconnect(Handle);
        IsConnected = false;
        Diag.Check(rc, "SQLDisconnect", Native.SQL_HANDLE_DBC, Handle, Info);
    }

    public void Dispose()
    {
        if (Handle == Native.SQL_NULL_HANDLE) return;
        if (IsConnected)
        {
            TryRollback();
            Native.SQLDisconnect(Handle);
            IsConnected = false;
        }
        Native.SQLFreeHandle(Native.SQL_HANDLE_DBC, Handle);
        Handle = Native.SQL_NULL_HANDLE;
    }
}
