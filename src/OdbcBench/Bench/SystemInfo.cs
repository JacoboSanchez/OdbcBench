using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using OdbcBench.Odbc;
using OdbcBench.Report;

namespace OdbcBench.Bench;

internal static class ToolInfo
{
    public static string Version { get; } =
        typeof(ToolInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(ToolInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}

/// <summary>
/// Process CPU time (user + kernel), allocation-free and fresh on every call: GetProcessTimes on Windows,
/// clock_gettime(CLOCK_PROCESS_CPUTIME_ID) on Linux and macOS.
/// </summary>
internal static unsafe partial class ProcessCpu
{
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec
    {
        public long Seconds;     // time_t, 8 bytes on 64-bit Linux and macOS
        public long Nanoseconds; // long
    }

    [LibraryImport(DriverManager.LibcName, EntryPoint = "clock_gettime")]
    private static partial int ClockGetTime(int clockId, TimeSpec* time);

    private static readonly int ProcessClock = OperatingSystem.IsMacOS() ? 12 : 2; // CLOCK_PROCESS_CPUTIME_ID

    public static double NowMs()
    {
        if (OperatingSystem.IsWindows())
            return GetProcessTimes(GetCurrentProcess(), out _, out _, out long kernel, out long user)
                ? (kernel + user) / 10_000.0 // 100 ns units
                : 0;

        TimeSpec time;
        return ClockGetTime(ProcessClock, &time) == 0 ? time.Seconds * 1000.0 + time.Nanoseconds / 1_000_000.0 : 0;
    }
}

/// <summary>Installed physical memory: GlobalMemoryStatusEx on Windows, /proc/meminfo on Linux, the GC's view otherwise.</summary>
internal static partial class PhysicalMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    public static double TotalBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status)) return status.TotalPhys;
        }
        else if (OperatingSystem.IsLinux() && SystemInfo.ReadProcField("/proc/meminfo", "MemTotal") is { } total)
        {
            // "16318044 kB"
            var number = total.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double kb)) return kb * 1024;
        }
        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    }
}

/// <summary>Machine facts recorded in the report so runs on different machines are not compared blindly.</summary>
internal static class SystemInfo
{
    public static EnvironmentInfo Collect() => new()
    {
        Os = $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture})",
        Cpu = CpuName(),
        LogicalProcessors = Environment.ProcessorCount,
        MemoryGb = Math.Round(PhysicalMemory.TotalBytes() / 1024.0 / 1024 / 1024, 1),
        PowerPlan = PowerPlan(),
        Runtime = $"{RuntimeInformation.FrameworkDescription} {RuntimeInformation.ProcessArchitecture}",
        DriverManagerVersion = DriverManagerFileVersion(),
        TieredCompilation = !AppContext.TryGetSwitch("System.Runtime.TieredCompilation", out bool tiered) || tiered,
        HighResolutionTimer = Stopwatch.IsHighResolution,
    };

    /// <summary>
    /// odbc32.dll's file version on Windows. On Linux and macOS, the unixODBC library that was loaded (the version itself
    /// comes from SQLGetInfo(SQL_DM_VER) once a connection is open).
    /// </summary>
    public static string DriverManagerFileVersion()
    {
        if (!OperatingSystem.IsWindows()) return DriverManager.LoadedName is { } name ? $"unixODBC ({name})" : "unixODBC";
        try
        {
            var info = FileVersionInfo.GetVersionInfo(Path.Combine(Environment.SystemDirectory, "odbc32.dll"));
            return info.FileVersion?.Split(' ')[0] ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string CpuName()
    {
        if (OperatingSystem.IsLinux())
            return ReadProcField("/proc/cpuinfo", "model name") ?? ReadProcField("/proc/cpuinfo", "Model") ?? "";
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Value of the first "key : value" line of a /proc file, or null.</summary>
    internal static string? ReadProcField(string path, string key)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && line.AsSpan(0, colon).Trim().SequenceEqual(key)) return line[(colon + 1)..].Trim();
            }
        }
        catch
        {
            // not readable: nothing to report
        }
        return null;
    }

    private static readonly Dictionary<string, string> KnownPlans = new(StringComparer.OrdinalIgnoreCase)
    {
        ["381b4222-f694-41f0-9685-ff5bb260df2e"] = "Balanced",
        ["8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"] = "High performance",
        ["a1841308-3541-4fab-bc81-f71556f20b4a"] = "Power saver",
        ["e9a42b02-d5df-448d-aa00-03f14749eb61"] = "Ultimate Performance",
    };

    private static string PowerPlan()
    {
        if (OperatingSystem.IsLinux()) return LinuxPowerSettings();
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            using var schemes = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
            if (schemes?.GetValue("ActivePowerScheme") is not string guid) return "unknown";
            if (KnownPlans.TryGetValue(guid, out var name)) return name;
            using var scheme = schemes.OpenSubKey(guid);
            if (scheme?.GetValue("FriendlyName") is string friendly && friendly.Length > 0)
            {
                int comma = friendly.LastIndexOf(',');
                return comma >= 0 ? friendly[(comma + 1)..].Trim() : friendly.Trim();
            }
            return guid;
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>The Linux counterparts of a power plan: the cpufreq governor and, where the firmware has one, the platform profile.</summary>
    private static string LinuxPowerSettings()
    {
        var parts = new List<string>(2);
        var governor = ReadFirstLine("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor");
        if (governor != null) parts.Add($"cpufreq governor {governor}");
        var profile = ReadFirstLine("/sys/firmware/acpi/platform_profile");
        if (profile != null) parts.Add($"platform profile {profile}");
        return parts.Count == 0 ? "unknown" : string.Join(", ", parts);
    }

    private static string? ReadFirstLine(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadLines(path).FirstOrDefault()?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }
}
