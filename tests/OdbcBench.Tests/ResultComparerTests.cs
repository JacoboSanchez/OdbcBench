using OdbcBench.Odbc;
using OdbcBench.Validation;
using static OdbcBench.Tests.TestData;

namespace OdbcBench.Tests;

public class ResultComparerTests
{
    private const string Ordered = "SELECT * FROM t ORDER BY id";
    private static readonly NormalizationOptions Default = new();

    [Fact]
    public void Identical_samples_pass()
    {
        var result = ResultComparer.Compare(new[] { Sample("a", Rows(10)), Sample("b", Rows(10)) }, Ordered, Default);
        Assert.Equal("PASS", result.Status);
        Assert.Equal(10, result.RowsCompared);
        Assert.Equal(2, result.DsnsCompared);
        Assert.Equal("a", result.BaselineDsn);
        Assert.Equal(4, result.Metadata.Count);
    }

    [Fact]
    public void Query_without_order_by_warns()
    {
        var result = ResultComparer.Compare(new[] { Sample("a", Rows(3)), Sample("b", Rows(3)) }, "select * from t", Default);
        Assert.Equal("WARN", result.Status);
        Assert.Contains(result.Issues, i => i.Message.Contains("ORDER BY"));
    }

    [Fact]
    public void Any_query_without_order_by_warns_when_dsns_override_it()
    {
        var result = ResultComparer.Compare(new[] { Sample("a", Rows(3)), Sample("b", Rows(3)) }, new[] { Ordered, "select * from t" }, Default);
        Assert.Equal("WARN", result.Status);
    }

    [Fact]
    public void A_different_cell_fails_with_row_and_column()
    {
        var other = Rows(5);
        other[2][2] = "3.51";
        var result = ResultComparer.Compare(new[] { Sample("a", Rows(5)), Sample("b", other) }, Ordered, Default);

        Assert.Equal("FAIL", result.Status);
        var diff = Assert.Single(result.Issues, i => i.CellDiff);
        Assert.Equal(3, diff.Row);
        Assert.Equal("amount", diff.Column);
        Assert.Equal("b", diff.Dsn);
        Assert.Equal("3.50", diff.BaselineValue);
        Assert.Equal("3.51", diff.Value);
    }

    [Fact]
    public void Formatting_differences_are_not_failures()
    {
        var other = Rows(3);
        foreach (var row in other)
        {
            row[1] += "   ";                          // CHAR padding
            row[2] = row[2]!.TrimEnd('0');            // 1.50 vs 1.5
            row[3] = row[3]!.Replace(".000", "");     // no fractional part
        }
        var result = ResultComparer.Compare(new[] { Sample("a", Rows(3)), Sample("b", other) }, Ordered, Default);
        Assert.DoesNotContain(result.Issues, i => i.Severity == Severity.Fail);
    }

    [Fact]
    public void Type_family_differences_warn_and_values_compare_as_the_richer_type()
    {
        var a = Sample("a", Rows(3));
        var b = Sample("b", Rows(3));
        b.Columns[3] = Column(4, "created", Native.SQL_WVARCHAR, 27); // timestamp delivered as text
        foreach (var row in b.Rows) row[3] = row[3]!.Replace(".000", ".0000000");

        var result = ResultComparer.Compare(new[] { a, b }, Ordered, Default);
        Assert.Equal("WARN", result.Status);
        Assert.Contains(result.Issues, i => i.Severity == Severity.Warn && i.Message.Contains("type family"));
        Assert.Equal("timestamp", result.Metadata[3].ComparedAs);
    }

    [Fact]
    public void Same_family_size_differences_are_informational()
    {
        var a = Sample("a", Rows(2));
        var b = Sample("b", Rows(2));
        b.Columns[1] = Column(2, "name", Native.SQL_WVARCHAR, 255);
        var result = ResultComparer.Compare(new[] { a, b }, Ordered, Default);
        Assert.Equal("PASS", result.Status);
        Assert.Contains(result.Issues, i => i.Severity == Severity.Info && i.Column == "name");
    }

    [Fact]
    public void Column_names_compare_case_insensitively()
    {
        var b = Sample("b", Rows(2));
        b.Columns[0] = Column(1, "ID", Native.SQL_INTEGER, 10);
        var result = ResultComparer.Compare(new[] { Sample("a", Rows(2)), b }, Ordered, Default);
        Assert.Equal("PASS", result.Status);
    }

    [Fact]
    public void A_renamed_column_fails()
    {
        var b = Sample("b", Rows(2));
        b.Columns[0] = Column(1, "identifier", Native.SQL_INTEGER, 10);
        var result = ResultComparer.Compare(new[] { Sample("a", Rows(2)), b }, Ordered, Default);
        Assert.Equal("FAIL", result.Status);
    }

    [Fact]
    public void Different_column_counts_fail()
    {
        var b = Sample("b", Rows(2));
        b.Columns.RemoveAt(3);
        var result = ResultComparer.Compare(new[] { Sample("a", Rows(2)), b }, Ordered, Default);
        Assert.Equal("FAIL", result.Status);
        Assert.Contains(result.Issues, i => i.Message.Contains("returns 3 columns"));
    }

    [Fact]
    public void A_missing_row_is_recognised_as_a_shift()
    {
        var result = ResultComparer.Compare(new[] { Sample("a", Rows(10)), Sample("b", Rows(10, skip: 3)) }, Ordered, Default);
        Assert.Equal("FAIL", result.Status);
        Assert.Contains(result.Issues, i => i.Message.Contains("row 3 of a is missing on b"));
    }

    [Fact]
    public void An_extra_row_is_recognised_as_a_shift()
    {
        var result = ResultComparer.Compare(new[] { Sample("a", Rows(10, skip: 3)), Sample("b", Rows(10)) }, Ordered, Default);
        Assert.Contains(result.Issues, i => i.Message.Contains("b has an extra row at row 3"));
    }

    [Fact]
    public void Fewer_rows_fail()
    {
        var result = ResultComparer.Compare(new[] { Sample("a", Rows(10)), Sample("b", Rows(7)) }, Ordered, Default);
        Assert.Contains(result.Issues, i => i.Severity == Severity.Fail && i.Message.Contains("returned 7 sample rows"));
    }

    [Fact]
    public void A_failed_sample_fails_validation()
    {
        var failed = new SampleResult { DsnName = "b", Ok = false, Error = "boom" };
        var result = ResultComparer.Compare(new[] { Sample("a", Rows(2)), failed }, Ordered, Default);
        Assert.Equal("FAIL", result.Status);
        Assert.Contains(result.Issues, i => i.Message.Contains("boom"));
    }

    [Fact]
    public void Extra_result_sets_warn()
    {
        var a = Sample("a", Rows(2));
        var b = new SampleResult { DsnName = "b", Columns = a.Columns, Rows = Rows(2).ToList(), MoreResults = true };
        var result = ResultComparer.Compare(new[] { a, b }, Ordered, Default);
        Assert.Equal("WARN", result.Status);
    }

    [Fact]
    public void Cell_differences_are_capped()
    {
        var other = Rows(20);
        foreach (var row in other) for (int c = 0; c < row.Length; c++) row[c] = "x" + row[c];
        var result = ResultComparer.Compare(new[] { Sample("a", Rows(20)), Sample("b", other) }, Ordered, Default);
        Assert.Equal(ResultComparer.MaxCellDiffs, result.Issues.Count(i => i.CellDiff));
        Assert.Contains(result.Issues, i => i.Message.Contains("80 differing cells"));
    }

    [Fact]
    public void A_single_dsn_has_nothing_to_compare()
    {
        var result = ResultComparer.Compare(new[] { Sample("a", Rows(2)) }, Ordered, Default);
        Assert.Equal("PASS", result.Status);
        Assert.Contains(result.Issues, i => i.Severity == Severity.Info);
    }

    [Theory]
    [InlineData("select * from t order by 1", true)]
    [InlineData("SELECT a FROM t\nORDER\n  BY a", true)]
    [InlineData("select * from border_by", false)]
    [InlineData("select * from t", false)]
    public void Order_by_detection(string query, bool expected)
    {
        Assert.Equal(expected, ResultComparer.HasOrderBy(query));
    }
}
