using OdbcBench.Config;
using OdbcBench.Initialize;

namespace OdbcBench.Tests;

public class InitializationTests
{
    [Fact]
    public void Catalog_is_the_cross_product_of_shapes_and_row_counts_plus_insert_target()
    {
        var config = new InitializeConfig
        {
            RowCounts = new() { 10, 100 },
            Shapes = new() { "narrow", "text" },
            InsertTable = "writes",
        };
        var tables = InitializationCatalog.Build(config);

        Assert.Equal(new[] { "read_narrow_10", "read_narrow_100", "read_text_10", "read_text_100", "writes" },
            tables.Select(t => t.Name));
        Assert.Equal(new long[] { 10, 100, 10, 100, 0 }, tables.Select(t => t.Rows));
        Assert.True(tables[^1].IsInsertTarget);
        Assert.Contains(tables[0].Columns, c => c.Name == "id" && c.Required && c.Type == SqlType.BigInt);
    }

    [Theory]
    [InlineData("PostgreSQL", "\"bench\".\"read_narrow_10\"", "BYTEA", "BOOLEAN")]
    [InlineData("Microsoft SQL Server", "[bench].[read_narrow_10]", "VARBINARY(64)", "BIT")]
    [InlineData("Oracle", "\"BENCH\".\"READ_NARROW_10\"", "RAW(64)", "NUMBER(1)")]
    public void Dialects_quote_names_and_map_nonportable_types(string dbms, string qualified, string binary, string boolean)
    {
        var d = SqlDialect.Detect(dbms);
        Assert.Equal(qualified, d.Qualify("bench", "read_narrow_10"));
        Assert.Equal(binary, d.Type(SqlType.Binary, 64));
        Assert.Equal(boolean, d.Type(SqlType.Boolean));
    }

    [Fact]
    public void Table_ddl_is_generated_from_logical_columns()
    {
        var d = SqlDialect.Detect("PostgreSQL");
        var table = new InitializationTable
        {
            Name = "read_narrow_10",
            Shape = "narrow",
            Rows = 10,
            Columns = InitializationCatalog.Columns("narrow"),
        };
        string sql = table.CreateSql(d, "bench");
        Assert.StartsWith("CREATE TABLE \"bench\".\"read_narrow_10\"", sql);
        Assert.Contains("\"id\" BIGINT NOT NULL", sql);
        Assert.Contains("\"created_at\" TIMESTAMP", sql);
    }

    [Fact]
    public void Initialization_defaults_and_validation()
    {
        var c = BenchConfig.Parse("""{ "dsns": [ { "name": "a", "dsn": "A" } ] }""");
        Assert.Empty(c.ValidateInitialization());
        var defaults = c.Initialize ?? new InitializeConfig();
        Assert.Equal(new long[] { 10_000, 1_000_000 }, defaults.RowCounts);
        Assert.Equal(new[] { "narrow", "wide", "text", "numeric" }, defaults.Shapes);

        c.Initialize = new InitializeConfig
        {
            Schema = "bad-name",
            Existing = "overwrite",
            RowCounts = new() { 0, 0 },
            Shapes = new() { "narrow", "mystery" },
            BatchSize = 0,
            ValueLength = 5000,
        };
        var errors = c.ValidateInitialization();
        Assert.Contains(errors, e => e.Contains("initialize.schema"));
        Assert.Contains(errors, e => e.Contains("initialize.existing"));
        Assert.Contains(errors, e => e.Contains("rowCounts values"));
        Assert.Contains(errors, e => e.Contains("rowCounts contains duplicates"));
        Assert.Contains(errors, e => e.Contains("unknown shape"));
        Assert.Contains(errors, e => e.Contains("batchSize"));
        Assert.Contains(errors, e => e.Contains("valueLength"));
    }

    [Fact]
    public void Init_cli_overrides_row_counts_batch_size_and_existing_mode()
    {
        var o = CliOptions.Parse(new[] { "init", "-c", "b.json", "--rows", "100,2000", "--batch-size", "250", "--recreate", "--dsn", "a" });
        var c = BenchConfig.Parse("""{ "dsns": [ { "name": "a", "dsn": "A" }, { "name": "b", "dsn": "B" } ] }""");
        o.ApplyTo(c);
        Assert.Equal(new long[] { 100, 2000 }, c.Initialize!.RowCounts);
        Assert.Equal(250, c.Initialize.BatchSize);
        Assert.Equal("recreate", c.Initialize.Existing);
        Assert.Equal(new[] { "a" }, c.EnabledDsns.Select(d => d.Name));
    }
}
