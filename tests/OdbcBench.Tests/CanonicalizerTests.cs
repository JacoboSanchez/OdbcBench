using OdbcBench.Odbc;
using OdbcBench.Validation;

namespace OdbcBench.Tests;

public class CanonicalizerTests
{
    private static readonly NormalizationOptions Default = new();

    private static string C(string? raw, TypeFamily family, NormalizationOptions? options = null) =>
        Canonicalizer.Canonical(raw, family, options ?? Default).Text;

    [Theory]
    [InlineData("1.50", "1.5")]
    [InlineData("-0.00", "0")]
    [InlineData(".5", "0.5")]
    [InlineData("1.5E+2", "150")]
    [InlineData("00012.3400", "12.34")]
    [InlineData("-1e-3", "-0.001")]
    [InlineData("70786.7300", "70786.73")]
    [InlineData("+7", "7")]
    public void Decimals_are_compared_by_value(string raw, string expected)
    {
        Assert.Equal(expected, C(raw, TypeFamily.Decimal));
    }

    [Fact]
    public void Decimal_normalisation_keeps_digits_beyond_double_precision()
    {
        Assert.Equal("12345678901234567890.123456789", C("12345678901234567890.1234567890", TypeFamily.Decimal));
    }

    [Theory]
    [InlineData(" 42 ", "42")]
    [InlineData("42.000", "42")]
    [InlineData("-0", "0")]
    [InlineData("18446744073709551615", "18446744073709551615")]
    public void Integers_are_parsed(string raw, string expected)
    {
        Assert.Equal(expected, C(raw, TypeFamily.Integer));
    }

    [Fact]
    public void Unparseable_values_are_flagged_and_kept_as_text()
    {
        var v = Canonicalizer.Canonical("abc", TypeFamily.Integer, Default);
        Assert.True(v.ParseFailed);
        Assert.Equal("abc", v.Text);
    }

    [Fact]
    public void Floats_use_round_trip_formatting()
    {
        Assert.Equal(C("1", TypeFamily.Float), C("1.0", TypeFamily.Float));
        Assert.Equal(C("150", TypeFamily.Float), C("1.5E+2", TypeFamily.Float));
    }

    [Fact]
    public void Floats_within_tolerance_compare_equal()
    {
        var a = Canonicalizer.Canonical((0.1 + 0.2).ToString("R", System.Globalization.CultureInfo.InvariantCulture), TypeFamily.Float, Default);
        var b = Canonicalizer.Canonical("0.3", TypeFamily.Float, Default);
        Assert.Equal(ResultComparer.CellOutcome.Equal, ResultComparer.CompareCells(a, b, TypeFamily.Float, Default, out _));
    }

    [Fact]
    public void Decimals_that_differ_are_different_even_when_doubles_agree()
    {
        var a = Canonicalizer.Canonical("12345678901234567.1", TypeFamily.Decimal, Default);
        var b = Canonicalizer.Canonical("12345678901234567.2", TypeFamily.Decimal, Default);
        Assert.Equal(ResultComparer.CellOutcome.Different, ResultComparer.CompareCells(a, b, TypeFamily.Decimal, Default, out _));
    }

    [Theory]
    [InlineData("2024-01-02 03:04:05.1230000", "2024-01-02 03:04:05.123")]
    [InlineData("2024-01-02T03:04:05", "2024-01-02 03:04:05")]
    [InlineData("2024-01-02 03:04:05.000", "2024-01-02 03:04:05")]
    [InlineData("2024-01-02", "2024-01-02 00:00:00")]
    [InlineData("2024-01-02 03:04:05 +0100", "2024-01-02 03:04:05 +01:00")]
    public void Timestamps_are_normalised(string raw, string expected)
    {
        Assert.Equal(expected, C(raw, TypeFamily.Timestamp));
    }

    [Fact]
    public void Timestamps_with_different_fraction_precision_warn_but_match()
    {
        var a = Canonicalizer.Canonical("2024-01-02 03:04:05.1234567", TypeFamily.Timestamp, Default);
        var b = Canonicalizer.Canonical("2024-01-02 03:04:05.123", TypeFamily.Timestamp, Default);
        Assert.Equal(ResultComparer.CellOutcome.EqualWithWarning, ResultComparer.CompareCells(a, b, TypeFamily.Timestamp, Default, out var reason));
        Assert.Contains("precision", reason);
    }

    [Fact]
    public void Dates_ignore_a_midnight_time_part()
    {
        Assert.Equal("2024-01-02", C("2024-01-02 00:00:00", TypeFamily.Date));
    }

    [Theory]
    [InlineData("3:04:05", "03:04:05")]
    [InlineData("03:04:05.5000", "03:04:05.5")]
    public void Times_are_normalised(string raw, string expected)
    {
        Assert.Equal(expected, C(raw, TypeFamily.Time));
    }

    [Fact]
    public void Guids_are_lower_case_without_braces()
    {
        Assert.Equal("abcdef01-2345-6789-abcd-ef0123456789", C("{ABCDEF01-2345-6789-ABCD-EF0123456789}", TypeFamily.Guid));
    }

    [Theory]
    [InlineData("0xABCD", "ABCD")]
    [InlineData("abcd", "ABCD")]
    public void Binary_is_upper_case_hex(string raw, string expected)
    {
        Assert.Equal(expected, C(raw, TypeFamily.Binary));
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("True", "1")]
    [InlineData("0", "0")]
    [InlineData("false", "0")]
    public void Booleans_become_zero_or_one(string raw, string expected)
    {
        Assert.Equal(expected, C(raw, TypeFamily.Bool));
    }

    [Fact]
    public void Trailing_spaces_are_trimmed_only_when_configured()
    {
        Assert.Equal("abc", C("abc   ", TypeFamily.Text));
        Assert.Equal("abc   ", C("abc   ", TypeFamily.Text, new NormalizationOptions { TrimTrailingSpaces = false }));
        Assert.Equal("  abc", C("  abc", TypeFamily.Text)); // leading spaces are data
    }

    [Fact]
    public void Null_differs_from_empty_unless_configured()
    {
        Assert.Equal(Canonicalizer.NullMarker, C(null, TypeFamily.Text));
        Assert.Equal("", C("", TypeFamily.Text));
        var lenient = new NormalizationOptions { NullEqualsEmpty = true };
        Assert.Equal(C("", TypeFamily.Text, lenient), C(null, TypeFamily.Text, lenient));
    }

    [Fact]
    public void Null_against_value_is_a_difference()
    {
        var a = Canonicalizer.Canonical(null, TypeFamily.Integer, Default);
        var b = Canonicalizer.Canonical("0", TypeFamily.Integer, Default);
        Assert.Equal(ResultComparer.CellOutcome.Different, ResultComparer.CompareCells(a, b, TypeFamily.Integer, Default, out _));
    }

    [Fact]
    public void Richest_family_wins_when_drivers_disagree()
    {
        Assert.Equal(TypeFamily.Timestamp, Canonicalizer.Richest(new[] { TypeFamily.Text, TypeFamily.Timestamp }));
        Assert.Equal(TypeFamily.Float, Canonicalizer.Richest(new[] { TypeFamily.Decimal, TypeFamily.Float }));
        Assert.Equal(TypeFamily.Text, Canonicalizer.Richest(new[] { TypeFamily.Text }));
    }
}
