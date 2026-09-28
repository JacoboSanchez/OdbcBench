using OdbcBench.Odbc;
using OdbcBench.Validation;

namespace OdbcBench.Insert;

/// <summary>What happens to the target table before every iteration (never timed).</summary>
public enum CleanupMode
{
    /// <summary>Nothing: every iteration adds its rows, so the table must accept duplicates.</summary>
    None,
    /// <summary>DELETE FROM the table.</summary>
    Delete,
    /// <summary>TRUNCATE TABLE.</summary>
    Truncate,
}

/// <summary>
/// The table one DSN writes to: its columns as the driver describes them, the INSERT statement built from them and
/// the helper statements around an iteration. Described on first use and shared by every series of that DSN.
/// </summary>
public sealed class InsertTarget
{
    /// <summary>Name of the validation sample that holds the generated values every read-back is compared with.</summary>
    public const string SentName = "(sent)";

    private readonly BindMode _bindMode;
    private readonly int _valueLength;
    private readonly List<string> _notes = new();
    private List<InsertColumn>? _columns;

    public string Table { get; }
    /// <summary>Columns to fill as configured; null means every column of the table that accepts a value.</summary>
    public IReadOnlyList<string>? RequestedColumns { get; }
    public string CountSql { get; }
    /// <summary>Null when the table is left as it is between iterations.</summary>
    public string? CleanupSql { get; }

    public InsertTarget(string table, IReadOnlyList<string>? columns, BindMode bindMode, int valueLength, CleanupMode cleanup)
    {
        Table = table;
        RequestedColumns = columns is { Count: > 0 } ? columns : null;
        _bindMode = bindMode;
        _valueLength = valueLength;
        CountSql = $"SELECT COUNT(*) FROM {table}";
        CleanupSql = cleanup switch
        {
            CleanupMode.Delete => $"DELETE FROM {table}",
            CleanupMode.Truncate => $"TRUNCATE TABLE {table}",
            _ => null,
        };
    }

    public bool Resolved => _columns != null;
    public IReadOnlyList<InsertColumn> Columns => _columns ?? throw new InvalidOperationException($"table {Table} has not been described yet");
    /// <summary>Columns left out of the INSERT and why.</summary>
    public IReadOnlyList<string> Notes => _notes;
    public string DescribeSql => $"SELECT {(RequestedColumns == null ? "*" : string.Join(", ", RequestedColumns))} FROM {Table} WHERE 1 = 0";
    public string InsertSql { get; private set; } = "";
    /// <summary>Reads the filled columns back in row order, for the validation.</summary>
    public string ReadBackSql { get; private set; } = "";

    /// <summary>The statement as far as it is known before the table was described (for reports of failed runs).</summary>
    public string InsertSqlOrOutline => Resolved
        ? InsertSql
        : $"INSERT INTO {Table} ({(RequestedColumns == null ? "every column" : string.Join(", ", RequestedColumns))}) VALUES (...)";

    /// <summary>
    /// Describes the target columns with an empty SELECT, the one way every driver supports, and decides what is
    /// generated and bound for each of them. Does nothing when the table was already described.
    /// </summary>
    public void Resolve(OdbcConnection connection)
    {
        if (Resolved) return;

        var described = new List<ColumnInfo>();
        var autoUnique = new HashSet<int>();
        using (var statement = new OdbcStatement(connection))
        {
            try
            {
                statement.ExecDirect(DescribeSql);
                short count = statement.NumResultCols();
                for (int i = 1; i <= count; i++)
                {
                    described.Add(statement.DescribeColumn(i));
                    if (statement.NumericAttribute(i, Native.SQL_DESC_AUTO_UNIQUE_VALUE) == Native.SQL_TRUE) autoUnique.Add(i);
                }
            }
            finally
            {
                statement.TryCloseCursor();
                connection.TryRollback(); // the SELECT opened a transaction when autocommit is off
            }
        }

        Build(described, autoUnique, connection.GetInfoString(Native.SQL_IDENTIFIER_QUOTE_CHAR));
    }

    /// <summary>
    /// Decides the generated values and bindings from the described columns and builds the statements.
    /// <paramref name="autoUnique"/> holds the ordinals of auto-increment columns; <paramref name="quote"/> is SQL_IDENTIFIER_QUOTE_CHAR.
    /// </summary>
    internal void Build(IReadOnlyList<ColumnInfo> described, IReadOnlySet<int> autoUnique, string quote)
    {
        if (described.Count == 0)
            throw new InvalidOperationException($"'{DescribeSql}' returned no columns");
        if (RequestedColumns != null && RequestedColumns.Count != described.Count)
            throw new InvalidOperationException($"insert.columns lists {RequestedColumns.Count} columns but '{DescribeSql}' returned {described.Count}");

        quote = quote.Trim();
        var notes = new List<string>();
        var columns = new List<InsertColumn>(described.Count);
        for (int i = 0; i < described.Count; i++)
        {
            var c = described[i];
            string sqlName = RequestedColumns != null ? RequestedColumns[i] : Quote(c.Name, quote);
            if (RequestedColumns == null && autoUnique.Contains(c.Ordinal))
            {
                notes.Add($"column '{c.Name}' is auto-increment and was left out of the INSERT");
                continue;
            }
            var column = ParameterMapper.Map(c, sqlName, _bindMode, _valueLength);
            if (column != null)
            {
                columns.Add(column);
                continue;
            }
            if (RequestedColumns != null)
                throw new NotSupportedException($"column '{c.Name}' ({c.SqlTypeName}): the generator has no values for this type; remove it from insert.columns");
            notes.Add($"column '{c.Name}' ({c.SqlTypeName}) has a type the generator has no values for and was left out of the INSERT");
        }
        if (columns.Count == 0)
            throw new InvalidOperationException($"no column of {Table} can be filled: {string.Join("; ", notes)}");

        string names = string.Join(", ", columns.Select(c => c.SqlName));
        InsertSql = $"INSERT INTO {Table} ({names}) VALUES ({string.Join(", ", columns.Select(_ => "?"))})";
        ReadBackSql = $"SELECT {names} FROM {Table} ORDER BY {OrderColumn(columns).SqlName}";
        _notes.AddRange(notes);
        _columns = columns;
    }

    /// <summary>The first <paramref name="rows"/> generated rows in text form, shaped like a validation sample.</summary>
    public SampleResult SentSample(int rows)
    {
        var columns = Columns;
        var sample = new SampleResult { DsnName = SentName, Columns = columns.Select(c => c.Column).ToList() };
        for (long row = 1; row <= rows; row++)
            sample.Rows.Add(columns.Select(c => (string?)DataGenerator.Text(c, row)).ToArray());
        return sample;
    }

    /// <summary>The column whose values follow the row number most faithfully: a wide integer, else a text with the whole key, else the first.</summary>
    private static InsertColumn OrderColumn(List<InsertColumn> columns) =>
        columns.FirstOrDefault(c => c.Kind == ValueKind.Integer && c.Modulus is 0 or >= 1L << 31)
        ?? columns.FirstOrDefault(c => c.Kind == ValueKind.Text && c.Length >= 10)
        ?? columns.FirstOrDefault(c => c.Kind == ValueKind.Integer)
        ?? columns[0];

    private static string Quote(string name, string quote) =>
        quote.Length == 0 ? name : quote + name.Replace(quote, quote + quote) + quote;
}
