using OdbcBench.Odbc;

namespace OdbcBench.Insert;

/// <summary>What the generator writes for a column, independent of how it is bound.</summary>
public enum ValueKind { Integer, Float, Decimal, Bit, Date, Time, Timestamp, TimestampOffset, Guid, Binary, Text }

/// <summary>A target column, the values generated for it and the parameter binding chosen for it.</summary>
public sealed class InsertColumn
{
    public required ColumnInfo Column { get; init; }
    /// <summary>The name as written in the INSERT statement: quoted when it came from the driver, verbatim when configured.</summary>
    public required string SqlName { get; init; }
    public ValueKind Kind { get; init; }
    /// <summary>Sent as UTF-16 text (SQL_C_WCHAR) that the driver converts, instead of the C type of its kind.</summary>
    public bool AsText { get; internal set; }
    /// <summary>
    /// A boolean the driver describes as text: its text form is true/false, which the server casts. Denodo's
    /// single-row INSERT, for one, rejects '1' for a boolean column.
    /// </summary>
    public bool BoolWords { get; init; }
    /// <summary>Characters (text) or bytes (binary) of every generated value.</summary>
    public int Length { get; init; }
    /// <summary>Integers: values are the row number modulo this; 0 means the row number itself.</summary>
    public long Modulus { get; init; }
    /// <summary>REAL: a 4-byte float instead of a double.</summary>
    public bool Single { get; init; }
    /// <summary>Decimals: digits before the point.</summary>
    public int IntegerDigits { get; init; }
    /// <summary>Decimals: digits after the point.</summary>
    public int Scale { get; init; }

    /// <summary>Characters of the text form of a value (what is sent in text bindings and compared by the validation).</summary>
    public int TextChars => Kind switch
    {
        ValueKind.Integer => 19,
        ValueKind.Float => 24,
        ValueKind.Decimal => Math.Max(1, IntegerDigits) + (Scale > 0 ? 1 + Scale : 0),
        ValueKind.Bit => BoolWords ? 5 : 1,
        ValueKind.Date => 10,
        ValueKind.Time => 8,
        ValueKind.Timestamp => 19,
        ValueKind.TimestampOffset => 26,
        ValueKind.Guid => 36,
        ValueKind.Binary => Length * 2,
        _ => Length,
    };
}

/// <summary>SQL type to generated value and parameter binding (the insert counterpart of <see cref="TypeMapper"/>).</summary>
public static class ParameterMapper
{
    /// <summary>
    /// Chooses the value kind, the SQL_C_* type and the element width for a target column, or returns null when the
    /// generator has no value for its type. Long columns are no obstacle: the buffer is sized by the generated value.
    /// </summary>
    /// <param name="valueType">
    /// The column's logical SQL type when the caller knows it (init does). It picks the generated values, never the
    /// binding, and only where the description loses it: Oracle describes DATE as a timestamp and its NUMBER-based
    /// integers and booleans as decimals.
    /// </param>
    public static InsertColumn? Map(ColumnInfo c, string sqlName, BindMode mode, int valueLength, short? valueType = null)
    {
        long size = c.ColumnSizeClamped;
        int variable = (int)Math.Max(1, size > 0 ? Math.Min(size, valueLength) : valueLength);
        short type = valueType is short logical &&
            c.SqlType is Native.SQL_DECIMAL or Native.SQL_NUMERIC or Native.SQL_TYPE_TIMESTAMP or Native.SQL_TIMESTAMP
            ? logical : c.SqlType;

        InsertColumn? column = type switch
        {
            Native.SQL_TINYINT => Integer(c, sqlName, 1L << 7),
            Native.SQL_SMALLINT => Integer(c, sqlName, 1L << 15),
            Native.SQL_INTEGER => Integer(c, sqlName, 1L << 31),
            Native.SQL_BIGINT => Integer(c, sqlName, 0),
            Native.SQL_REAL => new InsertColumn { Column = c, SqlName = sqlName, Kind = ValueKind.Float, Single = true },
            Native.SQL_FLOAT or Native.SQL_DOUBLE => new InsertColumn { Column = c, SqlName = sqlName, Kind = ValueKind.Float },
            Native.SQL_DECIMAL or Native.SQL_NUMERIC => Decimal(c, sqlName),
            Native.SQL_BIT => new InsertColumn { Column = c, SqlName = sqlName, Kind = ValueKind.Bit },
            Native.SQL_TYPE_DATE or Native.SQL_DATE => new InsertColumn { Column = c, SqlName = sqlName, Kind = ValueKind.Date },
            // SQL_TIME_STRUCT has no fraction, and time2 has no C struct in plain ODBC: both travel as text.
            Native.SQL_TYPE_TIME or Native.SQL_TIME => new InsertColumn { Column = c, SqlName = sqlName, Kind = ValueKind.Time, AsText = c.DecimalDigits > 0 },
            Native.SQL_SS_TIME2 => new InsertColumn { Column = c, SqlName = sqlName, Kind = ValueKind.Time, AsText = true },
            Native.SQL_TYPE_TIMESTAMP or Native.SQL_TIMESTAMP => new InsertColumn { Column = c, SqlName = sqlName, Kind = ValueKind.Timestamp },
            Native.SQL_SS_TIMESTAMPOFFSET => new InsertColumn { Column = c, SqlName = sqlName, Kind = ValueKind.TimestampOffset, AsText = true },
            Native.SQL_GUID => new InsertColumn { Column = c, SqlName = sqlName, Kind = ValueKind.Guid },
            Native.SQL_BINARY or Native.SQL_VARBINARY or Native.SQL_LONGVARBINARY =>
                new InsertColumn { Column = c, SqlName = sqlName, Kind = ValueKind.Binary, Length = variable },
            // psqlODBC and the drivers derived from it describe boolean as a short character column.
            Native.SQL_CHAR or Native.SQL_VARCHAR or Native.SQL_WCHAR or Native.SQL_WVARCHAR when TypeMapper.IsBooleanTypeName(c.TypeName) =>
                new InsertColumn { Column = c, SqlName = sqlName, Kind = ValueKind.Bit, AsText = true, BoolWords = true },
            Native.SQL_CHAR or Native.SQL_VARCHAR or Native.SQL_LONGVARCHAR or
            Native.SQL_WCHAR or Native.SQL_WVARCHAR or Native.SQL_WLONGVARCHAR =>
                new InsertColumn { Column = c, SqlName = sqlName, Kind = ValueKind.Text, Length = variable, AsText = true },
            _ => null,
        };
        if (column == null) return null;

        // Binary stays SQL_C_BINARY in wchar mode: hex text as a parameter is not portable across drivers.
        if (mode == BindMode.WChar && column.Kind != ValueKind.Binary) column.AsText = true;
        Apply(column);
        return column;
    }

    /// <summary>Forces SQL_C_WCHAR binding after the driver rejected the native C type. False when there is no text form to send.</summary>
    public static bool ForceText(InsertColumn column)
    {
        if (column.AsText || column.Kind == ValueKind.Binary) return false;
        column.AsText = true;
        column.Column.FellBackToText = true;
        Apply(column);
        return true;
    }

    private static InsertColumn Integer(ColumnInfo c, string sqlName, long modulus) =>
        new() { Column = c, SqlName = sqlName, Kind = ValueKind.Integer, Modulus = modulus };

    private static InsertColumn Decimal(ColumnInfo c, string sqlName)
    {
        long size = c.ColumnSizeClamped;
        int precision = size is > 0 and <= 38 ? (int)size : 18;
        int scale = Math.Clamp((int)c.DecimalDigits, 0, precision);
        return new InsertColumn
        {
            Column = c, SqlName = sqlName, Kind = ValueKind.Decimal, AsText = true,
            IntegerDigits = Math.Min(precision - scale, 18), Scale = scale,
        };
    }

    private static void Apply(InsertColumn column)
    {
        var c = column.Column;
        c.IsLong = false;
        c.Capped = false;
        if (column.AsText)
        {
            c.CType = Native.SQL_C_WCHAR;
            c.ElementBytes = (column.TextChars + 1) * sizeof(char);
            return;
        }

        (c.CType, c.ElementBytes) = column.Kind switch
        {
            ValueKind.Integer => c.SqlType switch
            {
                Native.SQL_TINYINT => (c.Unsigned ? Native.SQL_C_UTINYINT : Native.SQL_C_STINYINT, 1),
                Native.SQL_SMALLINT => (c.Unsigned ? Native.SQL_C_USHORT : Native.SQL_C_SSHORT, 2),
                Native.SQL_INTEGER => (c.Unsigned ? Native.SQL_C_ULONG : Native.SQL_C_SLONG, 4),
                _ => (c.Unsigned ? Native.SQL_C_UBIGINT : Native.SQL_C_SBIGINT, 8),
            },
            ValueKind.Float => column.Single ? (Native.SQL_C_FLOAT, 4) : (Native.SQL_C_DOUBLE, 8),
            ValueKind.Bit => (Native.SQL_C_BIT, 1),
            ValueKind.Date => (Native.SQL_C_TYPE_DATE, 6),
            ValueKind.Time => (Native.SQL_C_TYPE_TIME, 6),
            ValueKind.Timestamp => (Native.SQL_C_TYPE_TIMESTAMP, 16),
            ValueKind.Guid => (Native.SQL_C_GUID, 16),
            ValueKind.Binary => (Native.SQL_C_BINARY, column.Length),
            _ => throw new InvalidOperationException($"column '{c.Name}': {column.Kind} values have no native C type"),
        };
    }
}
