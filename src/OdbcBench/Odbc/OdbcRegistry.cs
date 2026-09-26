using Microsoft.Win32;

namespace OdbcBench.Odbc;

/// <summary>
/// Reads the ODBC registry in both views so bitness problems (IM002 "data source not found", IM014 "architecture
/// mismatch") can be explained: a 64-bit process only sees 64-bit system DSNs and 64-bit drivers.
/// </summary>
public static class OdbcRegistry
{
    public sealed record DsnLocation(string Scope, string Driver)
    {
        public override string ToString() => $"{Scope} -> \"{Driver}\"";
    }

    public static List<DsnLocation> FindDsn(string dsn)
    {
        var found = new List<DsnLocation>();
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(dsn)) return found;
        Probe(found, RegistryHive.LocalMachine, RegistryView.Registry64, "64-bit system DSN", dsn);
        Probe(found, RegistryHive.LocalMachine, RegistryView.Registry32, "32-bit system DSN", dsn);
        Probe(found, RegistryHive.CurrentUser, RegistryView.Registry64, "user DSN", dsn);
        return found;
    }

    public static bool DriverInstalled(string driver, bool is64Bit)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, is64Bit ? RegistryView.Registry64 : RegistryView.Registry32);
            using var key = root.OpenSubKey(@"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers");
            return key?.GetValue(driver) is string;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Explains a connect failure in terms of DSN and driver bitness, or returns null when there is nothing to add.</summary>
    public static string? Hint(string? dsn, string? sqlState)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(dsn)) return null;
        bool is64 = Environment.Is64BitProcess;
        string bits = is64 ? "64-bit" : "32-bit";
        string otherBits = is64 ? "32-bit" : "64-bit";
        var locations = FindDsn(dsn);
        var visible = locations.Where(l => l.Scope.StartsWith(bits, StringComparison.Ordinal) || l.Scope == "user DSN").ToList();

        if (sqlState == "IM002")
        {
            if (locations.Count == 0)
                return $"DSN '{dsn}' is not defined in any ODBC administrator. Check the name, or create it in the {bits} ODBC Data Source Administrator.";
            if (visible.Count == 0)
                return $"DSN '{dsn}' exists only as a {otherBits} DSN ({string.Join(", ", locations)}); this {bits} process cannot see it. Create it in the {bits} ODBC Data Source Administrator.";
        }

        if (sqlState is "IM002" or "IM003" or "IM014")
        {
            foreach (var l in visible)
                if (!DriverInstalled(l.Driver, is64))
                    return $"DSN '{dsn}' uses driver \"{l.Driver}\", which is not installed as a {bits} driver" +
                           (DriverInstalled(l.Driver, !is64) ? $" (only the {otherBits} version is installed)." : ".");
        }
        return null;
    }

    private static void Probe(List<DsnLocation> found, RegistryHive hive, RegistryView view, string scope, string dsn)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(@"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources");
            if (key?.GetValue(dsn) is string driver) found.Add(new DsnLocation(scope, driver));
        }
        catch
        {
            // unreadable registry view: nothing to report
        }
    }
}
