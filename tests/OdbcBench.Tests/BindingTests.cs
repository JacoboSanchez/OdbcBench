using OdbcBench.Fetch;
using OdbcBench.Odbc;
using static OdbcBench.Tests.TestData;

namespace OdbcBench.Tests;

public class BindingTests
{
    private const long Threshold = 8000;

    private static ColumnInfo Mapped(ColumnInfo c, BindMode mode = BindMode.Native)
    {
        TypeMapper.Apply(c, mode, Threshold);
        return c;
    }

    [Theory]
    [InlineData(Native.SQL_INTEGER, false, Native.SQL_C_SLONG, 4)]
    [InlineData(Native.SQL_INTEGER, true, Native.SQL_C_ULONG, 4)]
    [InlineData(Native.SQL_BIGINT, false, Native.SQL_C_SBIGINT, 8)]
    [InlineData(Native.SQL_SMALLINT, false, Native.SQL_C_SSHORT, 2)]
    [InlineData(Native.SQL_TINYINT, true, Native.SQL_C_UTINYINT, 1)]
    [InlineData(Native.SQL_DOUBLE, false, Native.SQL_C_DOUBLE, 8)]
    [InlineData(Native.SQL_FLOAT, false, Native.SQL_C_DOUBLE, 8)]
    [InlineData(Native.SQL_REAL, false, Native.SQL_C_FLOAT, 4)]
    [InlineData(Native.SQL_BIT, false, Native.SQL_C_BIT, 1)]
    [InlineData(Native.SQL_TYPE_DATE, false, Native.SQL_C_TYPE_DATE, 6)]
    [InlineData(Native.SQL_TYPE_TIMESTAMP, false, Native.SQL_C_TYPE_TIMESTAMP, 16)]
    [InlineData(Native.SQL_TIMESTAMP, false, Native.SQL_C_TYPE_TIMESTAMP, 16)]
    [InlineData(Native.SQL_GUID, false, Native.SQL_C_GUID, 16)]
    public void Fixed_width_types_bind_natively(short sqlType, bool unsigned, short cType, int bytes)
    {
        var c = Mapped(Column(1, "c", sqlType, 10, unsigned: unsigned));
        Assert.Equal(cType, c.CType);
        Assert.Equal(bytes, c.ElementBytes);
        Assert.False(c.IsLong);
    }

    [Fact]
    public void Character_columns_bind_as_utf16_with_a_terminator()
    {
        var c = Mapped(Column(1, "c", Native.SQL_VARCHAR, 50));
        Assert.Equal(Native.SQL_C_WCHAR, c.CType);
        Assert.Equal(102, c.ElementBytes);
    }

    [Fact]
    public void Character_width_uses_the_largest_reported_size()
    {
        var c = Mapped(Column(1, "c", Native.SQL_WVARCHAR, 10, displaySize: 40, length: 20));
        Assert.Equal((40 + 1) * 2, c.ElementBytes);
    }

    [Fact]
    public void Decimals_bind_as_text()
    {
        var c = Mapped(Column(1, "c", Native.SQL_DECIMAL, 10, 2));
        Assert.Equal(Native.SQL_C_WCHAR, c.CType);
        Assert.Equal((10 + 6 + 1) * 2, c.ElementBytes);
    }

    [Fact]
    public void Time_with_fraction_binds_as_text_because_the_struct_has_none()
    {
        Assert.Equal(Native.SQL_C_TYPE_TIME, Mapped(Column(1, "t", Native.SQL_TYPE_TIME, 8)).CType);
        Assert.Equal(Native.SQL_C_WCHAR, Mapped(Column(1, "t", Native.SQL_TYPE_TIME, 16, 7)).CType);
    }

    [Theory]
    [InlineData(Native.SQL_WVARCHAR, 0UL)]          // (max) types reported as size 0
    [InlineData(Native.SQL_VARCHAR, 10000UL)]       // 20,002 bytes as UTF-16: over the threshold
    [InlineData(Native.SQL_WLONGVARCHAR, 1073741823UL)]
    [InlineData(Native.SQL_LONGVARBINARY, 2147483647UL)]
    [InlineData(Native.SQL_VARBINARY, 0UL)]
    public void Large_or_unknown_sizes_are_long_columns(short sqlType, ulong size)
    {
        var c = Mapped(Column(1, "c", sqlType, size));
        Assert.True(c.IsLong);
        Assert.Equal(0, c.ElementBytes);
    }

    [Fact]
    public void Small_long_types_are_bound()
    {
        var c = Mapped(Column(1, "c", Native.SQL_LONGVARCHAR, 100));
        Assert.False(c.IsLong);
        Assert.Equal(202, c.ElementBytes);
    }

    [Fact]
    public void Binary_binds_raw_bytes()
    {
        var c = Mapped(Column(1, "b", Native.SQL_VARBINARY, 16));
        Assert.Equal(Native.SQL_C_BINARY, c.CType);
        Assert.Equal(16, c.ElementBytes);
    }

    [Fact]
    public void Driver_specific_types_bind_as_text_from_the_display_size()
    {
        var c = Mapped(Column(1, "c", Native.SQL_SS_TIME2, 16, 7, displaySize: 16));
        Assert.Equal(Native.SQL_C_WCHAR, c.CType);
        Assert.Equal(34, c.ElementBytes);
        Assert.True(Mapped(Column(1, "c", -999, 0)).IsLong);
    }

    [Fact]
    public void Wchar_mode_binds_everything_as_text()
    {
        var c = Mapped(Column(1, "c", Native.SQL_INTEGER, 10, displaySize: 11), BindMode.WChar);
        Assert.Equal(Native.SQL_C_WCHAR, c.CType);
        Assert.Equal(24, c.ElementBytes);
        var t = Mapped(Column(2, "t", Native.SQL_TYPE_TIMESTAMP, 23, 3), BindMode.WChar);
        Assert.Equal((19 + 4 + 7 + 1) * 2, t.ElementBytes);
    }

    [Fact]
    public void Text_fallback_after_a_rejected_binding()
    {
        var c = Mapped(Column(1, "c", Native.SQL_GUID, 36, displaySize: 36));
        TypeMapper.ForceText(c, Threshold);
        Assert.Equal(Native.SQL_C_WCHAR, c.CType);
        Assert.True(c.FellBackToText);
        Assert.Equal(74, c.ElementBytes);
    }

    // ---- binding plan

    private static List<ColumnInfo> Columns(params ColumnInfo[] columns) => columns.Select(c => Mapped(c)).ToList();

    [Fact]
    public void Without_long_columns_everything_is_bound_at_the_driver_array_size()
    {
        var plan = BindingPlan.Build(Columns(Column(1, "a", Native.SQL_INTEGER), Column(2, "b", Native.SQL_VARCHAR, 10)),
            1000, 1000, false, LongColumnMode.RowByRow, 65536, long.MaxValue);
        Assert.Equal(1000, plan.ArraySize);
        Assert.Equal(2, plan.Bound.Count);
        Assert.Empty(plan.Unbound);
        Assert.Null(plan.BlockFetchDisabledBy);
        Assert.Empty(plan.Warnings);
        Assert.Equal(4 + 22 + 2 * IntPtr.Size, plan.RowBytes);
    }

    [Fact]
    public void A_driver_that_lowers_the_array_size_is_reported()
    {
        var plan = BindingPlan.Build(Columns(Column(1, "a", Native.SQL_INTEGER)), 1000, 100, false, LongColumnMode.RowByRow, 65536, long.MaxValue);
        Assert.Equal(100, plan.ArraySize);
        Assert.Contains(plan.Warnings, w => w.Contains("100 instead of the requested 1000"));
    }

    [Fact]
    public void A_long_column_without_any_column_support_forces_row_by_row_and_getdata_for_the_rest()
    {
        var plan = BindingPlan.Build(Columns(
                Column(1, "a", Native.SQL_INTEGER),
                Column(2, "body", Native.SQL_WLONGVARCHAR, 0),
                Column(3, "c", Native.SQL_INTEGER)),
            1000, 1000, getDataAnyColumn: false, LongColumnMode.RowByRow, 65536, long.MaxValue);

        Assert.Equal(1, plan.ArraySize);
        Assert.Equal("body", plan.BlockFetchDisabledBy);
        Assert.Equal(new[] { "a" }, plan.Bound.Select(c => c.Name));
        Assert.Equal(new[] { "body", "c" }, plan.Unbound.Select(c => c.Name));
        Assert.Contains(plan.Warnings, w => w.Contains("SQL_GD_ANY_COLUMN"));
    }

    [Fact]
    public void With_any_column_support_only_long_columns_use_getdata()
    {
        var plan = BindingPlan.Build(Columns(
                Column(1, "a", Native.SQL_INTEGER),
                Column(2, "body", Native.SQL_WLONGVARCHAR, 0),
                Column(3, "c", Native.SQL_INTEGER)),
            1000, 1000, getDataAnyColumn: true, LongColumnMode.RowByRow, 65536, long.MaxValue);

        Assert.Equal(new[] { "a", "c" }, plan.Bound.Select(c => c.Name));
        Assert.Equal(new[] { "body" }, plan.Unbound.Select(c => c.Name));
    }

    [Fact]
    public void Bind_capped_keeps_block_fetch()
    {
        var plan = BindingPlan.Build(Columns(Column(1, "a", Native.SQL_INTEGER), Column(2, "body", Native.SQL_WLONGVARCHAR, 0)),
            1000, 1000, false, LongColumnMode.BindCapped, 4096, long.MaxValue);
        Assert.Equal(1000, plan.ArraySize);
        Assert.Empty(plan.Unbound);
        var body = plan.Bound.Single(c => c.Name == "body");
        Assert.True(body.Capped);
        Assert.Equal(4096, body.ElementBytes);
    }

    [Fact]
    public void Array_size_is_lowered_to_fit_max_bound_bytes()
    {
        var plan = BindingPlan.Build(Columns(Column(1, "big", Native.SQL_WVARCHAR, 3999)), // 8,000 bytes + 8 indicator
            10000, 10000, false, LongColumnMode.RowByRow, 65536, maxBoundBytes: 8_008 * 100);
        Assert.Equal(100, plan.ArraySize);
        Assert.Contains(plan.Warnings, w => w.Contains("lowered from 10000 to 100"));
    }
}
