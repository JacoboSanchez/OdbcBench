using System.Text.RegularExpressions;
using OdbcBench.Odbc;

namespace OdbcBench.Validation;

public enum Severity { Info, Warn, Fail }

/// <summary>
/// One validation finding. Cell differences carry the row (1-based), the column, the DSN compared against the
/// baseline and both raw values (null = SQL NULL).
/// </summary>
public sealed record ValidationIssue(
    Severity Severity,
    string Message,
    string? Column = null,
    int? Row = null,
    string? Dsn = null,
    bool CellDiff = false,
    string? BaselineValue = null,
    string? Value = null);

public sealed class ColumnMetadataRow
{
    public int Ordinal { get; init; }
    public string Name { get; init; } = "";
    public string ComparedAs { get; init; } = "";
    public Dictionary<string, string> TypeByDsn { get; init; } = new();
}

public sealed class ValidationResult
{
    public List<ValidationIssue> Issues { get; set; } = new();
    public List<ColumnMetadataRow> Metadata { get; set; } = new();
    public int RowsCompared { get; set; }
    public int DsnsCompared { get; set; }
    public string BaselineDsn { get; set; } = "";
    public bool StrictAbort { get; set; }

    public string Status =>
        Issues.Any(i => i.Severity == Severity.Fail) ? "FAIL" :
        Issues.Any(i => i.Severity == Severity.Warn) ? "WARN" : "PASS";

    public int FailCount => Issues.Count(i => i.Severity == Severity.Fail);
    public int WarnCount => Issues.Count(i => i.Severity == Severity.Warn);
}

/// <summary>Compares the validation samples of every DSN against the first one (plan section "Validation phase").</summary>
public static partial class ResultComparer
{
    /// <summary>Differing cells listed per DSN; the rest are only counted.</summary>
    public const int MaxCellDiffs = 50;

    public static ValidationResult Compare(IReadOnlyList<SampleResult> samples, string query, NormalizationOptions options)
        => Compare(samples, new[] { query }, options);

    /// <param name="queries">Every distinct query text in use (per-DSN overrides included); each is checked for ORDER BY.</param>
    public static ValidationResult Compare(IReadOnlyList<SampleResult> samples, IEnumerable<string> queries, NormalizationOptions options)
    {
        var result = new ValidationResult();

        foreach (var bad in samples.Where(s => !s.Ok))
            result.Issues.Add(new ValidationIssue(Severity.Fail, $"{bad.DsnName}: sample fetch failed: {bad.Error}", Dsn: bad.DsnName));

        if (queries.Any(q => !HasOrderBy(q)))
            result.Issues.Add(new ValidationIssue(Severity.Warn,
                "the query has no ORDER BY: the first rows are not guaranteed to be the same set on every DSN, so a data FAIL below may be spurious"));

        var ok = samples.Where(s => s.Ok).ToList();
        foreach (var s in ok)
            if (s.MoreResults)
                result.Issues.Add(new ValidationIssue(Severity.Warn, $"{s.DsnName}: the statement returns more than one result set; only the first one is measured", Dsn: s.DsnName));

        result.DsnsCompared = ok.Count;
        if (ok.Count == 0) return result;

        var baseline = ok[0];
        result.BaselineDsn = baseline.DsnName;
        int columnCount = baseline.Columns.Count;

        // Family used to compare each column: the richest one any DSN declared for that ordinal.
        var families = new TypeFamily[columnCount];
        for (int c = 0; c < columnCount; c++)
        {
            var declared = ok.Where(s => c < s.Columns.Count).Select(s => s.Columns[c].Family);
            families[c] = Canonicalizer.Richest(declared);
            var row = new ColumnMetadataRow { Ordinal = c + 1, Name = baseline.Columns[c].Name, ComparedAs = families[c].ToString().ToLowerInvariant() };
            foreach (var s in ok)
                if (c < s.Columns.Count) row.TypeByDsn[s.DsnName] = TypeText(s.Columns[c]);
            result.Metadata.Add(row);
        }

        result.RowsCompared = baseline.Rows.Count;
        if (ok.Count < 2)
        {
            result.Issues.Add(new ValidationIssue(Severity.Info, "only one DSN returned a sample; there is nothing to compare it with"));
            return result;
        }

        foreach (var other in ok.Skip(1))
            CompareWithBaseline(result, baseline, other, families, options);

        return result;
    }

    private static void CompareWithBaseline(ValidationResult result, SampleResult baseline, SampleResult other, TypeFamily[] families, NormalizationOptions options)
    {
        int columnCount = baseline.Columns.Count;
        if (other.Columns.Count != columnCount)
        {
            result.Issues.Add(new ValidationIssue(Severity.Fail,
                $"{other.DsnName} returns {other.Columns.Count} columns, {baseline.DsnName} returns {columnCount}", Dsn: other.DsnName));
            return;
        }

        for (int c = 0; c < columnCount; c++)
        {
            var a = baseline.Columns[c];
            var b = other.Columns[c];
            if (!string.Equals(a.Name.Trim(), b.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                result.Issues.Add(new ValidationIssue(Severity.Fail,
                    $"column {c + 1} is named '{a.Name}' on {baseline.DsnName} but '{b.Name}' on {other.DsnName}", a.Name, Dsn: other.DsnName));
            else if (a.Family != b.Family)
                result.Issues.Add(new ValidationIssue(Severity.Warn,
                    $"column '{a.Name}': type family {Lower(a.Family)} ({TypeText(a)}) on {baseline.DsnName} vs {Lower(b.Family)} ({TypeText(b)}) on {other.DsnName}; values compared as {Lower(families[c])}",
                    a.Name, Dsn: other.DsnName));
            else if (a.SqlType != b.SqlType || a.ColumnSize != b.ColumnSize || a.DecimalDigits != b.DecimalDigits)
                result.Issues.Add(new ValidationIssue(Severity.Info,
                    $"column '{a.Name}': {TypeText(a)} on {baseline.DsnName} vs {TypeText(b)} on {other.DsnName}", a.Name, Dsn: other.DsnName));
        }

        if (other.Rows.Count != baseline.Rows.Count)
            result.Issues.Add(new ValidationIssue(Severity.Fail,
                $"{other.DsnName} returned {other.Rows.Count} sample rows, {baseline.DsnName} returned {baseline.Rows.Count}", Dsn: other.DsnName));

        int rows = Math.Min(other.Rows.Count, baseline.Rows.Count);
        int diffs = 0;
        int firstDiffRow = -1;
        var warnedColumns = new HashSet<int>();

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columnCount; c++)
            {
                var family = families[c];
                var va = Canonicalizer.Canonical(baseline.Rows[r][c], family, options);
                var vb = Canonicalizer.Canonical(other.Rows[r][c], family, options);
                var outcome = CompareCells(va, vb, family, options, out string? reason);

                if (outcome == CellOutcome.Different)
                {
                    diffs++;
                    if (firstDiffRow < 0) firstDiffRow = r;
                    if (diffs <= MaxCellDiffs)
                    {
                        string name = baseline.Columns[c].Name;
                        string detail = va.ParseFailed || vb.ParseFailed ? $" (not parseable as {Lower(family)}, compared as text)" : "";
                        result.Issues.Add(new ValidationIssue(Severity.Fail,
                            $"row {r + 1}, column '{name}': '{Show(baseline.Rows[r][c])}' on {baseline.DsnName} vs '{Show(other.Rows[r][c])}' on {other.DsnName}{detail}",
                            name, r + 1, other.DsnName, CellDiff: true, BaselineValue: baseline.Rows[r][c], Value: other.Rows[r][c]));
                    }
                }
                else if (outcome == CellOutcome.EqualWithWarning && warnedColumns.Add(c))
                {
                    result.Issues.Add(new ValidationIssue(Severity.Warn,
                        $"column '{baseline.Columns[c].Name}': {reason} ({baseline.DsnName} vs {other.DsnName})", baseline.Columns[c].Name, Dsn: other.DsnName));
                }
            }
        }

        if (diffs > MaxCellDiffs)
            result.Issues.Add(new ValidationIssue(Severity.Info,
                $"{other.DsnName}: {diffs} differing cells in total; the first {MaxCellDiffs} are listed", Dsn: other.DsnName));

        if (firstDiffRow >= 0 && DetectShift(baseline, other, firstDiffRow, families, options) is { } shift)
            result.Issues.Add(new ValidationIssue(Severity.Fail, shift, Dsn: other.DsnName));
    }

    /// <summary>
    /// Recognises a whole-row shift: when every row after the first difference matches its neighbour on the other DSN,
    /// the cause is one missing or extra row (or a different order), not many independent value differences.
    /// </summary>
    private static string? DetectShift(SampleResult baseline, SampleResult other, int first, TypeFamily[] families, NormalizationOptions options)
    {
        if (Shifted(baseline.Rows, other.Rows, first, families, options))
            return $"{other.DsnName} matches {baseline.DsnName} shifted by one row from row {first + 1}: row {first + 1} of {baseline.DsnName} is missing on {other.DsnName} (or the order differs)";
        if (Shifted(other.Rows, baseline.Rows, first, families, options))
            return $"{other.DsnName} matches {baseline.DsnName} shifted by one row from row {first + 1}: {other.DsnName} has an extra row at row {first + 1} (or the order differs)";
        return null;
    }

    /// <summary>True when longer[r + 1] equals shorter[r] for every comparable row from <paramref name="first"/> on.</summary>
    private static bool Shifted(List<string?[]> longer, List<string?[]> shorter, int first, TypeFamily[] families, NormalizationOptions options)
    {
        int compared = 0;
        for (int r = first; r < shorter.Count && r + 1 < longer.Count; r++)
        {
            if (!RowsEqual(longer[r + 1], shorter[r], families, options)) return false;
            compared++;
        }
        return compared > 0;
    }

    private static bool RowsEqual(string?[] a, string?[] b, TypeFamily[] families, NormalizationOptions options)
    {
        for (int c = 0; c < families.Length && c < a.Length && c < b.Length; c++)
        {
            var va = Canonicalizer.Canonical(a[c], families[c], options);
            var vb = Canonicalizer.Canonical(b[c], families[c], options);
            if (CompareCells(va, vb, families[c], options, out _) == CellOutcome.Different) return false;
        }
        return true;
    }

    public enum CellOutcome { Equal, EqualWithWarning, Different }

    public static CellOutcome CompareCells(CanonicalValue a, CanonicalValue b, TypeFamily family, NormalizationOptions options, out string? reason)
    {
        reason = null;
        if (a.IsNull || b.IsNull)
            return a.IsNull == b.IsNull ? CellOutcome.Equal : CellOutcome.Different;

        if (a.Text == b.Text) return CellOutcome.Equal;

        if ((family == TypeFamily.Float || family == TypeFamily.Decimal || family == TypeFamily.Integer) && a.Number is double x && b.Number is double y)
        {
            if (x == y && family == TypeFamily.Float) return CellOutcome.Equal;
            double tolerance = options.FloatTolerance * Math.Max(Math.Abs(x), Math.Abs(y));
            if (family == TypeFamily.Float && Math.Abs(x - y) <= tolerance) return CellOutcome.Equal;
        }

        if ((family == TypeFamily.Timestamp || family == TypeFamily.Time) && a.FractionDigits != b.FractionDigits)
        {
            int digits = Math.Min(a.FractionDigits, b.FractionDigits);
            if (Canonicalizer.TruncateFraction(a.Text, digits) == Canonicalizer.TruncateFraction(b.Text, digits))
            {
                reason = $"fractional seconds precision differs ({a.FractionDigits} vs {b.FractionDigits} digits); equal at the coarser precision";
                return CellOutcome.EqualWithWarning;
            }
        }

        return CellOutcome.Different;
    }

    public static bool HasOrderBy(string query) => OrderByRegex().IsMatch(query);

    public static string TypeText(ColumnInfo c)
    {
        string size = c.ColumnSizeClamped > 0 ? $"({c.SizeText})" : "";
        string name = string.IsNullOrEmpty(c.TypeName) ? "" : $" {c.TypeName}";
        return $"{c.SqlTypeName}{size}{name}";
    }

    private static string Lower(TypeFamily f) => f.ToString().ToLowerInvariant();

    private static string Show(string? value) => value == null ? Canonicalizer.NullMarker : value.Length > 80 ? value[..77] + "..." : value;

    [GeneratedRegex(@"\bORDER\s+BY\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OrderByRegex();
}
