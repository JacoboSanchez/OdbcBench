using System.Security.Cryptography;
using System.Text;

namespace OdbcBench.Initialize;

/// <summary>Small DDL dialect used only to create the portable benchmark schema.</summary>
public abstract class SqlDialect
{
    public abstract string Name { get; }
    protected abstract string OpenQuote { get; }
    protected abstract string CloseQuote { get; }
    /// <summary>Longest identifier, in <see cref="IdentifierLengthUnit"/>s, that every supported server version accepts.</summary>
    public abstract int MaxIdentifierLength { get; }
    public virtual string IdentifierLengthUnit => "byte";
    // UTF-8 bytes, the usual server encoding for limits that PostgreSQL and Oracle state in bytes.
    protected virtual int IdentifierLength(ReadOnlySpan<char> name) => Encoding.UTF8.GetByteCount(name);

    public virtual string Quote(string identifier) => OpenQuote + identifier.Replace(CloseQuote, CloseQuote + CloseQuote) + CloseQuote;
    public string Qualify(string schema, string table) => schema.Length == 0 ? Quote(table) : $"{Quote(schema)}.{Quote(table)}";

    public bool FitsIdentifier(string name) => IdentifierLength(name) <= MaxIdentifierLength;

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
        while (keep > 0 && (IdentifierLength(name.AsSpan(0, keep)) > budget || char.IsHighSurrogate(name[keep - 1])))
            keep--;
        return $"{name[..keep].TrimEnd('_')}_{hash}";
    }

    /// <summary>Returns the schema that one-part names create objects in.</summary>
    public abstract string CurrentSchemaSql { get; }
    public abstract string Type(SqlType type, int size = 0);
    public virtual string? SchemaExistsSql(string schema) =>
        $"SELECT 1 FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{Literal(schema)}'";
    public virtual string? CreateSchemaSql(string schema) => schema.Length == 0 ? null : $"CREATE SCHEMA {Quote(schema)}";
    // Names no column, so a table with a generated name but a different layout still counts as existing.
    public virtual string TableProbeSql(string qualifiedTable) => $"SELECT 1 FROM {qualifiedTable} WHERE 1 = 0";
    /// <summary>
    /// True when a failed <see cref="TableProbeSql"/> means the table does not exist. Any other failure, such as a
    /// permission error, must stop init before --recreate drops the tables it did find.
    /// </summary>
    public virtual bool IsMissingTable(string? sqlState, int? nativeError) => sqlState is "42S02" or "S0002";
    /// <summary>
    /// Query that returns a row when the account sees every object in the schema, so that <see cref="IsMissingTable"/>
    /// and the catalog conflict checks can be trusted; null when the DBMS reports an unreadable table as a permission
    /// error and lists every relation whatever the account's privileges.
    /// </summary>
    public virtual string? TableVisibilitySql(string schema) => null;
    /// <summary>Query that returns a row when an object other than a table already uses this table name.</summary>
    public abstract string NonTableObjectSql(string schema, string table);
    /// <summary>
    /// Query that returns a row when this index name is already used by anything except an index on the given table,
    /// or null when index names are scoped to their table and cannot collide.
    /// </summary>
    public abstract string? ConflictingIndexSql(string schema, string index, string table);
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
    public override string CurrentSchemaSql => "SELECT current_schema()";
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
    // pg_class lists every relation whatever the account's privileges; tables, views, sequences and indexes share names.
    private static string RelationSql(string schema, string name) =>
        "SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace " +
        $"WHERE n.nspname = {(schema.Length == 0 ? "current_schema()" : $"'{Literal(schema)}'")} AND c.relname = '{Literal(name)}'";
    public override string NonTableObjectSql(string schema, string table) => RelationSql(schema, table) + " AND c.relkind NOT IN ('r', 'p')";
    public override string? ConflictingIndexSql(string schema, string index, string table) =>
        RelationSql(schema, index) + " AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class t ON t.oid = i.indrelid " +
        $"WHERE i.indexrelid = c.oid AND t.relname = '{Literal(table)}')";
    // psqlODBC passes the server's undefined_table state through.
    public override bool IsMissingTable(string? sqlState, int? nativeError) => sqlState == "42P01" || base.IsMissingTable(sqlState, nativeError);
    public override string? AnalyzeSql(string schema, string table) => $"ANALYZE {Qualify(schema, table)}";
}

internal sealed class SqlServerDialect : SqlDialect
{
    public override string Name => "SQL Server";
    protected override string OpenQuote => "[";
    protected override string CloseQuote => "]";
    public override int MaxIdentifierLength => 128;
    // sysname is nvarchar(128): the limit counts UTF-16 characters, not bytes.
    public override string IdentifierLengthUnit => "character";
    protected override int IdentifierLength(ReadOnlySpan<char> name) => name.Length;
    public override string CurrentSchemaSql => "SELECT SCHEMA_NAME()";
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
    public override string NonTableObjectSql(string schema, string table) =>
        $"SELECT 1 FROM sys.objects WHERE schema_id = SCHEMA_ID({(schema.Length == 0 ? "" : $"'{Literal(schema)}'")}) AND name = '{Literal(table)}' AND type <> 'U'";
    public override string? ConflictingIndexSql(string schema, string index, string table) => null; // index names are unique per table only
    // Msg 208, "Invalid object name", whatever SQLSTATE the driver maps it to.
    public override bool IsMissingTable(string? sqlState, int? nativeError) => nativeError == 208 || base.IsMissingTable(sqlState, nativeError);
    public override string? AnalyzeSql(string schema, string table) => $"UPDATE STATISTICS {Qualify(schema, table)}";
}

internal sealed class OracleDialect : SqlDialect
{
    public override string Name => "Oracle";
    protected override string OpenQuote => "\"";
    protected override string CloseQuote => "\"";
    // 128 bytes from 12.2, but only when the database's COMPATIBLE setting is also 12.2 or later; 30 works everywhere.
    public override int MaxIdentifierLength => 30;
    public override string CurrentSchemaSql => "SELECT SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') FROM DUAL";
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
    // ORA-00942, "table or view does not exist", whatever SQLSTATE the driver maps it to.
    public override bool IsMissingTable(string? sqlState, int? nativeError) => nativeError == 942 || base.IsMissingTable(sqlState, nativeError);
    // ORA-00942 also hides a table the account may not read, and ALL_OBJECTS and ALL_INDEXES list only what it may use,
    // even with SELECT ANY TABLE. Only the owner sees the whole schema, and requiring it for an empty schema also keeps
    // unqualified indexes in the current schema, which must then be the login user's.
    public override string? TableVisibilitySql(string schema) =>
        $"SELECT 1 FROM DUAL WHERE {Owner(schema)} = SYS_CONTEXT('USERENV', 'SESSION_USER')";
    // Tables share their namespace with these object types; indexes have a namespace of their own.
    public override string NonTableObjectSql(string schema, string table) =>
        $"SELECT 1 FROM ALL_OBJECTS WHERE OWNER = {Owner(schema)} AND OBJECT_NAME = UPPER('{Literal(table)}') " +
        "AND OBJECT_TYPE IN ('VIEW', 'MATERIALIZED VIEW', 'SEQUENCE', 'SYNONYM', 'PROCEDURE', 'FUNCTION', 'PACKAGE', 'TYPE')";
    public override string? ConflictingIndexSql(string schema, string index, string table) =>
        $"SELECT 1 FROM ALL_INDEXES WHERE OWNER = {Owner(schema)} AND INDEX_NAME = UPPER('{Literal(index)}') " +
        $"AND NOT (TABLE_OWNER = {Owner(schema)} AND TABLE_NAME = UPPER('{Literal(table)}'))";
    private static string Owner(string schema) =>
        schema.Length == 0 ? "SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')" : $"UPPER('{Literal(schema)}')";
    public override string? AnalyzeSql(string schema, string table) => null;
}
