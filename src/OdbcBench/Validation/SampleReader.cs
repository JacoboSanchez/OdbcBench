using OdbcBench.Fetch;
using OdbcBench.Odbc;

namespace OdbcBench.Validation;

/// <summary>The first N rows of the query on one DSN, as text, plus the described metadata.</summary>
public sealed class SampleResult
{
    public string DsnName { get; init; } = "";
    public List<ColumnInfo> Columns { get; init; } = new();
    public List<string?[]> Rows { get; init; } = new();
    /// <summary>True when SQLMoreResults reported a second result set.</summary>
    public bool MoreResults { get; init; }
    public bool Ok { get; init; } = true;
    public string? Error { get; init; }
    public List<string> Info { get; init; } = new();
}

/// <summary>
/// Fetches the validation sample with the most portable path: row array size 1, SQLFetch, and one chunked SQLGetData
/// per value as SQL_C_WCHAR. Independent of the block-fetch code, so a binding bug cannot mask a data difference.
/// </summary>
public static class SampleReader
{
    public static SampleResult Read(OdbcConnection connection, string dsnName, string query, int maxRows, int queryTimeoutSeconds)
    {
        var columns = new List<ColumnInfo>();
        var rows = new List<string?[]>();
        var info = new List<string>();
        try
        {
            using var statement = new OdbcStatement(connection);
            TrySet(statement, Native.SQL_ATTR_CURSOR_TYPE, Native.SQL_CURSOR_FORWARD_ONLY, "SQL_ATTR_CURSOR_TYPE");
            TrySet(statement, Native.SQL_ATTR_CONCURRENCY, Native.SQL_CONCUR_READ_ONLY, "SQL_ATTR_CONCURRENCY");
            if (queryTimeoutSeconds > 0)
                TrySet(statement, Native.SQL_ATTR_QUERY_TIMEOUT, (nuint)queryTimeoutSeconds, "SQL_ATTR_QUERY_TIMEOUT");

            short rc = statement.ExecDirect(query);
            if (rc == Native.SQL_NO_DATA)
                return new SampleResult { DsnName = dsnName, Ok = false, Error = "the statement produced no result set (SQL_NO_DATA)", Info = info };

            short count = statement.NumResultCols();
            for (int i = 1; i <= count; i++) columns.Add(statement.DescribeColumn(i));

            using var getData = new GetDataReader();
            while (rows.Count < maxRows)
            {
                rc = statement.Fetch();
                if (rc == Native.SQL_NO_DATA) break;
                if (rc == Native.SQL_ERROR || rc == Native.SQL_INVALID_HANDLE)
                    throw new OdbcException("SQLFetch", rc, statement.DrainDiagnostics());
                if (rc == Native.SQL_SUCCESS_WITH_INFO)
                    foreach (var d in statement.DrainDiagnostics()) info.Add(d.ToString());

                var row = new string?[count];
                for (int i = 1; i <= count; i++) row[i - 1] = getData.ReadText(statement, i);
                rows.Add(row);
            }

            short more = statement.MoreResults();
            bool moreResults = more == Native.SQL_SUCCESS || more == Native.SQL_SUCCESS_WITH_INFO;
            statement.TryCloseCursor();

            foreach (var d in statement.Info) info.Add(d.ToString());
            return new SampleResult { DsnName = dsnName, Columns = columns, Rows = rows, MoreResults = moreResults, Info = info };
        }
        catch (OdbcException ex)
        {
            return new SampleResult { DsnName = dsnName, Columns = columns, Rows = rows, Ok = false, Error = ex.Message, Info = info };
        }
        catch (Exception ex)
        {
            return new SampleResult { DsnName = dsnName, Columns = columns, Rows = rows, Ok = false, Error = ex.Message, Info = info };
        }
    }

    private static void TrySet(OdbcStatement statement, int attribute, nuint value, string name)
    {
        try
        {
            statement.SetUIntAttribute(attribute, value, name);
        }
        catch (OdbcException)
        {
            // optional attribute; the sample fetch works without it
        }
    }
}
