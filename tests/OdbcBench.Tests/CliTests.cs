using OdbcBench.Config;

namespace OdbcBench.Tests;

public class CliTests
{
    [Fact]
    public void Run_options_are_parsed()
    {
        var o = CliOptions.Parse(new[] { "run", "--config", "b.json", "-n", "5", "-w", "1", "-b", "1,100", "--dsn", "a,b", "-d", "c", "--strict", "--quiet", "-o", "out" });
        Assert.Equal("run", o.Command);
        Assert.Equal("b.json", o.ConfigPath);
        Assert.Equal(5, o.Iterations);
        Assert.Equal(1, o.Warmup);
        Assert.Equal(new[] { 1, 100 }, o.BlockSizes);
        Assert.Equal(new[] { "a", "b", "c" }, o.Dsns);
        Assert.True(o.Strict);
        Assert.True(o.Quiet);
        Assert.Equal("out", o.Output);
    }

    [Fact]
    public void Run_is_the_default_command()
    {
        Assert.Equal("run", CliOptions.Parse(new[] { "--config", "x.json" }).Command);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("frobnicate")]
    public void Unknown_arguments_are_rejected(string arg)
    {
        Assert.Throws<CliException>(() => CliOptions.Parse(new[] { arg }));
    }

    [Theory]
    [InlineData("-n", "0")]
    [InlineData("-n", "abc")]
    [InlineData("-b", "10,-1")]
    [InlineData("-w", "-1")]
    public void Bad_numbers_are_rejected(string option, string value)
    {
        Assert.Throws<CliException>(() => CliOptions.Parse(new[] { "run", option, value }));
    }

    [Fact]
    public void Missing_value_is_rejected()
    {
        Assert.Throws<CliException>(() => CliOptions.Parse(new[] { "run", "--config" }));
    }

    [Fact]
    public void Overrides_are_applied_to_the_configuration()
    {
        var config = BenchConfig.Parse("""
            { "query": "SELECT 1", "baseline": "a",
              "dsns": [ { "name": "a", "dsn": "A" }, { "name": "b", "dsn": "B" }, { "name": "c", "dsn": "C", "enabled": false } ] }
            """);
        config.ResolveQuery(null);
        CliOptions.Parse(new[] { "run", "-n", "7", "-w", "0", "-b", "10", "--dsn", "b,C", "--no-validate", "--strict" }).ApplyTo(config);

        Assert.Equal(7, config.Iterations);
        Assert.Equal(0, config.WarmupIterations);
        Assert.Equal(new[] { 10 }, config.BlockSizes);
        Assert.Equal(new[] { "b", "c" }, config.EnabledDsns.Select(d => d.Name));
        Assert.Null(config.Baseline); // the configured baseline was filtered out
        Assert.False(config.Validation.Enabled);
        Assert.True(config.Validation.Strict);
        Assert.Empty(config.Validate());
    }

    [Fact]
    public void Unknown_dsn_filter_is_rejected()
    {
        var config = BenchConfig.Parse("""{ "query": "SELECT 1", "dsns": [ { "name": "a", "dsn": "A" } ] }""");
        Assert.Throws<CliException>(() => CliOptions.Parse(new[] { "run", "--dsn", "zzz" }).ApplyTo(config));
    }
}
