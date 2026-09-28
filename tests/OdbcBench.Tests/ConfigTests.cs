using OdbcBench.Config;

namespace OdbcBench.Tests;

public class ConfigTests
{
    private const string Minimal = """
        {
          // comments and trailing commas are accepted
          "query": "SELECT 1 ORDER BY 1",
          "dsns": [
            { "name": "a", "dsn": "DsnA", "uid": "u", "pwd": "p" },
            { "name": "b", "dsn": "DsnB" },
          ],
        }
        """;

    [Fact]
    public void Defaults_are_applied()
    {
        var c = BenchConfig.Parse(Minimal);
        c.ResolveQuery(null);
        Assert.Empty(c.Validate());
        Assert.Equal(3, c.WarmupIterations);
        Assert.Equal(20, c.Iterations);
        Assert.Equal(new[] { 1000 }, c.BlockSizes);
        Assert.Equal(10, c.Validation.Rows);
        Assert.True(c.Interleave);
        Assert.True(c.ReuseStatement);
        Assert.Equal("a", c.BaselineDsn().Name);
    }

    [Fact]
    public void Invalid_settings_are_all_reported()
    {
        var c = BenchConfig.Parse("""
            { "dsns": [ { "name": "a" }, { "name": "a", "dsn": "x" } ],
              "iterations": 0, "blockSizes": [ 0, 5, 5 ], "bindMode": "fast", "baseline": "zzz" }
            """);
        c.ResolveQuery(null);
        var errors = c.Validate();
        Assert.Contains(errors, e => e.Contains("either dsn or connectionString"));
        Assert.Contains(errors, e => e.Contains("used more than once"));
        Assert.Contains(errors, e => e.Contains("no query"));
        Assert.Contains(errors, e => e.Contains("iterations must be at least 1"));
        Assert.Contains(errors, e => e.Contains("0 is not a valid row array size"));
        Assert.Contains(errors, e => e.Contains("duplicates"));
        Assert.Contains(errors, e => e.Contains("bindMode"));
        Assert.Contains(errors, e => e.Contains("baseline 'zzz'"));
    }

    [Fact]
    public void Per_dsn_query_overrides_the_global_one()
    {
        var c = BenchConfig.Parse("""{ "query": "SELECT 1", "dsns": [ { "name": "a", "dsn": "A", "query": " SELECT 2 " } ] }""");
        c.ResolveQuery(null);
        Assert.Equal("SELECT 2", c.QueryFor(c.Dsns[0]));
    }

    [Fact]
    public void Query_file_is_read_relative_to_the_config()
    {
        string dir = Path.Combine(Path.GetTempPath(), "odbcbench-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "q.sql"), "SELECT 42\n");
            File.WriteAllText(Path.Combine(dir, "bench.json"), """{ "queryFile": "q.sql", "dsns": [ { "name": "a", "dsn": "A" } ] }""");
            var c = BenchConfig.Load(Path.Combine(dir, "bench.json"));
            Assert.Equal("SELECT 42", c.ResolvedQuery);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Connection_string_quotes_special_characters()
    {
        var d = new DsnConfig { Name = "a", Dsn = "My DSN", Uid = "user", Pwd = "p;w}d", ExtraAttributes = "Encrypt=yes;" };
        Assert.Equal("DSN=My DSN;UID=user;PWD={p;w}}d};Encrypt=yes;", d.BuildConnectionString());
    }

    [Fact]
    public void Full_connection_string_gets_credentials_appended()
    {
        var d = new DsnConfig { Name = "a", ConnectionString = "Driver={X};Host=h", Uid = "u", Pwd = "p" };
        Assert.Equal("Driver={X};Host=h;UID=u;PWD=p;", d.BuildConnectionString());
    }

    [Fact]
    public void Empty_password_is_sent_explicitly_but_missing_one_is_not()
    {
        Assert.Contains("PWD=;", new DsnConfig { Dsn = "A", Uid = "u", Pwd = "" }.BuildConnectionString());
        Assert.DoesNotContain("PWD", new DsnConfig { Dsn = "A", Uid = "u" }.BuildConnectionString());
    }

    [Theory]
    [InlineData("Driver={X};UID=u;PWD=secret;Host=h", "Driver={X};UID=u;PWD=***;Host=h")]
    [InlineData("pwd={a;b}}c};x=1", "pwd=***;x=1")]
    [InlineData("Host=h;token=abc123", "Host=h;token=***")]
    [InlineData("Host=h;PersonalAccessToken=abc;ClientSecret=s", "Host=h;PersonalAccessToken=***;ClientSecret=***")]
    [InlineData("Password=x", "Password=***")]
    [InlineData("DSN=A;UID=u", "DSN=A;UID=u")]
    public void Secrets_are_redacted(string input, string expected)
    {
        Assert.Equal(expected, BenchConfig.Redact(input));
    }

    [Fact]
    public void Redacted_copy_hides_passwords_and_leaves_the_original_intact()
    {
        var c = BenchConfig.Parse(Minimal);
        c.Dsns[1].ConnectionString = "Driver={X};PWD=hidden";
        var copy = c.Redacted();
        Assert.Equal("***", copy.Dsns[0].Pwd);
        Assert.Equal("Driver={X};PWD=***", copy.Dsns[1].ConnectionString);
        Assert.Equal("p", c.Dsns[0].Pwd);
        string json = System.Text.Json.JsonSerializer.Serialize(copy, BenchConfig.JsonOptions);
        Assert.DoesNotContain("hidden", json);
        Assert.DoesNotContain("\"p\"", json);
    }

    [Fact]
    public void Password_resolution_order()
    {
        const string variable = "ODBCBENCH_TEST_PWD";
        Environment.SetEnvironmentVariable(variable, "from-env");
        try
        {
            var c = BenchConfig.Parse("""
                { "query": "SELECT 1", "dsns": [
                  { "name": "inline", "dsn": "A", "uid": "u", "pwd": "inline" },
                  { "name": "env", "dsn": "B", "uid": "u", "pwdEnv": "ODBCBENCH_TEST_PWD" },
                  { "name": "prompt", "dsn": "C", "uid": "u" },
                  { "name": "integrated", "dsn": "D" },
                  { "name": "in-string", "connectionString": "Driver={X};PWD=x", "uid": "u" } ] }
                """);
            var prompted = new List<string>();
            c.ResolvePasswords(d => { prompted.Add(d.Name); return "typed"; });

            Assert.Equal("inline", c.Dsns[0].ResolvedPassword);
            Assert.Equal("from-env", c.Dsns[1].ResolvedPassword);
            Assert.Equal("typed", c.Dsns[2].ResolvedPassword);
            Assert.Null(c.Dsns[3].ResolvedPassword);
            Assert.Equal(new[] { "prompt" }, prompted);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void Missing_environment_variable_is_an_error()
    {
        var c = BenchConfig.Parse("""{ "query": "SELECT 1", "dsns": [ { "name": "a", "dsn": "A", "uid": "u", "pwdEnv": "ODBCBENCH_SURELY_UNSET_1234" } ] }""");
        Assert.Throws<ConfigException>(() => c.ResolvePasswords(_ => null));
    }

    [Fact]
    public void Broken_json_is_a_config_error()
    {
        Assert.Throws<ConfigException>(() => BenchConfig.Parse("{ \"dsns\": [ "));
    }

    [Fact]
    public void Insert_workload_needs_a_table_not_a_query()
    {
        var c = BenchConfig.Parse("""
            { "dsns": [ { "name": "a", "dsn": "A" }, { "name": "b", "dsn": "B", "insertTable": "\"b\".\"t\"" } ],
              "insert": { "table": "dbo.t" } }
            """);
        c.ResolveQuery(null);
        Assert.Empty(c.Validate(Workload.Insert));
        Assert.Contains(c.Validate(Workload.Select), e => e.Contains("no query"));
        Assert.Equal("dbo.t", c.TableFor(c.Dsns[0]));
        Assert.Equal("\"b\".\"t\"", c.TableFor(c.Dsns[1]));
    }

    [Fact]
    public void Invalid_insert_settings_are_all_reported()
    {
        var c = BenchConfig.Parse("""
            { "dsns": [ { "name": "a", "dsn": "A" } ],
              "insert": { "rows": 0, "cleanup": "drop", "transaction": "never", "valueLength": 0, "columns": [ "id", " " ] } }
            """);
        var errors = c.Validate(Workload.Insert);
        Assert.Contains(errors, e => e.Contains("no target table"));
        Assert.Contains(errors, e => e.Contains("insert.rows"));
        Assert.Contains(errors, e => e.Contains("insert.cleanup"));
        Assert.Contains(errors, e => e.Contains("insert.transaction"));
        Assert.Contains(errors, e => e.Contains("insert.valueLength"));
        Assert.Contains(errors, e => e.Contains("empty name"));
    }

    [Fact]
    public void Insert_options_follow_the_configuration()
    {
        var c = BenchConfig.Parse("""
            { "dsns": [ { "name": "a", "dsn": "A" } ], "bindMode": "wchar", "reuseStatement": false,
              "insert": { "table": "t", "rows": 5000, "transaction": "perBatch", "cleanup": "truncate", "verify": false } }
            """);
        var o = c.InsertOptions(250);
        Assert.Equal(250, o.BatchSize);
        Assert.Equal(5000, o.Rows);
        Assert.Equal(OdbcBench.Insert.TransactionMode.PerBatch, o.Transaction);
        Assert.Equal(OdbcBench.Odbc.BindMode.WChar, o.BindMode);
        Assert.False(o.ReuseStatement);
        Assert.False(o.Verify);
        Assert.Equal("TRUNCATE TABLE t", c.InsertTargetFor(c.Dsns[0]).CleanupSql);
    }

    [Fact]
    public void Insert_defaults()
    {
        var c = BenchConfig.Parse("""{ "dsns": [ { "name": "a", "dsn": "A" } ], "insert": { "table": "t" } }""");
        var o = c.InsertOptions(1000);
        Assert.Equal(100_000, o.Rows);
        Assert.Equal(OdbcBench.Insert.TransactionMode.PerIteration, o.Transaction);
        Assert.True(o.Verify);
        Assert.Null(c.InsertTargetFor(c.Dsns[0]).CleanupSql);
    }

    [Fact]
    public void Fetch_options_follow_the_configuration()
    {
        var c = BenchConfig.Parse("""
            { "query": "SELECT 1", "dsns": [ { "name": "a", "dsn": "A" } ],
              "bindMode": "wchar", "longColumnMode": "bindCapped", "reuseStatement": false, "queryTimeoutSeconds": 30 }
            """);
        var o = c.FetchOptions(250);
        Assert.Equal(250, o.BlockSize);
        Assert.Equal(OdbcBench.Odbc.BindMode.WChar, o.BindMode);
        Assert.Equal(OdbcBench.Fetch.LongColumnMode.BindCapped, o.LongColumnMode);
        Assert.False(o.ReuseStatement);
        Assert.Equal(30, o.QueryTimeoutSeconds);
    }
}
