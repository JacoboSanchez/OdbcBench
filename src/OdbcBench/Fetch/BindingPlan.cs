using System.Globalization;
using OdbcBench.Odbc;

namespace OdbcBench.Fetch;

/// <summary>What to do with columns that cannot be bound with a bounded buffer (LOBs, unknown sizes).</summary>
public enum LongColumnMode
{
    /// <summary>Row array size 1 and chunked SQLGetData for the long columns: every byte is read, block fetch is off for that series.</summary>
    RowByRow,
    /// <summary>Keep block fetch; bind long columns with a capped buffer and count truncations.</summary>
    BindCapped,
}

/// <summary>Bound/unbound decision per column plus the final row array size (plan section "Long columns and the mixed bound/unbound rule").</summary>
public sealed class BindingPlan
{
    public int RequestedBlockSize { get; init; }
    /// <summary>Row array size the driver accepted for the requested value (before the long-column and memory rules).</summary>
    public int DriverBlockSize { get; init; }
    /// <summary>Row array size the statement actually uses.</summary>
    public int ArraySize { get; internal set; }
    public IReadOnlyList<ColumnInfo> Columns { get; init; } = Array.Empty<ColumnInfo>();
    public IReadOnlyList<ColumnInfo> Bound { get; init; } = Array.Empty<ColumnInfo>();
    /// <summary>Columns read with SQLGetData after each fetch, in ascending ordinal order.</summary>
    public IReadOnlyList<ColumnInfo> Unbound { get; init; } = Array.Empty<ColumnInfo>();
    public string? BlockFetchDisabledBy { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    /// <summary>Bound bytes per row (data + indicators).</summary>
    public long RowBytes { get; init; }
    public long BoundBytes => (long)ArraySize * RowBytes;

    public static BindingPlan Build(
        IReadOnlyList<ColumnInfo> columns,
        int requestedBlockSize,
        int driverBlockSize,
        bool getDataAnyColumn,
        LongColumnMode longMode,
        int longColumnCapBytes,
        long maxBoundBytes)
    {
        var warnings = new List<string>();
        int arraySize = Math.Max(1, driverBlockSize);
        if (driverBlockSize != requestedBlockSize)
            warnings.Add($"driver accepted a row array size of {driverBlockSize} instead of the requested {requestedBlockSize} (SQLSTATE 01S02)");

        var bound = new List<ColumnInfo>(columns.Count);
        var unbound = new List<ColumnInfo>();
        string? disabledBy = null;

        var longColumns = columns.Where(c => c.IsLong).ToList();
        if (longColumns.Count > 0 && longMode == LongColumnMode.BindCapped)
        {
            int cap = Math.Max(64, longColumnCapBytes) & ~1;
            foreach (var c in longColumns)
            {
                c.ElementBytes = cap;
                c.IsLong = false;
                c.Capped = true;
            }
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"long column(s) {Names(longColumns)} bound with a {cap:N0}-byte cap: values beyond the cap are truncated and counted (longColumnMode = bindCapped)"));
            longColumns.Clear();
        }

        if (longColumns.Count > 0)
        {
            arraySize = 1;
            disabledBy = longColumns[0].Name;
            warnings.Add($"block fetch disabled: long column '{disabledBy}' ({longColumns[0].SqlTypeName}) is read with SQLGetData row by row (longColumnMode = rowByRow)");
            bool afterLong = false;
            bool forcedUnbound = false;
            foreach (var c in columns)
            {
                if (c.IsLong)
                {
                    afterLong = true;
                    unbound.Add(c);
                }
                else if (afterLong && !getDataAnyColumn)
                {
                    unbound.Add(c);
                    forcedUnbound = true;
                }
                else
                {
                    bound.Add(c);
                }
            }
            if (forcedUnbound)
                warnings.Add("driver lacks SQL_GD_ANY_COLUMN: every column after the first long column is read with SQLGetData as well");
        }
        else
        {
            bound.AddRange(columns);
        }

        long rowBytes = 0;
        foreach (var c in bound) rowBytes += c.ElementBytes + IntPtr.Size;

        if (rowBytes > 0 && (long)arraySize * rowBytes > maxBoundBytes)
        {
            int lowered = (int)Math.Max(1, Math.Min(int.MaxValue, maxBoundBytes / rowBytes));
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"row array size lowered from {arraySize} to {lowered}: one row needs {rowBytes:N0} bytes of bound buffers and maxBoundBytes is {maxBoundBytes:N0}"));
            arraySize = lowered;
        }

        return new BindingPlan
        {
            RequestedBlockSize = requestedBlockSize,
            DriverBlockSize = driverBlockSize,
            ArraySize = arraySize,
            Columns = columns,
            Bound = bound,
            Unbound = unbound,
            BlockFetchDisabledBy = disabledBy,
            Warnings = warnings,
            RowBytes = rowBytes,
        };
    }

    private static string Names(IEnumerable<ColumnInfo> columns) => string.Join(", ", columns.Select(c => $"'{c.Name}'"));
}
