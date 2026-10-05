using OdbcBench.Insert;
using OdbcBench.Odbc;
using static OdbcBench.Tests.TestData;

namespace OdbcBench.Tests;

public class InsertTests
{
    private static InsertColumn Map(ColumnInfo c, BindMode mode = BindMode.Native, int valueLength = 32) =>
        ParameterMapper.Map(c, c.Name, mode, valueLength) ?? throw new InvalidOperationException($"{c.SqlTypeName} was not mapped");

    // ---- parameter mapping

    [Theory]
    [InlineData(Native.SQL_TINYINT, Native.SQL_C_STINYINT, 1)]
    [InlineData(Native.SQL_SMALLINT, Native.SQL_C_SSHORT, 2)]
    [InlineData(Native.SQL_INTEGER, Native.SQL_C_SLONG, 4)]
    [InlineData(Native.SQL_BIGINT, Native.SQL_C_SBIGINT, 8)]
    [InlineData(Native.SQL_REAL, Native.SQL_C_FLOAT, 4)]
    [InlineData(Native.SQL_FLOAT, Native.SQL_C_DOUBLE, 8)]
    [InlineData(Native.SQL_DOUBLE, Native.SQL_C_DOUBLE, 8)]
    [InlineData(Native.SQL_BIT, Native.SQL_C_BIT, 1)]
    [InlineData(Native.SQL_TYPE_DATE, Native.SQL_C_TYPE_DATE, 6)]
    [InlineData(Native.SQL_TYPE_TIME, Native.SQL_C_TYPE_TIME, 6)]
    [InlineData(Native.SQL_TYPE_TIMESTAMP, Native.SQL_C_TYPE_TIMESTAMP, 16)]
    [InlineData(Native.SQL_GUID, Native.SQL_C_GUID, 16)]
    public void Fixed_width_parameters_bind_natively(short sqlType, short cType, int bytes)
    {
        var c = Map(Column(1, "c", sqlType, 10));
        Assert.False(c.AsText);
        Assert.Equal(cType, c.Column.CType);
        Assert.Equal(bytes, c.Column.ElementBytes);
    }

    [Fact]
    public void Text_is_sized_by_the_value_length_capped_at_the_column_size()
    {
        Assert.Equal((32 + 1) * 2, Map(Column(1, "c", Native.SQL_VARCHAR, 40)).Column.ElementBytes);
        Assert.Equal((8 + 1) * 2, Map(Column(1, "c", Native.SQL_WCHAR, 8)).Column.ElementBytes);
        var lob = Map(Column(1, "c", Native.SQL_WLONGVARCHAR, 0), valueLength: 100); // (max) types: no size
        Assert.Equal(100, lob.Length);
        Assert.False(lob.Column.IsLong);
    }

    [Fact]
    public void Decimals_and_fractional_times_are_sent_as_text()
    {
        var d = Map(Column(1, "d", Native.SQL_DECIMAL, 10, 2));
        Assert.True(d.AsText);
        Assert.Equal((8 + 1 + 2 + 1) * 2, d.Column.ElementBytes);
        Assert.True(Map(Column(1, "t", Native.SQL_TYPE_TIME, 16, 7)).AsText);
        Assert.Equal(ValueKind.TimestampOffset, Map(Column(1, "o", Native.SQL_SS_TIMESTAMPOFFSET, 34, 7)).Kind);
    }

    [Fact]
    public void Boolean_described_as_text_is_sent_as_true_or_false()
    {
        var b = Map(Column(1, "flag", Native.SQL_VARCHAR, 5, typeName: "bool"));
        Assert.Equal(ValueKind.Bit, b.Kind);
        Assert.True(b.AsText);
        Assert.Equal("true", DataGenerator.Text(b, 1));
        Assert.Equal("false", DataGenerator.Text(b, 2));
        var (length, bytes) = Write(b, 2);
        Assert.Equal("false", System.Text.Encoding.Unicode.GetString(bytes, 0, length));
        Assert.Equal((5 + 1) * 2, b.Column.ElementBytes);
    }

    [Fact]
    public void Bit_sent_as_text_in_wchar_mode_stays_0_or_1()
    {
        var b = Map(Column(1, "flag", Native.SQL_BIT, 1), BindMode.WChar);
        Assert.True(b.AsText);
        Assert.Equal("1", DataGenerator.Text(b, 1));
        Assert.Equal("0", DataGenerator.Text(b, 2));
    }

    [Fact]
    public void Boolean_described_as_text_compares_as_bool()
    {
        Assert.Equal(TypeFamily.Bool, Column(1, "flag", Native.SQL_VARCHAR, 5, typeName: "bool").Family);
        Assert.Equal(TypeFamily.Text, Column(1, "flag", Native.SQL_VARCHAR, 5, typeName: "varchar").Family);

        var target = new InsertTarget("t", null, BindMode.Native, 16, CleanupMode.Delete);
        target.Build(new[] { Column(1, "id", Native.SQL_INTEGER, 10), Column(2, "flag", Native.SQL_VARCHAR, 5, typeName: "bool") }, new HashSet<int>(), "\"");
        var sent = target.SentSample(2);
        var readBack = Sample("psql", new List<ColumnInfo> { Column(1, "id", Native.SQL_INTEGER, 10), Column(2, "flag", Native.SQL_VARCHAR, 5, typeName: "bool") },
            new string?[] { "1", "1" }, new string?[] { "2", "0" }); // psqlODBC's BoolsAsChar reads true back as 1
        var result = OdbcBench.Validation.ResultComparer.Compare(new[] { sent, readBack }, target.ReadBackSql, new OdbcBench.Validation.NormalizationOptions());
        Assert.Equal("PASS", result.Status);
    }

    [Fact]
    public void Logical_types_pick_the_values_when_the_description_loses_them()
    {
        // How Oracle describes the wide shape: DATE as a timestamp, NUMBER(5) and NUMBER(1) as decimals.
        var described = new[]
        {
            Column(1, "CATEGORY", Native.SQL_DECIMAL, 5),
            Column(2, "FLAG", Native.SQL_DECIMAL, 1),
            Column(3, "BUSINESS_DATE", Native.SQL_TYPE_TIMESTAMP, 19),
            Column(4, "AMOUNT", Native.SQL_DECIMAL, 18, 4),
        };
        var logical = new Dictionary<string, short>(StringComparer.OrdinalIgnoreCase)
        {
            ["category"] = Native.SQL_SMALLINT, ["flag"] = Native.SQL_BIT, ["business_date"] = Native.SQL_TYPE_DATE, ["amount"] = Native.SQL_DECIMAL,
        };
        var target = new InsertTarget("t", null, BindMode.Native, 16, CleanupMode.None, logical);
        target.Build(described, new HashSet<int>(), "\"");

        var c = target.Columns;
        Assert.Equal((ValueKind.Integer, 1L << 15), (c[0].Kind, c[0].Modulus));
        Assert.Equal(ValueKind.Bit, c[1].Kind);
        Assert.Equal(ValueKind.Date, c[2].Kind);
        Assert.Equal((ValueKind.Decimal, 4), (c[3].Kind, c[3].Scale));
        // The binding keeps the described SQL type; only the C type follows the logical values.
        Assert.Equal(Native.SQL_TYPE_TIMESTAMP, c[2].Column.SqlType);
        Assert.Equal(Native.SQL_C_TYPE_DATE, c[2].Column.CType);

        // Without logical types, the description decides as before.
        var plain = new InsertTarget("t", null, BindMode.Native, 16, CleanupMode.None);
        plain.Build(new[] { Column(1, "FLAG", Native.SQL_DECIMAL, 1), Column(2, "BUSINESS_DATE", Native.SQL_TYPE_TIMESTAMP, 19) }, new HashSet<int>(), "\"");
        Assert.Equal(new[] { ValueKind.Decimal, ValueKind.Timestamp }, plain.Columns.Select(x => x.Kind));
    }

    private static OdbcBench.Validation.SampleResult Sample(string dsn, List<ColumnInfo> columns, params string?[][] rows) =>
        new() { DsnName = dsn, Columns = columns, Rows = rows.ToList() };

    [Fact]
    public void Wchar_mode_sends_everything_but_binary_as_text()
    {
        var i = Map(Column(1, "i", Native.SQL_INTEGER, 10), BindMode.WChar);
        Assert.True(i.AsText);
        Assert.Equal(Native.SQL_C_WCHAR, i.Column.CType);
        var b = Map(Column(2, "b", Native.SQL_VARBINARY, 16), BindMode.WChar);
        Assert.False(b.AsText);
        Assert.Equal(Native.SQL_C_BINARY, b.Column.CType);
    }

    [Fact]
    public void Types_without_generated_values_are_not_mapped()
    {
        Assert.Null(ParameterMapper.Map(Column(1, "x", Native.SQL_SS_XML, 0), "x", BindMode.Native, 32));
        Assert.Null(ParameterMapper.Map(Column(1, "i", Native.SQL_INTERVAL_YEAR, 0), "i", BindMode.Native, 32));
    }

    [Fact]
    public void Text_fallback_after_a_rejected_binding()
    {
        var g = Map(Column(1, "g", Native.SQL_GUID, 36));
        Assert.True(ParameterMapper.ForceText(g));
        Assert.True(g.Column.FellBackToText);
        Assert.Equal((36 + 1) * 2, g.Column.ElementBytes);
        Assert.False(ParameterMapper.ForceText(g));                               // already text
        Assert.False(ParameterMapper.ForceText(Map(Column(1, "b", Native.SQL_BINARY, 4)))); // binary has no text form to send
    }

    // ---- generated values

    private static unsafe (int Length, byte[] Bytes) Write(InsertColumn column, long row)
    {
        var buffer = new byte[column.Column.ElementBytes];
        fixed (byte* p = buffer)
        {
            int n = DataGenerator.Write(column, row, p);
            return (n, buffer);
        }
    }

    [Fact]
    public void Integers_follow_the_row_number_within_the_type_range()
    {
        var i = Map(Column(1, "i", Native.SQL_INTEGER, 10));
        Assert.Equal(12345, BitConverter.ToInt32(Write(i, 12345).Bytes));
        Assert.Equal(3, BitConverter.ToInt32(Write(i, (1L << 31) + 3).Bytes));
        var s = Map(Column(1, "s", Native.SQL_SMALLINT, 5));
        Assert.Equal(40000 % 32768, BitConverter.ToInt16(Write(s, 40000).Bytes));
        Assert.Equal("7232", DataGenerator.Text(s, 40000));
        var big = Map(Column(1, "b", Native.SQL_BIGINT, 19));
        Assert.Equal(5_000_000_000L, BitConverter.ToInt64(Write(big, 5_000_000_000L).Bytes));
    }

    [Fact]
    public void Floats_are_exact_quarters()
    {
        var d = Map(Column(1, "d", Native.SQL_DOUBLE, 15));
        Assert.Equal(0.75, BitConverter.ToDouble(Write(d, 3).Bytes));
        Assert.Equal("0.75", DataGenerator.Text(d, 3));
        Assert.Equal("1.00", DataGenerator.Text(d, 4));
        var r = Map(Column(1, "r", Native.SQL_REAL, 7));
        Assert.Equal(1.25f, BitConverter.ToSingle(Write(r, 5).Bytes));
    }

    [Theory]
    [InlineData(10, 2, 123, "123.23")]
    [InlineData(5, 0, 123456, "23456")]
    [InlineData(4, 4, 7, "0.0700")]
    [InlineData(19, 4, 1, "1.0100")]
    public void Decimals_fit_precision_and_scale(ulong precision, short scale, long row, string expected)
    {
        var d = Map(Column(1, "d", Native.SQL_DECIMAL, precision, scale));
        Assert.Equal(expected, DataGenerator.Text(d, row));
        Assert.True(expected.Length <= d.TextChars);
        var (length, bytes) = Write(d, row);
        Assert.Equal(expected, System.Text.Encoding.Unicode.GetString(bytes, 0, length));
    }

    [Fact]
    public void Dates_times_and_timestamps_match_their_text()
    {
        var date = Map(Column(1, "d", Native.SQL_TYPE_DATE, 10));
        var (_, d) = Write(date, 1);
        Assert.Equal((2000, 1, 2), (BitConverter.ToInt16(d, 0), BitConverter.ToUInt16(d, 2), BitConverter.ToUInt16(d, 4)));
        Assert.Equal("2000-01-02", DataGenerator.Text(date, 1));

        var time = Map(Column(1, "t", Native.SQL_TYPE_TIME, 8));
        var (_, t) = Write(time, 3661);
        Assert.Equal((1, 1, 1), (BitConverter.ToUInt16(t, 0), BitConverter.ToUInt16(t, 2), BitConverter.ToUInt16(t, 4)));
        Assert.Equal("01:01:01", DataGenerator.Text(time, 3661));

        var stamp = Map(Column(1, "s", Native.SQL_TYPE_TIMESTAMP, 23, 3));
        var (length, s) = Write(stamp, 86_401);
        Assert.Equal(16, length);
        Assert.Equal((2000, 1, 2, 0, 0, 1, 0u),
            (BitConverter.ToInt16(s, 0), BitConverter.ToUInt16(s, 2), BitConverter.ToUInt16(s, 4),
             BitConverter.ToUInt16(s, 6), BitConverter.ToUInt16(s, 8), BitConverter.ToUInt16(s, 10), BitConverter.ToUInt32(s, 12)));
        Assert.Equal("2000-01-02 00:00:01", DataGenerator.Text(stamp, 86_401));

        var offset = Map(Column(1, "o", Native.SQL_SS_TIMESTAMPOFFSET, 34, 7));
        Assert.Equal("2000-01-02 00:00:01 +00:00", DataGenerator.Text(offset, 86_401));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(123_456_789_012L)]
    public void Guid_bytes_and_text_are_the_same_value(long row)
    {
        var g = Map(Column(1, "g", Native.SQL_GUID, 36));
        var guid = new Guid(Write(g, row).Bytes); // SQLGUID and System.Guid share the memory layout
        string text = DataGenerator.Text(g, row);
        Assert.Equal(guid.ToString("D"), text);
        Assert.Equal('4', text[14]);                // version 4
        Assert.Contains(text[19], "89ab");          // RFC 4122 variant
    }

    [Fact]
    public void Binary_holds_the_row_number_big_endian()
    {
        var b = Map(Column(1, "b", Native.SQL_BINARY, 4));
        var (length, bytes) = Write(b, 0x01020304);
        Assert.Equal(4, length);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes);
        Assert.Equal("01020304", DataGenerator.Text(b, 0x01020304));
    }

    [Fact]
    public void Text_starts_with_the_zero_padded_row_number()
    {
        var v = Map(Column(1, "v", Native.SQL_VARCHAR, 40));
        string text = DataGenerator.Text(v, 5);
        Assert.Equal(32, text.Length);
        Assert.StartsWith("0000000005-", text);
        var (length, bytes) = Write(v, 5);
        Assert.Equal(64, length);
        Assert.Equal(text, System.Text.Encoding.Unicode.GetString(bytes, 0, length));
        Assert.Equal('\0', BitConverter.ToChar(bytes, length)); // terminated
        Assert.Equal("234", DataGenerator.Text(Map(Column(1, "c", Native.SQL_CHAR, 3)), 1234));
    }

    [Fact]
    public void Values_depend_only_on_the_column_and_the_row()
    {
        var c = Map(Column(1, "v", Native.SQL_WVARCHAR, 20));
        Assert.Equal(DataGenerator.Text(c, 42), DataGenerator.Text(Map(Column(7, "other", Native.SQL_WVARCHAR, 20)), 42));
        Assert.NotEqual(DataGenerator.Text(c, 42), DataGenerator.Text(c, 43));
    }

    // ---- target table

    private static InsertTarget Target(IReadOnlyList<string>? columns = null, CleanupMode cleanup = CleanupMode.Delete) =>
        new("dbo.orders", columns, BindMode.Native, 16, cleanup);

    [Fact]
    public void Insert_statement_quotes_described_names_and_skips_auto_increment()
    {
        var target = Target();
        target.Build(new[]
        {
            Column(1, "row_id", Native.SQL_INTEGER, 10),
            Column(2, "id", Native.SQL_BIGINT, 19),
            Column(3, "customer", Native.SQL_WVARCHAR, 40),
            Column(4, "doc", Native.SQL_SS_XML, 0),
        }, new HashSet<int> { 1 }, "\"");

        Assert.Equal("INSERT INTO dbo.orders (\"id\", \"customer\") VALUES (?, ?)", target.InsertSql);
        Assert.Equal("SELECT \"id\", \"customer\" FROM dbo.orders ORDER BY \"id\"", target.ReadBackSql);
        Assert.Contains(target.Notes, n => n.Contains("'row_id' is auto-increment"));
        Assert.Contains(target.Notes, n => n.Contains("'doc' (SS_XML)"));
    }

    [Fact]
    public void Configured_columns_are_used_verbatim()
    {
        var target = Target(new[] { "[Name]", "[Id]" });
        target.Build(new[] { Column(1, "Name", Native.SQL_VARCHAR, 20), Column(2, "Id", Native.SQL_INTEGER, 10) }, new HashSet<int>(), "\"");
        Assert.Equal("INSERT INTO dbo.orders ([Name], [Id]) VALUES (?, ?)", target.InsertSql);
        Assert.Equal("SELECT [Name], [Id] FROM dbo.orders ORDER BY [Id]", target.ReadBackSql);
        Assert.Equal("SELECT [Name], [Id] FROM dbo.orders WHERE 1 = 0", target.DescribeSql);
    }

    [Fact]
    public void Configured_column_without_generated_values_is_an_error()
    {
        var target = Target(new[] { "doc" });
        Assert.Throws<NotSupportedException>(() => target.Build(new[] { Column(1, "doc", Native.SQL_SS_XML, 0) }, new HashSet<int>(), "\""));
    }

    [Fact]
    public void A_driver_without_identifier_quotes_gets_bare_names()
    {
        var target = Target();
        target.Build(new[] { Column(1, "id", Native.SQL_INTEGER, 10) }, new HashSet<int>(), " ");
        Assert.Equal("INSERT INTO dbo.orders (id) VALUES (?)", target.InsertSql);
    }

    [Theory]
    [InlineData(CleanupMode.None, null)]
    [InlineData(CleanupMode.Delete, "DELETE FROM dbo.orders")]
    [InlineData(CleanupMode.Truncate, "TRUNCATE TABLE dbo.orders")]
    public void Cleanup_statement_follows_the_mode(CleanupMode mode, string? expected)
    {
        Assert.Equal(expected, Target(cleanup: mode).CleanupSql);
    }

    [Fact]
    public void Sent_sample_holds_the_text_of_the_first_rows()
    {
        var target = Target();
        target.Build(new[] { Column(1, "id", Native.SQL_INTEGER, 10), Column(2, "amount", Native.SQL_DECIMAL, 10, 2) }, new HashSet<int>(), "");
        var sample = target.SentSample(3);
        Assert.Equal(InsertTarget.SentName, sample.DsnName);
        Assert.Equal(new[] { "1", "2", "3" }, sample.Rows.Select(r => r[0]));
        Assert.Equal("3.03", sample.Rows[2][1]);
    }
}
