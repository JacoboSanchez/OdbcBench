using OdbcBench.Bench;
using OdbcBench.Odbc;
using OdbcBench.Report;

namespace OdbcBench.Tests;

public class PlatformTests
{
    [Fact]
    public void Driver_manager_candidates_match_the_current_os()
    {
        var candidates = DriverManager.Candidates();
        if (OperatingSystem.IsWindows()) Assert.Equal(new[] { "odbc32.dll" }, candidates);
        else Assert.All(candidates, c => Assert.Contains("libodbc", c));
    }

    [Fact]
    public void Process_cpu_time_is_read_and_advances()
    {
        double before = ProcessCpu.NowMs();
        double spin = 0;
        for (int i = 0; i < 20_000_000; i++) spin += Math.Sqrt(i);
        double after = ProcessCpu.NowMs();
        Assert.True(spin > 0);
        Assert.True(before > 0);
        Assert.True(after >= before);
    }

    [Fact]
    public void Physical_memory_is_reported()
    {
        Assert.True(PhysicalMemory.TotalBytes() > 0);
    }

    [Fact]
    public void Ini_sections_are_read_case_insensitively()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                ; comment
                [ODBC Data Sources]
                Sales = SQLite3

                [Sales]
                Driver   = SQLite3
                Database = /data/sales.db
                # comment
                [Other]
                Driver=/opt/driver/libother.so
                """);
            var sales = UnixOdbc.Section(path, "sales");
            Assert.NotNull(sales);
            Assert.Equal("SQLite3", sales!["driver"]);
            Assert.Equal("/data/sales.db", sales["Database"]);
            Assert.Equal(2, sales.Count);
            Assert.Equal("/opt/driver/libother.so", UnixOdbc.Section(path, "Other")!["Driver"]);
            Assert.Null(UnixOdbc.Section(path, "Missing"));
            Assert.Null(UnixOdbc.Section(path + ".absent", "Sales"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Driver_library_paths_are_checked_only_when_absolute()
    {
        Assert.True(UnixOdbc.LibraryExists("libsqlite3odbc.so"));
        Assert.False(UnixOdbc.LibraryExists("/nonexistent/libdriver.so"));
        Assert.Equal("/opt/x/libdriver.so", UnixOdbc.DriverLibrary("/opt/x/libdriver.so"));
    }

    [Fact]
    public void Cpu_tick_note_appears_only_for_windows_runs()
    {
        var windows = SampleRun.Create();
        Assert.Contains("ticks of about 15.6 ms", MarkdownReportWriter.Render(windows));

        var linux = SampleRun.Create();
        linux.Environment.Os = "Ubuntu 24.04.1 LTS (X64)";
        Assert.DoesNotContain("ticks of about 15.6 ms", MarkdownReportWriter.Render(linux));
    }
}
