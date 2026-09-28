using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OdbcBench.Odbc;

/// <summary>
/// Locates the ODBC Driver Manager for the current OS: odbc32.dll from System32 on Windows, unixODBC's libodbc on
/// Linux and macOS. <c>ODBCBENCH_DRIVER_MANAGER</c> overrides the library (a file name or a full path), for example
/// to compare two unixODBC builds. Also maps the logical name "libc" used by the Unix system calls in this assembly.
/// </summary>
internal static class DriverManager
{
    /// <summary>Logical library name used by every ODBC <c>LibraryImport</c>; mapped to the real file here.</summary>
    public const string LibraryName = "odbc";
    /// <summary>Logical library name of the C runtime on Unix (clock_gettime and friends).</summary>
    public const string LibcName = "libc";
    public const string OverrideVariable = "ODBCBENCH_DRIVER_MANAGER";

    private static readonly object Gate = new();
    private static nint _handle;
    private static string? _loadedName;

    /// <summary>The library the Driver Manager was loaded from, or null before the first ODBC call.</summary>
    public static string? LoadedName => _loadedName;

    /// <summary>File names tried, in order, when no override is set.</summary>
    public static IReadOnlyList<string> Candidates()
    {
        if (OperatingSystem.IsWindows()) return new[] { "odbc32.dll" };
        if (OperatingSystem.IsMacOS())
            return new[]
            {
                "libodbc.2.dylib", "libodbc.dylib",
                "/opt/homebrew/lib/libodbc.2.dylib", "/usr/local/lib/libodbc.2.dylib",
            };
        return new[] { "libodbc.so.2", "libodbc.so.1", "libodbc.so" };
    }

    [ModuleInitializer]
    internal static void Register() => NativeLibrary.SetDllImportResolver(typeof(DriverManager).Assembly, Resolve);

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name == LibraryName) return Load(assembly, searchPath);
        if (name == LibcName && !OperatingSystem.IsWindows()) return NativeLibrary.GetMainProgramHandle();
        return 0; // default probing
    }

    private static nint Load(Assembly assembly, DllImportSearchPath? searchPath)
    {
        lock (Gate)
        {
            if (_handle != 0) return _handle;

            string? overridden = Environment.GetEnvironmentVariable(OverrideVariable);
            var names = string.IsNullOrWhiteSpace(overridden) ? Candidates() : new[] { overridden.Trim() };
            foreach (var candidate in names)
            {
                if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out _handle))
                {
                    _loadedName = candidate;
                    return _handle;
                }
            }

            string install = OperatingSystem.IsWindows()
                ? "odbc32.dll ships with Windows; check that System32 is intact."
                : OperatingSystem.IsMacOS()
                    ? "Install unixODBC (for example 'brew install unixodbc')."
                    : "Install unixODBC (for example 'apt install unixodbc' or 'dnf install unixODBC').";
            throw new DllNotFoundException(
                $"ODBC Driver Manager not found (tried {string.Join(", ", names)}). {install} " +
                $"Set {OverrideVariable} to the library path to use a specific one.");
        }
    }
}
