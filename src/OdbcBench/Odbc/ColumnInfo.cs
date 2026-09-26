namespace OdbcBench.Odbc;

public enum BindMode { Native, WChar }

/// <summary>Coarse type family used to compare metadata and normalise values across drivers that map types differently.</summary>
public enum TypeFamily { Text, Integer, Decimal, Float, Bool, Date, Time, Timestamp, Binary, Guid, Other }

/// <summary>A described result column plus the binding decision made for it.</summary>
public sealed class ColumnInfo
{
    public int Ordinal { get; init; }
    public string Name { get; init; } = "";
    public short SqlType { get; init; }
    public nuint ColumnSize { get; init; }
    public short DecimalDigits { get; init; }
    public short Nullable { get; init; }
    /// <summary>SQL_DESC_LENGTH (characters for text, -1 when unknown).</summary>
    public long Length { get; set; } = -1;
    /// <summary>SQL_DESC_OCTET_LENGTH (bytes, -1 when unknown).</summary>
    public long OctetLength { get; set; } = -1;
    /// <summary>SQL_DESC_DISPLAY_SIZE (characters, -1 when unknown).</summary>
    public long DisplaySize { get; set; } = -1;
    public bool Unsigned { get; set; }
    public string TypeName { get; set; } = "";

    // ---- binding decision (filled by TypeMapper / BindingPlan)
    public short CType { get; set; }
    /// <summary>Bytes per bound element (0 for long columns read with SQLGetData).</summary>
    public int ElementBytes { get; set; }
    /// <summary>True when the column cannot be bound with a bounded buffer and is read with SQLGetData.</summary>
    public bool IsLong { get; set; }
    /// <summary>True when a long column was bound with a capped buffer (longColumnMode = bindCapped).</summary>
    public bool Capped { get; set; }
    /// <summary>True when the native binding was rejected (07006) and the column fell back to SQL_C_WCHAR.</summary>
    public bool FellBackToText { get; set; }

    public TypeFamily Family => TypeMapper.FamilyOf(SqlType);
    public string SqlTypeName => TypeMapper.SqlTypeName(SqlType);
    public string CTypeName => TypeMapper.CTypeName(CType);
    public long ColumnSizeClamped => (ulong)ColumnSize > long.MaxValue ? long.MaxValue : (long)(ulong)ColumnSize;

    public string SizeText => DecimalDigits > 0 ? $"{ColumnSizeClamped},{DecimalDigits}" : ColumnSizeClamped.ToString();

    public string BindingText => IsLong
        ? $"SQLGetData as {CTypeName} (long)"
        : $"{CTypeName} x {ElementBytes} B{(Capped ? " (capped)" : "")}{(FellBackToText ? " (text fallback)" : "")}";
}

/// <summary>SQL type to C type mapping (plan section "Type mapping").</summary>
public static class TypeMapper
{
    public static TypeFamily FamilyOf(short sqlType) => sqlType switch
    {
        Native.SQL_CHAR or Native.SQL_VARCHAR or Native.SQL_LONGVARCHAR or
        Native.SQL_WCHAR or Native.SQL_WVARCHAR or Native.SQL_WLONGVARCHAR => TypeFamily.Text,
        Native.SQL_TINYINT or Native.SQL_SMALLINT or Native.SQL_INTEGER or Native.SQL_BIGINT => TypeFamily.Integer,
        Native.SQL_DECIMAL or Native.SQL_NUMERIC => TypeFamily.Decimal,
        Native.SQL_REAL or Native.SQL_FLOAT or Native.SQL_DOUBLE => TypeFamily.Float,
        Native.SQL_BIT => TypeFamily.Bool,
        Native.SQL_TYPE_DATE or Native.SQL_DATE => TypeFamily.Date,
        Native.SQL_TYPE_TIME or Native.SQL_TIME or Native.SQL_SS_TIME2 => TypeFamily.Time,
        Native.SQL_TYPE_TIMESTAMP or Native.SQL_TIMESTAMP or Native.SQL_SS_TIMESTAMPOFFSET => TypeFamily.Timestamp,
        Native.SQL_BINARY or Native.SQL_VARBINARY or Native.SQL_LONGVARBINARY => TypeFamily.Binary,
        Native.SQL_GUID => TypeFamily.Guid,
        _ => TypeFamily.Other,
    };

    public static bool IsInterval(short sqlType) => sqlType is >= Native.SQL_INTERVAL_YEAR and <= Native.SQL_INTERVAL_MINUTE_TO_SECOND;

    /// <summary>Chooses the SQL_C_* type and element width for a column, or marks it long.</summary>
    public static void Apply(ColumnInfo c, BindMode mode, long longThresholdBytes)
    {
        c.IsLong = false;
        c.Capped = false;
        if (mode == BindMode.WChar)
        {
            BindAsText(c, TextCharsForWCharMode(c), longThresholdBytes);
            return;
        }

        switch (c.SqlType)
        {
            case Native.SQL_CHAR:
            case Native.SQL_VARCHAR:
            case Native.SQL_WCHAR:
            case Native.SQL_WVARCHAR:
                BindAsText(c, MaxOf(c.ColumnSizeClamped, c.Length, c.DisplaySize), longThresholdBytes);
                break;
            case Native.SQL_LONGVARCHAR:
            case Native.SQL_WLONGVARCHAR:
                // Bound only when the driver reports a real, small size; otherwise read with SQLGetData.
                BindAsText(c, MaxOf(c.ColumnSizeClamped, c.Length, c.DisplaySize), longThresholdBytes);
                break;
            case Native.SQL_DECIMAL:
            case Native.SQL_NUMERIC:
            {
                long precision = c.ColumnSizeClamped > 0 && c.ColumnSizeClamped < 1000 ? c.ColumnSizeClamped : 38;
                BindAsText(c, precision + 6, longThresholdBytes); // sign, point, leading zero, exponent slack
                break;
            }
            case Native.SQL_TINYINT: Set(c, c.Unsigned ? Native.SQL_C_UTINYINT : Native.SQL_C_STINYINT, 1); break;
            case Native.SQL_SMALLINT: Set(c, c.Unsigned ? Native.SQL_C_USHORT : Native.SQL_C_SSHORT, 2); break;
            case Native.SQL_INTEGER: Set(c, c.Unsigned ? Native.SQL_C_ULONG : Native.SQL_C_SLONG, 4); break;
            case Native.SQL_BIGINT: Set(c, c.Unsigned ? Native.SQL_C_UBIGINT : Native.SQL_C_SBIGINT, 8); break;
            case Native.SQL_REAL: Set(c, Native.SQL_C_FLOAT, 4); break;
            case Native.SQL_FLOAT:
            case Native.SQL_DOUBLE: Set(c, Native.SQL_C_DOUBLE, 8); break;
            case Native.SQL_BIT: Set(c, Native.SQL_C_BIT, 1); break;
            case Native.SQL_TYPE_DATE:
            case Native.SQL_DATE: Set(c, Native.SQL_C_TYPE_DATE, 6); break;
            case Native.SQL_TYPE_TIME:
            case Native.SQL_TIME:
                if (c.DecimalDigits > 0) BindAsText(c, 8 + 1 + c.DecimalDigits + 4, longThresholdBytes); // SQL_TIME_STRUCT has no fraction
                else Set(c, Native.SQL_C_TYPE_TIME, 6);
                break;
            case Native.SQL_TYPE_TIMESTAMP:
            case Native.SQL_TIMESTAMP: Set(c, Native.SQL_C_TYPE_TIMESTAMP, 16); break;
            case Native.SQL_GUID: Set(c, Native.SQL_C_GUID, 16); break;
            case Native.SQL_BINARY:
            case Native.SQL_VARBINARY:
            case Native.SQL_LONGVARBINARY:
            {
                long bytes = MaxOf(c.ColumnSizeClamped, c.OctetLength, c.Length);
                if (bytes <= 0 || bytes > longThresholdBytes) MarkLong(c, Native.SQL_C_BINARY);
                else Set(c, Native.SQL_C_BINARY, (int)bytes);
                break;
            }
            default:
                // Intervals, SQL Server specific types (time2, datetimeoffset, xml, variant, udt) and unknown codes: text.
                BindAsText(c, c.DisplaySize > 0 ? c.DisplaySize : IsInterval(c.SqlType) ? 64 : 0, longThresholdBytes);
                break;
        }
    }

    /// <summary>Forces SQL_C_WCHAR binding (used after a 07006 rejection of the native type).</summary>
    public static void ForceText(ColumnInfo c, long longThresholdBytes)
    {
        c.FellBackToText = true;
        c.IsLong = false;
        c.Capped = false;
        BindAsText(c, TextCharsForWCharMode(c), longThresholdBytes);
    }

    /// <summary>Character count for a text rendering of the column (wchar bind mode and text fallback).</summary>
    public static long TextCharsForWCharMode(ColumnInfo c)
    {
        if (c.DisplaySize > 0) return c.DisplaySize;
        return c.Family switch
        {
            TypeFamily.Text => MaxOf(c.ColumnSizeClamped, c.Length),
            TypeFamily.Integer => 21,
            TypeFamily.Float => 24,
            TypeFamily.Decimal => (c.ColumnSizeClamped > 0 && c.ColumnSizeClamped < 1000 ? c.ColumnSizeClamped : 38) + 6,
            TypeFamily.Bool => 1,
            TypeFamily.Date => 10,
            TypeFamily.Time => 8 + (c.DecimalDigits > 0 ? 1 + c.DecimalDigits : 0),
            TypeFamily.Timestamp => 19 + (c.DecimalDigits > 0 ? 1 + c.DecimalDigits : 0) + 7,
            TypeFamily.Guid => 36,
            TypeFamily.Binary => c.ColumnSizeClamped > 0 ? c.ColumnSizeClamped * 2 : 0,
            _ => IsInterval(c.SqlType) ? 64 : 0,
        };
    }

    private static void BindAsText(ColumnInfo c, long chars, long longThresholdBytes)
    {
        if (chars <= 0 || chars > int.MaxValue / 4) { MarkLong(c, Native.SQL_C_WCHAR); return; }
        long bytes = (chars + 1) * 2;
        if (bytes > longThresholdBytes) { MarkLong(c, Native.SQL_C_WCHAR); return; }
        Set(c, Native.SQL_C_WCHAR, (int)bytes);
    }

    private static void Set(ColumnInfo c, short cType, int elementBytes)
    {
        c.CType = cType;
        c.ElementBytes = elementBytes;
        c.IsLong = false;
    }

    private static void MarkLong(ColumnInfo c, short cType)
    {
        c.CType = cType;
        c.ElementBytes = 0;
        c.IsLong = true;
    }

    private static long MaxOf(long a, long b) => Math.Max(a, b);
    private static long MaxOf(long a, long b, long c) => Math.Max(a, Math.Max(b, c));

    /// <summary>Bytes a fixed-width C type occupies (0 for variable-width types).</summary>
    public static int FixedSize(short cType) => cType switch
    {
        Native.SQL_C_SBIGINT or Native.SQL_C_UBIGINT or Native.SQL_C_DOUBLE => 8,
        Native.SQL_C_SLONG or Native.SQL_C_ULONG or Native.SQL_C_FLOAT => 4,
        Native.SQL_C_SSHORT or Native.SQL_C_USHORT => 2,
        Native.SQL_C_STINYINT or Native.SQL_C_UTINYINT or Native.SQL_C_BIT => 1,
        Native.SQL_C_TYPE_DATE or Native.SQL_C_TYPE_TIME => 6,
        Native.SQL_C_TYPE_TIMESTAMP or Native.SQL_C_GUID => 16,
        Native.SQL_C_NUMERIC => 19,
        _ => 0,
    };

    public static bool IsVariableWidth(short cType) => cType is Native.SQL_C_WCHAR or Native.SQL_C_CHAR or Native.SQL_C_BINARY;

    public static string SqlTypeName(short t) => t switch
    {
        Native.SQL_CHAR => "CHAR", Native.SQL_VARCHAR => "VARCHAR", Native.SQL_LONGVARCHAR => "LONGVARCHAR",
        Native.SQL_WCHAR => "WCHAR", Native.SQL_WVARCHAR => "WVARCHAR", Native.SQL_WLONGVARCHAR => "WLONGVARCHAR",
        Native.SQL_DECIMAL => "DECIMAL", Native.SQL_NUMERIC => "NUMERIC",
        Native.SQL_TINYINT => "TINYINT", Native.SQL_SMALLINT => "SMALLINT", Native.SQL_INTEGER => "INTEGER", Native.SQL_BIGINT => "BIGINT",
        Native.SQL_REAL => "REAL", Native.SQL_FLOAT => "FLOAT", Native.SQL_DOUBLE => "DOUBLE", Native.SQL_BIT => "BIT",
        Native.SQL_TYPE_DATE => "TYPE_DATE", Native.SQL_TYPE_TIME => "TYPE_TIME", Native.SQL_TYPE_TIMESTAMP => "TYPE_TIMESTAMP",
        Native.SQL_DATE => "DATE", Native.SQL_TIME => "TIME", Native.SQL_TIMESTAMP => "TIMESTAMP",
        Native.SQL_GUID => "GUID", Native.SQL_BINARY => "BINARY", Native.SQL_VARBINARY => "VARBINARY", Native.SQL_LONGVARBINARY => "LONGVARBINARY",
        Native.SQL_SS_TIME2 => "SS_TIME2", Native.SQL_SS_TIMESTAMPOFFSET => "SS_TIMESTAMPOFFSET", Native.SQL_SS_XML => "SS_XML",
        Native.SQL_SS_VARIANT => "SS_VARIANT", Native.SQL_SS_UDT => "SS_UDT", Native.SQL_SS_TABLE => "SS_TABLE",
        >= Native.SQL_INTERVAL_YEAR and <= Native.SQL_INTERVAL_MINUTE_TO_SECOND => $"INTERVAL({t})",
        Native.SQL_UNKNOWN_TYPE => "UNKNOWN",
        _ => $"type({t})",
    };

    public static string CTypeName(short t) => t switch
    {
        Native.SQL_C_CHAR => "SQL_C_CHAR", Native.SQL_C_WCHAR => "SQL_C_WCHAR",
        Native.SQL_C_SSHORT => "SQL_C_SSHORT", Native.SQL_C_USHORT => "SQL_C_USHORT",
        Native.SQL_C_SLONG => "SQL_C_SLONG", Native.SQL_C_ULONG => "SQL_C_ULONG",
        Native.SQL_C_STINYINT => "SQL_C_STINYINT", Native.SQL_C_UTINYINT => "SQL_C_UTINYINT",
        Native.SQL_C_SBIGINT => "SQL_C_SBIGINT", Native.SQL_C_UBIGINT => "SQL_C_UBIGINT",
        Native.SQL_C_FLOAT => "SQL_C_FLOAT", Native.SQL_C_DOUBLE => "SQL_C_DOUBLE", Native.SQL_C_BIT => "SQL_C_BIT",
        Native.SQL_C_BINARY => "SQL_C_BINARY", Native.SQL_C_TYPE_DATE => "SQL_C_TYPE_DATE", Native.SQL_C_TYPE_TIME => "SQL_C_TYPE_TIME",
        Native.SQL_C_TYPE_TIMESTAMP => "SQL_C_TYPE_TIMESTAMP", Native.SQL_C_GUID => "SQL_C_GUID", Native.SQL_C_NUMERIC => "SQL_C_NUMERIC",
        _ => $"SQL_C({t})",
    };
}
