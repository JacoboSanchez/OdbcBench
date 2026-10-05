using OdbcBench.Config;

namespace OdbcBench.Initialize;

public sealed record InitializationColumn(string Name, SqlType Type, int Size = 0, bool Required = false);

public sealed class InitializationTable
{
    public required string Name { get; init; }
    public required string Shape { get; init; }
    public required long Rows { get; init; }
    public bool IsInsertTarget { get; init; }
    public required IReadOnlyList<InitializationColumn> Columns { get; init; }

    public string QualifiedName(SqlDialect dialect, string schema) => dialect.Qualify(schema, Name);

    public string CreateSql(SqlDialect dialect, string schema)
    {
        string columns = string.Join(", ", Columns.Select(c =>
            $"{dialect.Quote(c.Name)} {dialect.Type(c.Type, c.Size)}{(c.Required ? " NOT NULL" : "")}"));
        return $"CREATE TABLE {QualifiedName(dialect, schema)} ({columns})";
    }
}

/// <summary>The deterministic, built-in size × shape benchmark dataset.</summary>
public static class InitializationCatalog
{
    public static IReadOnlyList<InitializationTable> Build(InitializeConfig config)
    {
        var result = new List<InitializationTable>();
        foreach (string configuredShape in config.Shapes)
        {
            string shape = configuredShape.Trim().ToLowerInvariant();
            var columns = Columns(shape);
            foreach (long rows in config.RowCounts)
                result.Add(new InitializationTable
                {
                    Name = ReadTableName(shape, rows),
                    Shape = shape,
                    Rows = rows,
                    Columns = columns,
                });
        }
        if (config.CreateInsertTable)
            result.Add(new InitializationTable
            {
                Name = config.InsertTable.Trim(),
                Shape = "wide",
                Rows = 0,
                IsInsertTarget = true,
                Columns = Columns("wide"),
            });
        return result;
    }

    public static string ReadTableName(string shape, long rows) => $"read_{shape.Trim().ToLowerInvariant()}_{rows}";

    public static IReadOnlyList<InitializationColumn> Columns(string shape) => shape switch
    {
        "narrow" => new[]
        {
            new InitializationColumn("id", SqlType.BigInt, Required: true),
            // SMALLINT wraps every 32,768 rows in DataGenerator, making this a useful non-unique filter/index.
            new InitializationColumn("category", SqlType.SmallInt),
            new InitializationColumn("amount", SqlType.Double),
            new InitializationColumn("created_at", SqlType.Timestamp),
        },
        "numeric" => new[]
        {
            new InitializationColumn("id", SqlType.BigInt, Required: true),
            new InitializationColumn("small_value", SqlType.SmallInt),
            new InitializationColumn("int_value", SqlType.Integer),
            new InitializationColumn("big_value", SqlType.BigInt),
            new InitializationColumn("decimal_value", SqlType.Decimal),
            new InitializationColumn("real_value", SqlType.Real),
            new InitializationColumn("double_value", SqlType.Double),
            new InitializationColumn("flag", SqlType.Boolean),
        },
        "text" => new[]
        {
            new InitializationColumn("id", SqlType.BigInt, Required: true),
            new InitializationColumn("code", SqlType.VarChar, 32),
            new InitializationColumn("short_text", SqlType.VarChar, 128),
            new InitializationColumn("long_text", SqlType.VarChar, 512),
        },
        "wide" => new[]
        {
            new InitializationColumn("id", SqlType.BigInt, Required: true),
            new InitializationColumn("category", SqlType.SmallInt),
            new InitializationColumn("small_value", SqlType.SmallInt),
            new InitializationColumn("amount", SqlType.Decimal),
            new InitializationColumn("ratio", SqlType.Double),
            new InitializationColumn("flag", SqlType.Boolean),
            new InitializationColumn("business_date", SqlType.Date),
            new InitializationColumn("created_at", SqlType.Timestamp),
            new InitializationColumn("code", SqlType.VarChar, 32),
            new InitializationColumn("name", SqlType.VarChar, 128),
            new InitializationColumn("description", SqlType.VarChar, 512),
            new InitializationColumn("payload", SqlType.Binary, 64),
        },
        _ => throw new ArgumentException($"unknown initialization shape '{shape}'", nameof(shape)),
    };
}
