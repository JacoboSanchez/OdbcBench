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
        // A probe that named a column would read "missing column" as "missing table" and skip the drop on --recreate.
        Assert.Equal($"SELECT 1 FROM {qualified} WHERE 1 = 0", d.TableProbeSql(d.Qualify("bench", "read_narrow_10")));
    }

    [Fact]
    public void Generated_identifiers_are_shortened_only_past_the_dialect_limit()
    {
        var oracle = SqlDialect.Detect("Oracle");
        Assert.Equal("ix_read_narrow_10000_category", oracle.FitIdentifier("ix_read_narrow_10000_category"));

        // 31 bytes: rejected by Oracle databases whose COMPATIBLE setting is below 12.2.
        string category = oracle.FitIdentifier("ix_read_narrow_1000000_category");
        string id = oracle.FitIdentifier("ix_read_narrow_1000000_id_with_a_long_suffix");
        Assert.Equal(30, category.Length);
        Assert.Matches("^ix_read_narrow_100000_[0-9a-f]{8}$", category);
        Assert.Equal(category, oracle.FitIdentifier("ix_read_narrow_1000000_category"));
        Assert.NotEqual(category, id);
        Assert.True(id.Length <= 30);

        var postgres = SqlDialect.Detect("PostgreSQL");
        Assert.Equal("ix_read_narrow_1000000_category", postgres.FitIdentifier("ix_read_narrow_1000000_category"));
        Assert.Equal(63, postgres.FitIdentifier(new string('x', 64)).Length);

        // PostgreSQL and Oracle limits are bytes; SQL Server's are characters.
        string cjk = new string('表', 50); // 150 UTF-8 bytes
        Assert.False(postgres.FitsIdentifier(cjk));
        Assert.True(SqlDialect.Detect("Microsoft SQL Server").FitsIdentifier(cjk));
        Assert.False(SqlDialect.Detect("Microsoft SQL Server").FitsIdentifier(new string('x', 129)));
    }

    [Theory]
    [InlineData("PostgreSQL", "CREATE UNIQUE INDEX \"ix_t_id\" ON \"bench\".\"t\" (\"id\")")]
    [InlineData("Microsoft SQL Server", "CREATE UNIQUE INDEX [ix_t_id] ON [bench].[t] ([id])")]
    [InlineData("Oracle", "CREATE UNIQUE INDEX \"BENCH\".\"IX_T_ID\" ON \"BENCH\".\"T\" (\"ID\")")]
    public void Indexes_are_created_in_the_table_schema(string dbms, string expected)
    {
        var d = SqlDialect.Detect(dbms);
        Assert.Equal(expected, d.CreateIndexSql("bench", "ix_t_id", d.Qualify("bench", "t"), "id", unique: true));
    }

    [Fact]
    public void Schema_and_table_names_must_fit_the_dialect_limit_before_anything_is_created()
    {
        var tables = InitializationCatalog.Build(new InitializeConfig
        {
            RowCounts = new() { 10 },
            Shapes = new() { "narrow" },
            InsertTable = "insert_target_with_a_31_byte_nm",
        });
        var error = Assert.Throws<InvalidOperationException>(() =>
            DatabaseInitializer.RequireFittingNames("bench", tables, SqlDialect.Detect("Oracle")));
        Assert.Contains("30-byte", error.Message);
        Assert.EndsWith(": insert_target_with_a_31_byte_nm", error.Message);
        DatabaseInitializer.RequireFittingNames("bench", tables, SqlDialect.Detect("PostgreSQL"));
        DatabaseInitializer.RequireFittingNames("", tables, SqlDialect.Detect("PostgreSQL")); // the connection's default schema

        // PostgreSQL would create a 64-byte schema truncated to 63 bytes, which the next run could not find.
        string schema = new string('s', 64);
        error = Assert.Throws<InvalidOperationException>(() =>
            DatabaseInitializer.RequireFittingNames(schema, tables, SqlDialect.Detect("PostgreSQL")));
        Assert.EndsWith($": {schema}", error.Message);
    }

    [Theory]
    [InlineData("PostgreSQL", "42P01", 7, true)]
    [InlineData("PostgreSQL", "42S02", 0, true)]
    [InlineData("PostgreSQL", "42501", 7, false)] // insufficient_privilege on a table that exists
    [InlineData("Microsoft SQL Server", "42S02", 208, true)]
    [InlineData("Microsoft SQL Server", "42000", 208, true)]
    [InlineData("Microsoft SQL Server", "42000", 229, false)] // SELECT permission denied
    [InlineData("Oracle", "42S02", 942, true)]
    [InlineData("Oracle", "42000", 942, true)]
    [InlineData("Oracle", "42000", 1031, false)] // ORA-01031 insufficient privileges
    public void Only_missing_table_errors_mean_a_table_is_absent(string dbms, string state, int native, bool missing)
    {
        Assert.Equal(missing, SqlDialect.Detect(dbms).IsMissingTable(state, native));
    }

    [Fact]
    public void Name_conflicts_are_looked_up_in_each_dbms_catalog()
    {
        var postgres = SqlDialect.Detect("PostgreSQL");
        Assert.EndsWith("n.nspname = 'bench' AND c.relname = 'read_narrow_10' AND c.relkind NOT IN ('r', 'p')",
            postgres.NonTableObjectSql("bench", "read_narrow_10"));
        Assert.Contains("n.nspname = current_schema()", postgres.NonTableObjectSql("", "read_narrow_10"));
        Assert.Contains("t.relname = 'read_narrow_10'", postgres.ConflictingIndexSql("bench", "ix_read_narrow_10_id", "read_narrow_10"));

        var sqlServer = SqlDialect.Detect("Microsoft SQL Server");
        Assert.Contains("schema_id = SCHEMA_ID('bench') AND name = 'read_narrow_10' AND type <> 'U'", sqlServer.NonTableObjectSql("bench", "read_narrow_10"));
        Assert.Contains("SCHEMA_ID()", sqlServer.NonTableObjectSql("", "read_narrow_10"));
        Assert.Null(sqlServer.ConflictingIndexSql("bench", "ix_read_narrow_10_id", "read_narrow_10"));

        var oracle = SqlDialect.Detect("Oracle");
        Assert.Contains("FROM ALL_OBJECTS WHERE OWNER = UPPER('bench') AND OBJECT_NAME = UPPER('read_narrow_10')", oracle.NonTableObjectSql("bench", "read_narrow_10"));
        Assert.Contains("NOT (TABLE_OWNER = UPPER('bench') AND TABLE_NAME = UPPER('read_narrow_10'))",
            oracle.ConflictingIndexSql("bench", "ix_read_narrow_10_id", "read_narrow_10"));
    }

    [Theory]
    [InlineData("PostgreSQL", "SELECT current_schema()")]
    [InlineData("Microsoft SQL Server", "SELECT SCHEMA_NAME()")]
    [InlineData("Oracle", "SELECT SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') FROM DUAL")]
    public void An_empty_schema_is_resolved_to_the_current_one(string dbms, string sql)
    {
        Assert.Equal(sql, SqlDialect.Detect(dbms).CurrentSchemaSql);
    }

    [Fact]
    public void Oracle_init_requires_the_login_user_to_own_the_schema()
    {
        // Neither ORA-00942 nor ALL_OBJECTS can be trusted for another user's schema, even with SELECT ANY TABLE.
        Assert.Equal("SELECT 1 FROM DUAL WHERE UPPER('bench') = SYS_CONTEXT('USERENV', 'SESSION_USER')",
            SqlDialect.Detect("Oracle").TableVisibilitySql("bench"));
        Assert.Equal("SELECT 1 FROM DUAL WHERE SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') = SYS_CONTEXT('USERENV', 'SESSION_USER')",
            SqlDialect.Detect("Oracle").TableVisibilitySql(""));
        // PostgreSQL and SQL Server report a table the account cannot read as a permission error, not as missing.
        Assert.Null(SqlDialect.Detect("PostgreSQL").TableVisibilitySql("bench"));
        Assert.Null(SqlDialect.Detect("Microsoft SQL Server").TableVisibilitySql("bench"));
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

        // A 40-character é schema is 40 bytes in LATIN1 but 80 in UTF-8; ASCII names are the same length everywhere.
        c.Initialize = new InitializeConfig { Schema = new string('é', 40) };
        Assert.Contains(c.ValidateInitialization(), e => e.Contains("initialize.schema") && e.Contains("ASCII"));
    }

    [Fact]
    public void Explicit_nulls_in_initialize_are_configuration_errors()
    {
        var c = BenchConfig.Parse("""
            { "dsns": [ { "name": "a", "dsn": "A" } ],
              "initialize": { "schema": null, "existing": null, "insertTable": null, "rowCounts": null, "shapes": [ "narrow", null ] } }
            """);
        var errors = c.ValidateInitialization();
        foreach (string field in new[] { "schema", "existing", "insertTable", "rowCounts", "shapes" })
            Assert.Contains($"initialize.{field} cannot be null", errors);

        c.Initialize = new InitializeConfig { CreateInsertTable = false, InsertTable = null! }; // unused, so allowed
        Assert.Empty(c.ValidateInitialization());
    }

    [Fact]
    public void Insert_table_cannot_reuse_a_generated_read_table_name()
    {
        var c = BenchConfig.Parse("""{ "dsns": [ { "name": "a", "dsn": "A" } ] }""");
        c.Initialize = new InitializeConfig { RowCounts = new() { 10_000 }, Shapes = new() { "wide" }, InsertTable = "READ_WIDE_10000" };
        Assert.Contains(c.ValidateInitialization(), e => e.Contains("initialize.insertTable 'READ_WIDE_10000'"));

        c.Initialize.CreateInsertTable = false;
        Assert.Empty(c.ValidateInitialization());
        c.Initialize.InsertTable = ""; // unused when no insert target is created
        Assert.Empty(c.ValidateInitialization());
        c.Initialize.CreateInsertTable = true;
        Assert.Contains(c.ValidateInitialization(), e => e.Contains("initialize.insertTable must be a simple SQL identifier"));
        c.Initialize.InsertTable = "read_wide_100";
        Assert.Empty(c.ValidateInitialization());
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
