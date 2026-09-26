using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using OdbcBench.Odbc;

namespace OdbcBench.Validation;

public sealed class NormalizationOptions
{
    /// <summary>Trim trailing spaces of text values (CHAR padding differs between drivers).</summary>
    public bool TrimTrailingSpaces { get; init; } = true;
    /// <summary>Treat NULL and the empty string as equal.</summary>
    public bool NullEqualsEmpty { get; init; } = false;
    /// <summary>Relative tolerance when both values parse as floating point.</summary>
    public double FloatTolerance { get; init; } = 1e-9;
}

/// <summary>A cell reduced to a comparable form.</summary>
public readonly record struct CanonicalValue(string Text, double? Number, int FractionDigits, bool ParseFailed)
{
    public bool IsNull => Text == Canonicalizer.NullMarker;
}

/// <summary>
/// Normalises the text a driver returned for a value according to the type family the column is compared as,
/// so two drivers that format the same value differently still compare equal.
/// </summary>
public static partial class Canonicalizer
{
    public const string NullMarker = "<NULL>";

    // Richest first: when two drivers disagree on the family of a column, the values are compared as the richer type.
    private static readonly TypeFamily[] Priority =
    {
        TypeFamily.Timestamp, TypeFamily.Date, TypeFamily.Time, TypeFamily.Guid, TypeFamily.Float, TypeFamily.Decimal,
        TypeFamily.Integer, TypeFamily.Bool, TypeFamily.Binary, TypeFamily.Text, TypeFamily.Other,
    };

    public static TypeFamily Richest(IEnumerable<TypeFamily> families)
    {
        var set = families.ToHashSet();
        foreach (var f in Priority)
            if (set.Contains(f)) return f;
        return TypeFamily.Other;
    }

    public static CanonicalValue Canonical(string? raw, TypeFamily family, NormalizationOptions options)
    {
        if (raw == null)
            return options.NullEqualsEmpty ? new CanonicalValue("", null, 0, false) : new CanonicalValue(NullMarker, null, 0, false);

        switch (family)
        {
            case TypeFamily.Text:
                return new CanonicalValue(options.TrimTrailingSpaces ? raw.TrimEnd(' ') : raw, null, 0, false);

            case TypeFamily.Integer:
            {
                var t = raw.Trim();
                if (BigInteger.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var big))
                    return new CanonicalValue(big.ToString(CultureInfo.InvariantCulture), (double)big, 0, false);
                var dec = NormalizeDecimal(t);
                if (dec != null && !dec.Contains('.'))
                    return new CanonicalValue(dec, double.TryParse(dec, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null, 0, false);
                return new CanonicalValue(t, null, 0, true);
            }

            case TypeFamily.Decimal:
            {
                var t = raw.Trim();
                var dec = NormalizeDecimal(t);
                if (dec != null)
                    return new CanonicalValue(dec, double.TryParse(dec, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null, 0, false);
                return new CanonicalValue(t, null, 0, true);
            }

            case TypeFamily.Float:
            {
                var t = raw.Trim();
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    return new CanonicalValue(v.ToString("R", CultureInfo.InvariantCulture), v, 0, false);
                var dec = NormalizeDecimal(t);
                if (dec != null && double.TryParse(dec, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    return new CanonicalValue(v.ToString("R", CultureInfo.InvariantCulture), v, 0, false);
                return new CanonicalValue(t, null, 0, true);
            }

            case TypeFamily.Bool:
            {
                var t = raw.Trim().ToLowerInvariant();
                if (t is "1" or "true" or "t" or "y" or "yes") return new CanonicalValue("1", 1, 0, false);
                if (t is "0" or "false" or "f" or "n" or "no") return new CanonicalValue("0", 0, 0, false);
                return new CanonicalValue(t, null, 0, true);
            }

            case TypeFamily.Date:
            {
                var m = DateTimeRegex().Match(raw.Trim());
                if (!m.Success) return new CanonicalValue(raw.Trim(), null, 0, true);
                return new CanonicalValue($"{m.Groups["y"].Value}-{m.Groups["mo"].Value}-{m.Groups["d"].Value}", null, 0, false);
            }

            case TypeFamily.Time:
            {
                var m = TimeRegex().Match(raw.Trim());
                if (!m.Success) return new CanonicalValue(raw.Trim(), null, 0, true);
                string fraction = TrimFraction(m.Groups["f"].Value);
                string text = $"{m.Groups["h"].Value.PadLeft(2, '0')}:{m.Groups["mi"].Value}:{(m.Groups["s"].Success ? m.Groups["s"].Value : "00")}";
                if (fraction.Length > 0) text += "." + fraction;
                return new CanonicalValue(text, null, fraction.Length, false);
            }

            case TypeFamily.Timestamp:
            {
                var m = DateTimeRegex().Match(raw.Trim());
                if (!m.Success) return new CanonicalValue(raw.Trim(), null, 0, true);
                string fraction = TrimFraction(m.Groups["f"].Value);
                string text = $"{m.Groups["y"].Value}-{m.Groups["mo"].Value}-{m.Groups["d"].Value} " +
                              $"{(m.Groups["h"].Success ? m.Groups["h"].Value : "00")}:{(m.Groups["mi"].Success ? m.Groups["mi"].Value : "00")}:{(m.Groups["s"].Success ? m.Groups["s"].Value : "00")}";
                if (fraction.Length > 0) text += "." + fraction;
                if (m.Groups["z"].Success) text += " " + NormalizeOffset(m.Groups["z"].Value);
                return new CanonicalValue(text, null, fraction.Length, false);
            }

            case TypeFamily.Guid:
            {
                var t = raw.Trim();
                if (Guid.TryParse(t, out var g)) return new CanonicalValue(g.ToString("D"), null, 0, false);
                return new CanonicalValue(t, null, 0, true);
            }

            case TypeFamily.Binary:
            {
                var t = raw.Trim();
                if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
                if (t.Length % 2 == 0 && t.All(Uri.IsHexDigit)) return new CanonicalValue(t.ToUpperInvariant(), null, 0, false);
                return new CanonicalValue(t, null, 0, true);
            }

            default:
                return new CanonicalValue(raw.Trim(), null, 0, false);
        }
    }

    /// <summary>Cuts a canonical time/timestamp text to the given number of fraction digits.</summary>
    public static string TruncateFraction(string canonicalText, int digits)
    {
        return FractionRegex().Replace(canonicalText, m =>
        {
            string f = m.Groups[1].Value;
            if (digits <= 0) return "";
            return "." + (f.Length > digits ? f[..digits] : f);
        }, 1);
    }

    /// <summary>Digits-only normalisation of a decimal string: no exponent, no leading/trailing zeros, "-0" becomes "0".</summary>
    public static string? NormalizeDecimal(string text)
    {
        var m = DecimalRegex().Match(text);
        if (!m.Success) return null;

        bool negative = m.Groups["sign"].Value == "-";
        string intPart = m.Groups["int"].Value;
        string fracPart = m.Groups["frac"].Value;
        int exponent = m.Groups["exp"].Success ? int.Parse(m.Groups["exp"].Value, CultureInfo.InvariantCulture) : 0;

        string digits = intPart + fracPart;
        int pointPos = intPart.Length + exponent;
        if (pointPos < 0)
        {
            digits = new string('0', -pointPos) + digits;
            pointPos = 0;
        }
        else if (pointPos > digits.Length)
        {
            digits += new string('0', pointPos - digits.Length);
        }

        string i = digits[..pointPos].TrimStart('0');
        string f = digits[pointPos..].TrimEnd('0');
        if (i.Length == 0) i = "0";
        if (i == "0" && f.Length == 0) negative = false;
        return (negative ? "-" : "") + i + (f.Length > 0 ? "." + f : "");
    }

    private static string TrimFraction(string fraction) => fraction.TrimEnd('0');

    private static string NormalizeOffset(string z)
    {
        z = z.Trim();
        if (z.Equals("Z", StringComparison.OrdinalIgnoreCase)) return "+00:00";
        string sign = z[..1];
        string digits = z[1..].Replace(":", "");
        if (digits.Length == 2) digits += "00";
        return $"{sign}{digits[..2]}:{digits[2..]}";
    }

    [GeneratedRegex(@"^(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2})(?:[ T](?<h>\d{2}):(?<mi>\d{2})(?::(?<s>\d{2})(?:\.(?<f>\d+))?)?)?\s*(?<z>Z|[+-]\d{2}(?::?\d{2})?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DateTimeRegex();

    [GeneratedRegex(@"^(?<h>\d{1,2}):(?<mi>\d{2})(?::(?<s>\d{2})(?:\.(?<f>\d+))?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"^(?<sign>[+-])?(?<int>\d*)(?:\.(?<frac>\d*))?(?:[eE](?<exp>[+-]?\d+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalRegex();

    [GeneratedRegex(@"\.(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex FractionRegex();
}
