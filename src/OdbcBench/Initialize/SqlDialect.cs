using System.Security.Cryptography;
using System.Text;

namespace OdbcBench.Initialize;

/// <summary>Small DDL dialect used only to create the portable benchmark schema.</summary>
public abstract class SqlDialect
{
    public abstract string Name { get; }
    protected abstract string OpenQuote { get; }
    protected abstract string CloseQuote { get; }
    /// <summary>Longest identifier, in bytes, that every supported server version accepts.</summary>
    public abstract int MaxIdentifierLength { get; }

    public virtual string Quote(string identifier) => OpenQuote + identifier.Replace(CloseQuote, CloseQuote + CloseQuote) + CloseQuote;
    public string Qualify(string schema, string table) => schema.Length == 0 ? Quote(table) : $"{Quote(schema)}.{Quote(table)}";

    public bool FitsIdentifier(string name) => Encoding.UTF8.GetByteCount(name) <= MaxIdentifierLength;

    /// <summary>
    /// Returns a generated name unchanged when it fits the identifier limit, otherwise a prefix plus a hash of the full
    /// name, so long names stay valid, distinct and identical on every run.
    /// </summary>
    public string FitIdentifier(string name)
    {
        if (FitsIdentifier(name)) return name;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)), 0, 4).ToLowerInvariant();
        int budget = MaxIdentifierLength - hash.Length - 1;
        int keep = Math.Min(name.Length, budget);
        while (keep > 0 && (Encoding.UTF8.GetByteCount(name.AsSpan(0, keep)) > budget || char.IsHighSurrogate(name[keep - 1])))
            keep--;
        return $"{name[..keep].TrimEnd('_')}_{hash}";
    }

    public abstract string Type(SqlType type, int size = 0);
    public virtual string? SchemaExistsSql(string schema) =>
        $"SELECT 1 FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{Literal(schema)}'";
    public virtual string? CreateSchemaSql(string schema) => schema.Length == 0 ? null : $"CREATE SCHEMA {Quote(schema)}";
    // Names no column, so a table with a generated name but a different layout still counts as existing.
    public virtual string TableProbeSql(string qualifiedTable) => $"SELECT 1 FROM {qualifiedTable} WHERE 1 = 0";
    public virtual string DropTableSql(string qualifiedTable) => $"DROP TABLE {qualifiedTable}";
    public string CreateIndexSql(string schema, string indexName, string qualifiedTable, string column, bool unique) =>
        $"CREATE {(unique ? "UNIQUE " : "")}INDEX {IndexReference(schema, indexName)} ON {qualifiedTable} ({Quote(column)})";
    // PostgreSQL and SQL Server always put an index in its table's schema and reject a schema-qualified index name.
    protected virtual string IndexReference(string schema, string indexName) => Quote(indexName);
    public abstract string? AnalyzeSql(string schema, string table);

    public static SqlDialect Detect(string dbmsName)
    {
        if (dbmsName.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase)) return new PostgreSqlDialect();
        if (dbmsName.Contains("SQL Server", StringComparison.OrdinalIgnoreCase)) return new SqlServerDialect();
        if (dbmsName.Contains("Oracle", StringComparison.OrdinalIgnoreCase)) return new OracleDialect();
        throw new NotSupportedException($"init does not support DBMS '{dbmsName}'; supported DBMSs are PostgreSQL, SQL Server and Oracle");
    }

    protected static string Literal(string value) => value.Replace("'", "''");
}

public enum SqlType
{
    SmallInt, Integer, BigInt, Real, Double, Decimal, Boolean, Date, Timestamp, VarChar, Binary,
}

internal sealed class PostgreSqlDialect : SqlDialect
{
    public override string Name => "PostgreSQL";
    protected override string OpenQuote => "\"";
    protected override string CloseQuote => "\"";
    public override int MaxIdentifierLength => 63; // NAMEDATALEN - 1; longer names are silently truncated.
    public override string Type(SqlType type, int size = 0) => type switch
    {
        SqlType.SmallInt => "SMALLINT",
        SqlType.Integer => "INTEGER",
        SqlType.BigInt => "BIGINT",
        SqlType.Real => "REAL",
        SqlType.Double => "DOUBLE PRECISION",
        SqlType.Decimal => "DECIMAL(18,4)",
        SqlType.Boolean => "BOOLEAN",
        SqlType.Date => "DATE",
        SqlType.Timestamp => "TIMESTAMP",
        SqlType.VarChar => $"VARCHAR({size})",
        SqlType.Binary => "BYTEA",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
    public override string? AnalyzeSql(string schema, string table) => $"ANALYZE {Qualify(schema, table)}";
}

internal sealed class SqlServerDialect : SqlDialect
{
    public override string Name => "SQL Server";
    protected override string OpenQuote => "[";
    protected override string CloseQuote => "]";
    public override int MaxIdentifierLength => 128;
    public override string Type(SqlType type, int size = 0) => type switch
    {
        SqlType.SmallInt => "SMALLINT",
        SqlType.Integer => "INTEGER",
        SqlType.BigInt => "BIGINT",
        SqlType.Real => "REAL",
        SqlType.Double => "FLOAT(53)",
        SqlType.Decimal => "DECIMAL(18,4)",
        SqlType.Boolean => "BIT",
        SqlType.Date => "DATE",
        SqlType.Timestamp => "DATETIME2(6)",
        SqlType.VarChar => $"VARCHAR({size})",
        SqlType.Binary => $"VARBINARY({size})",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
    public override string? AnalyzeSql(string schema, string table) => $"UPDATE STATISTICS {Qualify(schema, table)}";
}

internal sealed class OracleDialect : SqlDialect
{
    public override string Name => "Oracle";
    protected override string OpenQuote => "\"";
    protected override string CloseQuote => "\"";
    // 128 bytes from 12.2, but only when the database's COMPATIBLE setting is also 12.2 or later; 30 works everywhere.
    public override int MaxIdentifierLength => 30;
    // Unquoted Oracle identifiers fold to upper case. Generate conventional quoted names that remain addressable
    // through drivers which return upper-case metadata, even when the config used lower case.
    public override string Quote(string identifier) => base.Quote(identifier.ToUpperInvariant());
    // Without a schema, Oracle creates the index in the login user's schema rather than in the table's.
    protected override string IndexReference(string schema, string indexName) => Qualify(schema, indexName);
    public override string Type(SqlType type, int size = 0) => type switch
    {
        SqlType.SmallInt => "NUMBER(5)",
        SqlType.Integer => "NUMBER(10)",
        SqlType.BigInt => "NUMBER(19)",
        SqlType.Real => "BINARY_FLOAT",
        SqlType.Double => "BINARY_DOUBLE",
        SqlType.Decimal => "NUMBER(18,4)",
        SqlType.Boolean => "NUMBER(1)",
        SqlType.Date => "DATE",
        SqlType.Timestamp => "TIMESTAMP(6)",
        SqlType.VarChar => $"VARCHAR2({size})",
        SqlType.Binary => $"RAW({size})",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
    public override string? SchemaExistsSql(string schema) => schema.Length == 0 ? null
        : $"SELECT 1 FROM ALL_USERS WHERE USERNAME = UPPER('{Literal(schema)}')";
    public override string? CreateSchemaSql(string schema) => null; // Oracle schemas are users and cannot be created as ordinary namespaces.
    public override string? AnalyzeSql(string schema, string table) => null;
}
