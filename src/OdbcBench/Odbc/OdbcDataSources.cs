using Microsoft.Win32;

namespace OdbcBench.Odbc;

/// <summary>
/// Finds where a DSN and its driver are defined so connect failures (IM002 "data source not found", IM003 "driver could
/// not be loaded", IM014 "architecture mismatch") can be explained. On Windows it reads the ODBC registry in both views,
/// because a 64-bit process only sees 64-bit system DSNs and 64-bit drivers. On Linux and macOS it reads unixODBC's
/// odbc.ini and odbcinst.ini files.
/// </summary>
public static class OdbcDataSources
{
    public sealed record DsnLocation(string Scope, string Driver)
    {
        public override string ToString() => $"{Scope} -> \"{Driver}\"";
    }

    public static List<DsnLocation> FindDsn(string dsn)
    {
        var found = new List<DsnLocation>();
        if (string.IsNullOrWhiteSpace(dsn)) return found;
        if (OperatingSystem.IsWindows())
        {
            Probe(found, RegistryHive.LocalMachine, RegistryView.Registry64, "64-bit system DSN", dsn);
            Probe(found, RegistryHive.LocalMachine, RegistryView.Registry32, "32-bit system DSN", dsn);
            Probe(found, RegistryHive.CurrentUser, RegistryView.Registry64, "user DSN", dsn);
        }
        else
        {
            foreach (var (scope, path) in UnixOdbc.DsnFiles())
            {
                var section = UnixOdbc.Section(path, dsn);
                if (section != null) found.Add(new DsnLocation($"{scope} ({path})", section.GetValueOrDefault("Driver", "")));
            }
        }
        return found;
    }

    public static bool DriverInstalled(string driver, bool is64Bit)
    {
        if (!OperatingSystem.IsWindows()) return UnixOdbc.DriverLibrary(driver) is { } library && UnixOdbc.LibraryExists(library);
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

    /// <summary>Explains a connect failure in terms of DSN and driver definitions, or returns null when there is nothing to add.</summary>
    public static string? Hint(string? dsn, string? sqlState)
    {
        if (string.IsNullOrWhiteSpace(dsn)) return null;
        return OperatingSystem.IsWindows() ? WindowsHint(dsn, sqlState) : UnixHint(dsn, sqlState);
    }

    private static string? WindowsHint(string dsn, string? sqlState)
    {
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

    private static string? UnixHint(string dsn, string? sqlState)
    {
        var locations = FindDsn(dsn);
        if (sqlState == "IM002" && locations.Count == 0)
        {
            var searched = UnixOdbc.DsnFiles().Select(f => f.Path).ToList();
            return $"DSN '{dsn}' is not defined in {(searched.Count == 0 ? "any odbc.ini file" : string.Join(" or ", searched))}. " +
                   "Check the name, or add a [" + dsn + "] section; 'odbcinst -j' lists the files unixODBC reads.";
        }

        if (sqlState is "IM002" or "IM003" or "01000")
        {
            foreach (var l in locations)
            {
                if (l.Driver.Length == 0)
                    return $"DSN '{dsn}' ({l.Scope}) has no Driver= entry.";
                var library = UnixOdbc.DriverLibrary(l.Driver);
                if (library == null)
                    return $"DSN '{dsn}' uses driver \"{l.Driver}\", which is not defined in {string.Join(" or ", UnixOdbc.DriverFiles())}.";
                if (!UnixOdbc.LibraryExists(library))
                    return $"DSN '{dsn}' uses driver \"{l.Driver}\", whose library {library} does not exist.";
            }
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

/// <summary>
/// The configuration files unixODBC reads, located the way unixODBC does: ODBCINI and ODBCSYSINI/ODBCINSTINI first,
/// then ~/.odbc.ini and the usual system configuration directories.
/// </summary>
internal static class UnixOdbc
{
    private static readonly string[] SystemDirectories = { "/etc", "/usr/local/etc", "/opt/homebrew/etc", "/etc/unixODBC" };

    /// <summary>The odbc.ini files that exist, user first.</summary>
    public static List<(string Scope, string Path)> DsnFiles()
    {
        var files = new List<(string, string)>();
        string? user = Environment.GetEnvironmentVariable("ODBCINI");
        if (string.IsNullOrWhiteSpace(user))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            user = home.Length > 0 ? Path.Combine(home, ".odbc.ini") : null;
        }
        if (user != null && File.Exists(user)) files.Add(("user DSN", user));
        string? system = FirstExisting("odbc.ini");
        if (system != null && system != user) files.Add(("system DSN", system));
        return files;
    }

    /// <summary>The odbcinst.ini files that exist.</summary>
    public static List<string> DriverFiles()
    {
        var files = new List<string>();
        string? explicitFile = Environment.GetEnvironmentVariable("ODBCINSTINI");
        if (!string.IsNullOrWhiteSpace(explicitFile))
        {
            if (!Path.IsPathRooted(explicitFile))
                explicitFile = Path.Combine(Environment.GetEnvironmentVariable("ODBCSYSINI") ?? "/etc", explicitFile);
            if (File.Exists(explicitFile)) files.Add(explicitFile);
        }
        var system = FirstExisting("odbcinst.ini");
        if (system != null && !files.Contains(system)) files.Add(system);
        return files;
    }

    /// <summary>
    /// The shared library a DSN's Driver= value points to: the value itself when it is a path, otherwise the Driver=
    /// entry of the matching odbcinst.ini section. Null when the driver name is not defined anywhere.
    /// </summary>
    public static string? DriverLibrary(string driver)
    {
        if (driver.Contains('/')) return driver;
        foreach (var file in DriverFiles())
        {
            var section = Section(file, driver);
            if (section != null && section.TryGetValue("Driver", out var library) && library.Length > 0)
                return library;
        }
        return null;
    }

    /// <summary>False only for an absolute path that is missing; a bare file name is found by the dynamic loader.</summary>
    public static bool LibraryExists(string library) => !Path.IsPathRooted(library) || File.Exists(library);

    /// <summary>The keys of section [name] in an INI file (names compared case-insensitively), or null when absent.</summary>
    public static Dictionary<string, string>? Section(string path, string name)
    {
        try
        {
            Dictionary<string, string>? current = null;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';') continue;
                if (line[0] == '[')
                {
                    if (current != null) return current;
                    int end = line.IndexOf(']');
                    if (end > 0 && string.Equals(line[1..end].Trim(), name, StringComparison.OrdinalIgnoreCase))
                        current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }
                if (current == null) continue;
                int eq = line.IndexOf('=');
                if (eq > 0) current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            return current;
        }
        catch
        {
            return null; // unreadable file: nothing to report
        }
    }

    private static string? FirstExisting(string fileName)
    {
        string? sysIni = Environment.GetEnvironmentVariable("ODBCSYSINI");
        var directories = string.IsNullOrWhiteSpace(sysIni) ? SystemDirectories : new[] { sysIni };
        return directories.Select(d => Path.Combine(d, fileName)).FirstOrDefault(File.Exists);
    }
}
