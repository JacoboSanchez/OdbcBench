using System.Diagnostics;
using System.Globalization;
using OdbcBench.Config;
using OdbcBench.Insert;
using OdbcBench.Odbc;

namespace OdbcBench.Initialize;

/// <summary>Creates and fills one benchmark database through the selected ODBC driver.</summary>
public sealed class DatabaseInitializer
{
    private readonly BenchConfig _config;
    private readonly InitializeConfig _options;
    private readonly OdbcConnection _connection;
    private readonly SqlDialect _dialect;
    private readonly TextWriter _output;
    private readonly bool _quiet;
    private readonly string _schema;

    public DatabaseInitializer(BenchConfig config, OdbcConnection connection, TextWriter output, bool quiet)
    {
        _config = config;
        _options = config.Initialize ?? new InitializeConfig();
        _connection = connection;
        _dialect = SqlDialect.Detect(connection.Driver?.DbmsName ?? "");
        _output = output;
        _quiet = quiet;
        _schema = _options.Schema.Trim();
    }

    public IReadOnlyList<InitializationTable> Run()
    {
        var tables = InitializationCatalog.Build(_options);
        RequireFittingNames(_schema, tables, _dialect);
        EnsureSchema();
        RequireTableVisibility();
        RequireNoConflictingObjects(tables);

        var existing = tables.Where(TableExists).ToList();
        if (existing.Count > 0 && !_options.Existing.Equals("recreate", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("generated table(s) already exist: " +
                string.Join(", ", existing.Select(t => t.QualifiedName(_dialect, _schema))) +
                "; set initialize.existing to 'recreate' or pass --recreate to replace them");

        foreach (var table in existing.AsEnumerable().Reverse())
        {
            Status($"Dropping     {table.QualifiedName(_dialect, _schema)}");
            Execute(_dialect.DropTableSql(table.QualifiedName(_dialect, _schema)));
        }

        foreach (var table in tables)
        {
            string qualified = table.QualifiedName(_dialect, _schema);
            Status($"Creating     {qualified} ({table.Shape}{(table.IsInsertTarget ? ", empty insert target" : $", {table.Rows.ToString("N0", CultureInfo.InvariantCulture)} rows")})");
            Execute(table.CreateSql(_dialect, _schema));
            if (!table.IsInsertTarget) Populate(table);
            foreach (var (index, column, unique) in Indexes(table))
                Execute(_dialect.CreateIndexSql(_schema, index, qualified, column, unique));
            if (_options.Analyze && !table.IsInsertTarget && _dialect.AnalyzeSql(_schema, table.Name) is string analyze)
                Execute(analyze);
        }
        return tables;
    }

    public string DialectName => _dialect.Name;

    // Checked before anything is created: the insert target comes last, after every read table has been loaded, and
    // PostgreSQL silently truncates an overlong schema, which the next run's existence check would then miss.
    internal static void RequireFittingNames(string schema, IEnumerable<InitializationTable> tables, SqlDialect dialect)
    {
        var overlong = tables.Select(t => t.Name).Prepend(schema).Where(name => !dialect.FitsIdentifier(name)).ToList();
        if (overlong.Count > 0)
            throw new InvalidOperationException($"name(s) longer than the {dialect.MaxIdentifierLength}-{dialect.IdentifierLengthUnit} identifier limit init uses for {dialect.Name}: " +
                string.Join(", ", overlong));
    }

    private void EnsureSchema()
    {
        if (_schema.Length == 0) return;
        bool exists = _dialect.SchemaExistsSql(_schema) is string sql && QueryHasRows(sql);
        if (exists) return;
        if (_dialect.CreateSchemaSql(_schema) is not string create)
            throw new InvalidOperationException($"schema '{_schema}' does not exist; Oracle schemas are database users, so create the user first or set initialize.schema to an existing user (or to an empty string for the current schema)");
        Status($"Creating schema {_dialect.Quote(_schema)}");
        Execute(create);
    }

    private void RequireTableVisibility()
    {
        if (_dialect.TableVisibilitySql(_schema) is string sql && !QueryHasRows(sql))
            throw new InvalidOperationException($"{_dialect.Name} hides objects this account cannot read, so init only manages a schema owned by the login user; connect as the owner of {(_schema.Length == 0 ? "the current schema" : $"schema {_dialect.Quote(_schema)}")}");
    }

    // Runs before any table is dropped, so --recreate cannot rebuild part of the dataset and then stop on a name it
    // was never going to be able to use.
    private void RequireNoConflictingObjects(IReadOnlyList<InitializationTable> tables)
    {
        var conflicts = new List<string>();
        foreach (var table in tables)
        {
            if (QueryHasRows(_dialect.NonTableObjectSql(_schema, table.Name)))
                conflicts.Add($"{table.QualifiedName(_dialect, _schema)} exists but is not a table");
            foreach (var (index, _, _) in Indexes(table))
                if (_dialect.ConflictingIndexSql(_schema, index, table.Name) is string sql && QueryHasRows(sql))
                    conflicts.Add($"index name {_dialect.Qualify(_schema, index)} is already used by another object");
        }
        if (conflicts.Count > 0)
            throw new InvalidOperationException("init will not replace objects it did not create: " + string.Join("; ", conflicts));
    }

    private void Populate(InitializationTable table)
    {
        string qualified = table.QualifiedName(_dialect, _schema);
        var target = new InsertTarget(qualified, null, BindMode.Native, _options.ValueLength, CleanupMode.None);
        var options = new InsertOptions
        {
            BatchSize = _options.BatchSize,
            Rows = table.Rows,
            BindMode = BindMode.Native,
            ReuseStatement = true,
            Transaction = TransactionMode.PerIteration,
            Verify = true,
            MaxBoundBytes = _config.MaxBoundBytes,
            QueryTimeoutSeconds = _config.QueryTimeoutSeconds,
        };
        using var writer = new BatchInsertWriter(_connection, target, options);
        var preparationFailure = writer.Prepare(1, warmup: false);
        if (preparationFailure != null)
            throw new InvalidOperationException($"could not prepare {qualified}: {preparationFailure.ErrorMessage}");
        long started = Stopwatch.GetTimestamp();
        var sample = writer.Execute(1, warmup: false);
        writer.Verify(sample);
        if (!sample.Ok)
            throw new InvalidOperationException($"could not populate {qualified}: [{sample.ErrorSqlState}] {sample.ErrorMessage}");
        if (sample.VerifiedRows != table.Rows)
            throw new InvalidOperationException($"populating {qualified} sent {table.Rows:N0} rows but the table gained {sample.VerifiedRows:N0}");
        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        Status(string.Create(CultureInfo.InvariantCulture,
            $"Populated    {qualified}: {table.Rows:N0} rows in {seconds:0.0}s ({table.Rows / Math.Max(seconds, 0.001):N0} rows/s, parameter array {sample.EffectiveBlockSize:N0})"));
    }

    private bool TableExists(InitializationTable table)
    {
        using var statement = new OdbcStatement(_connection);
        try
        {
            statement.ExecDirect(_dialect.TableProbeSql(table.QualifiedName(_dialect, _schema)));
            return true;
        }
        catch (OdbcException ex) when (_dialect.IsMissingTable(ex.SqlState, ex.NativeError))
        {
            return false;
        }
        finally
        {
            statement.TryCloseCursor();
            _connection.TryRollback(); // end the metadata transaction when a previous bulk load disabled autocommit
        }
    }

    private bool QueryHasRows(string sql)
    {
        using var statement = new OdbcStatement(_connection);
        try
        {
            statement.ExecDirect(sql);
            short rc = statement.Fetch();
            if (rc == Native.SQL_ERROR || rc == Native.SQL_INVALID_HANDLE)
                throw new OdbcException("SQLFetch", rc, statement.DrainDiagnostics());
            return rc != Native.SQL_NO_DATA;
        }
        finally
        {
            statement.TryCloseCursor();
            _connection.TryRollback();
        }
    }

    private void Execute(string sql)
    {
        using var statement = new OdbcStatement(_connection);
        try
        {
            statement.ExecDirect(sql);
            if (!_connection.AutoCommit) _connection.Commit();
        }
        catch
        {
            _connection.TryRollback();
            throw;
        }
        finally { statement.TryCloseCursor(); }
    }

    private IEnumerable<(string Index, string Column, bool Unique)> Indexes(InitializationTable table)
    {
        if (!_options.CreateIndexes || table.IsInsertTarget) yield break;
        yield return (IndexName(table, "id"), "id", true);
        if (table.Columns.Any(c => c.Name == "category"))
            yield return (IndexName(table, "category"), "category", false);
    }

    private string IndexName(InitializationTable table, string column) => _dialect.FitIdentifier($"ix_{table.Name}_{column}");
    private void Status(string text) { if (!_quiet) _output.WriteLine(text); }
}
