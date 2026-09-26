using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using OdbcBench.Report;

namespace OdbcBench.Bench;

internal static class ToolInfo
{
    public static string Version { get; } =
        typeof(ToolInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(ToolInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}

/// <summary>Process CPU time (user + kernel) from GetProcessTimes: allocation-free, fresh on every call.</summary>
internal static partial class ProcessCpu
{
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    public static double NowMs()
    {
        return GetProcessTimes(GetCurrentProcess(), out _, out _, out long kernel, out long user)
            ? (kernel + user) / 10_000.0 // 100 ns units
            : 0;
    }
}

/// <summary>Installed physical memory as reported by Windows (GlobalMemoryStatusEx).</summary>
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
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.TotalPhys : GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
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

    public static string DriverManagerFileVersion()
    {
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

    private static readonly Dictionary<string, string> KnownPlans = new(StringComparer.OrdinalIgnoreCase)
    {
        ["381b4222-f694-41f0-9685-ff5bb260df2e"] = "Balanced",
        ["8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"] = "High performance",
        ["a1841308-3541-4fab-bc81-f71556f20b4a"] = "Power saver",
        ["e9a42b02-d5df-448d-aa00-03f14749eb61"] = "Ultimate Performance",
    };

    private static string PowerPlan()
    {
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
}
