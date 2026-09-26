namespace OdbcBench.Odbc;

/// <summary>ODBC environment handle: Driver Manager connection pooling explicitly OFF, ODBC 3.80 behaviour (3.0 fallback).</summary>
public sealed unsafe class OdbcEnvironment : IDisposable
{
    public nint Handle { get; private set; }
    public uint OdbcVersion { get; }
    public List<OdbcDiagnostic> Info { get; } = new();

    public OdbcEnvironment(uint odbcVersion = Native.SQL_OV_ODBC3_80)
    {
        // Process-wide, must happen before the first environment is allocated. The default is already OFF; being explicit
        // documents that no pooled connection can ever hide connect costs or reuse state between DSNs.
        Native.SQLSetEnvAttr(Native.SQL_NULL_HANDLE, Native.SQL_ATTR_CONNECTION_POOLING, (nint)Native.SQL_CP_OFF, Native.SQL_IS_UINTEGER);

        nint handle;
        short rc = Native.SQLAllocHandle(Native.SQL_HANDLE_ENV, Native.SQL_NULL_HANDLE, &handle);
        if (!Native.Succeeded(rc))
            throw new OdbcException("SQLAllocHandle(SQL_HANDLE_ENV)", rc, Array.Empty<OdbcDiagnostic>());
        Handle = handle;

        try
        {
            rc = Native.SQLSetEnvAttr(handle, Native.SQL_ATTR_ODBC_VERSION, (nint)odbcVersion, 0);
            if (!Native.Succeeded(rc) && odbcVersion != Native.SQL_OV_ODBC3)
            {
                Info.AddRange(Diag.Drain(Native.SQL_HANDLE_ENV, handle));
                odbcVersion = Native.SQL_OV_ODBC3;
                rc = Native.SQLSetEnvAttr(handle, Native.SQL_ATTR_ODBC_VERSION, (nint)odbcVersion, 0);
            }
            Diag.Check(rc, "SQLSetEnvAttr(SQL_ATTR_ODBC_VERSION)", Native.SQL_HANDLE_ENV, handle, Info);
            OdbcVersion = odbcVersion;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string OdbcVersionText => OdbcVersion switch
    {
        Native.SQL_OV_ODBC3_80 => "3.80",
        Native.SQL_OV_ODBC3 => "3.0",
        _ => OdbcVersion.ToString(),
    };

    public void Dispose()
    {
        if (Handle == Native.SQL_NULL_HANDLE) return;
        Native.SQLFreeHandle(Native.SQL_HANDLE_ENV, Handle);
        Handle = Native.SQL_NULL_HANDLE;
    }
}
