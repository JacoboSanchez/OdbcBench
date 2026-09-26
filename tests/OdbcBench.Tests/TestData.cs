using OdbcBench.Odbc;
using OdbcBench.Validation;

namespace OdbcBench.Tests;

internal static class TestData
{
    public static ColumnInfo Column(int ordinal, string name, short sqlType, ulong size = 0, short digits = 0,
        long displaySize = -1, long length = -1, long octetLength = -1, bool unsigned = false, string typeName = "") =>
        new()
        {
            Ordinal = ordinal,
            Name = name,
            SqlType = sqlType,
            ColumnSize = (nuint)size,
            DecimalDigits = digits,
            DisplaySize = displaySize,
            Length = length,
            OctetLength = octetLength,
            Unsigned = unsigned,
            TypeName = typeName,
        };

    /// <summary>A sample with columns (id INTEGER, name VARCHAR(20), amount DECIMAL(10,2), created TIMESTAMP).</summary>
    public static SampleResult Sample(string dsn, params string?[][] rows) => new()
    {
        DsnName = dsn,
        Columns = new List<ColumnInfo>
        {
            Column(1, "id", Native.SQL_INTEGER, 10),
            Column(2, "name", Native.SQL_VARCHAR, 20),
            Column(3, "amount", Native.SQL_DECIMAL, 10, 2),
            Column(4, "created", Native.SQL_TYPE_TIMESTAMP, 23, 3),
        },
        Rows = rows.ToList(),
    };

    public static string?[][] Rows(int count, int skip = -1)
    {
        var list = new List<string?[]>();
        for (int i = 1; list.Count < count; i++)
        {
            if (i == skip) continue;
            list.Add(new string?[] { i.ToString(), $"name {i}", $"{i}.50", $"2024-01-{i % 28 + 1:00} 10:00:00.000" });
        }
        return list.ToArray();
    }
}
