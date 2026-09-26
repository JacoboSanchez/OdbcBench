# OdbcBench

OdbcBench compares ODBC drivers, or versions of one driver, on the same `SELECT`. For every configured DSN it:

1. **Validates** that the first rows (10 by default) and the result-set metadata are the same on every DSN.
2. **Benchmarks** the query with warmup and measured iterations. Each iteration executes the query and reads every row and every column value without storing them.
3. **Writes a comparison report** in Markdown, plus a JSON file with every sample.

The tool calls the ODBC API directly in `odbc32.dll`, the Windows Driver Manager. It uses no wrapper such as pyodbc or System.Data.Odbc, so every ODBC function in the measured path is explicit and listed below.

It is **64-bit only**. It loads the 64-bit Driver Manager and therefore sees only 64-bit drivers and DSNs. Those are the ones in `C:\Windows\System32\odbcad32.exe`.

## Requirements and build

- Windows x64 and the .NET 8 SDK (runtime 8.0 or later to run).
- The 64-bit ODBC drivers you want to compare, and DSNs or connection strings for them.

```powershell
dotnet build -c Release
dotnet test                       # unit tests; no database needed
dotnet publish src/OdbcBench -c Release -p:PublishSingleFile=true -o publish
```

The executable is `publish\OdbcBench.exe`. During development it is `src\OdbcBench\bin\Release\net8.0\win-x64\OdbcBench.exe`.

## Quick start

1. Copy `samples\config.sample.json` to `bench.json`. Set the query, then one entry per DSN.
2. Check that every DSN connects and that the columns are bound as you expect:

   ```powershell
   OdbcBench probe --config bench.json
   ```

3. Run the comparison:

   ```powershell
   OdbcBench run --config bench.json
   ```

The report lands in `results\run-<date>-<time>.md`, next to the `.json` with the raw data.

Before comparing two drivers, run an **A/A test**: list the same DSN twice under two names. The spread between the two tells you the noise floor of your setup. A difference between drivers smaller than that is not a finding.

## Command line

```text
OdbcBench run    --config FILE [options]
OdbcBench probe  --config FILE [--dsn NAME]
OdbcBench report --json FILE [--output DIR]

  -c, --config FILE        configuration file (JSON)
  -n, --iterations N       measured iterations per series
  -w, --warmup N           warmup iterations per series
  -b, --block-size N[,N]   row array sizes to benchmark
  -d, --dsn NAME[,NAME]    only these DSN entries
      --query-file FILE    read the query from FILE
  -o, --output DIR         output directory
      --strict             stop before benchmarking when validation fails
      --no-validate        skip validation and the dry run
      --quiet              no progress output
```

`report` re-renders the Markdown from a saved JSON result.

Press Ctrl+C once to stop after the current iteration and write partial results. Press it a second time to abort immediately.

| Exit code | Meaning |
|---|---|
| 0 | Completed; everything connected, validated and ran. |
| 1 | Completed with problems: a DSN or series failed, row counts differ, validation failed, or the run was interrupted. |
| 2 | Validation failed in strict mode; nothing was benchmarked. |
| 3 | Usage or configuration error, or no DSN could be connected. |

## Configuration

A DSN entry needs a `name` plus either `dsn` or a full `connectionString`. Credentials are appended as `UID=` and `PWD=`. Values containing `;` or `}` are brace-quoted automatically.

| Field | Default | Meaning |
|---|---|---|
| `query` / `queryFile` | | The `SELECT` to run. `queryFile` is relative to the configuration file. |
| `dsns[].name` | | Label used in the report. |
| `dsns[].dsn` / `connectionString` | | What to connect to. |
| `dsns[].uid`, `pwd`, `pwdEnv` | | Password order: `pwd`, then the variable named by `pwdEnv`, then a masked prompt. At the prompt, Enter alone uses the password stored in the DSN. |
| `dsns[].extraAttributes` | | Extra `key=value;` pairs appended to the connection string. |
| `dsns[].query` | | Per-DSN query for dialect differences. |
| `dsns[].enabled` | `true` | Skip an entry without deleting it. |
| `baseline` | first DSN | Every ratio is relative to this DSN. |
| `warmupIterations` | `3` | Iterations run and reported but excluded from the statistics. |
| `iterations` | `20` | Measured iterations per series. |
| `blockSizes` | `[1000]` | Row array sizes (`SQL_ATTR_ROW_ARRAY_SIZE`). Each size is its own series per DSN. |
| `bindMode` | `native` | `native` binds each column in its natural C type. `wchar` binds everything as `SQL_C_WCHAR`, which measures the driver's text conversion. |
| `interleave` | `true` | Each round runs every series once, alternating direction between rounds. Drift then hits every DSN equally. |
| `reuseStatement` | `true` | One statement per series with describe and bind done once. `false` rebuilds it every iteration, and that cost counts. |
| `connectionPerIteration` | `false` | Connect and disconnect in every iteration. Connect time is reported but not part of the total. |
| `connectSamples` | `5` | Extra connections opened and closed to measure connect cost. |
| `longColumnMode` | `rowByRow` | Handling for LOB columns or columns of unknown size. See below. |
| `longColumnThresholdBytes` | `8000` | A column whose bound buffer would exceed this is a long column. |
| `longColumnCapBytes` | `65536` | Buffer per value in `bindCapped` mode. |
| `maxBoundBytes` | 256 MiB | Upper limit for all bound buffers. The row array size is lowered to fit. |
| `queryTimeoutSeconds` | `0` | `SQL_ATTR_QUERY_TIMEOUT`. Zero means none, set explicitly because a DSN may define its own. |
| `loginTimeoutSeconds` | `30` | `SQL_ATTR_LOGIN_TIMEOUT`. |
| `maxConsecutiveErrors` | `3` | A series stops after this many failed iterations in a row. |
| `cacheBuster` | `false` | Appends a unique comment to every execution to defeat result caches. It also defeats plan-cache reuse. |
| `calibrate` | `true` | Measures the harness's own value-reading cost. |
| `processPriority` | `normal` | Or `aboveNormal` or `high`. |
| `pauseBetweenIterationsMs` | `0` | Sleep between iterations. |
| `odbcVersion` | `3.80` | Or `3.0` for a driver that misbehaves under ODBC 3.8 behaviour. |
| `validation.rows` | `10` | Rows compared. |
| `validation.strict` | `false` | Stop before benchmarking when validation fails. |
| `validation.normalization` | | `trimTrailingSpaces`, `nullEqualsEmpty`, `floatTolerance` (relative). |
| `output` | `results`, `run` | Directory, file prefix, and which formats to write. |

Passwords never reach the report, the JSON or the console. Secret-looking attributes (`PWD`, `token`, `secret` and similar) are masked, and the completed connection string returned by the driver is discarded.

## What exactly is measured

**Setup, once per run.** `SQLSetEnvAttr(SQL_ATTR_CONNECTION_POOLING, SQL_CP_OFF)`, `SQLAllocHandle(ENV)` and `SQLSetEnvAttr(SQL_ATTR_ODBC_VERSION, 3.80)`.

**Per DSN.** `SQLDriverConnectW` opens one connection that every iteration reuses. `SQLGetInfoW` reads the driver and DBMS identity and the `SQL_GETDATA_EXTENSIONS` bits. The tool also checks whether the driver exports Unicode entry points; if not, the Driver Manager converts every string, and that cost is part of that driver's time.

**Per series, meaning one DSN at one block size.** One statement handle is kept for the whole run. It is set to forward-only and read-only, with `SQL_ATTR_ROW_BIND_TYPE = SQL_BIND_BY_COLUMN` and `SQL_ATTR_ROW_ARRAY_SIZE = N`. The row array size is read back with `SQLGetStmtAttrW`, because drivers may lower it. The report shows the size that actually ran.

**Per iteration.** Only this part is timed:

| Timer | ODBC calls |
|---|---|
| execute | `SQLExecDirectW` |
| describe + bind | `SQLNumResultCols`, `SQLDescribeColW`, `SQLColAttributeW`, `SQLBindCol`. Only when the statement is built: the dry run, the first warmup, or every iteration with `reuseStatement: false`. |
| first batch | The first `SQLFetchScroll(SQL_FETCH_NEXT)`, which returns the first row array. |
| fetch | Every `SQLFetchScroll` until `SQL_NO_DATA`, plus reading every value. |
| close | `SQLFreeStmt(SQL_CLOSE)` |
| total | execute + describe/bind + fetch + close |

Every value is read from the bound arrays through raw pointers and folded into a per-column FNV-1a checksum. Nothing is stored, and the compiler cannot skip the reads. The checksum does not depend on the block size, so it also proves that every block size returned the same data. The fetch loop allocates no managed memory; the report shows this per iteration.

**Type mapping.** Integers, floating point, bit, date, timestamp and GUID columns bind as their C types. Character data binds as `SQL_C_WCHAR`. `DECIMAL` and `NUMERIC` bind as text, because `SQL_C_NUMERIC` needs per-driver descriptor tweaks. Driver-specific types bind as text, sized from `SQL_DESC_DISPLAY_SIZE`. If a driver rejects a binding with SQLSTATE 07006, that column falls back to text and the report says so.

**Long columns.** These are LOBs, `(max)` types, or columns of unknown size.

- `rowByRow` (the default) reads long columns completely with chunked `SQLGetData`. This needs a row array size of 1, so block fetch is off for that query and the report says so. Drivers without `SQL_GD_ANY_COLUMN` also read every later column with `SQLGetData`.
- `bindCapped` keeps block fetch. Long values are bound with a buffer of `longColumnCapBytes`, and truncated values are counted.

**Resources.** CPU is process user + kernel time. Windows counts it in ticks of about 15.6 ms, so it is coarse for short iterations. Forced garbage collections happen between iterations, outside the timers.

## Validation

Each DSN's first rows are read with `SQLFetch` and `SQLGetData` as text. That path is separate from the block-fetch path, so a binding bug cannot hide a data difference. Values are normalised before comparing:

- decimals by value, so `1.50` equals `1.5`;
- floats with a relative tolerance;
- timestamps at the coarser fractional precision of the two;
- GUIDs, binary and booleans in canonical form;
- trailing spaces trimmed, for `CHAR` padding.

| Verdict | When |
|---|---|
| FAIL | A column count or name differs, a row count differs, or any value differs. A missing or extra row is reported as a shift instead of dozens of cell differences. |
| WARN | A column's type family differs, for example a timestamp delivered as text; only the fractional-second precision differs; the query has no `ORDER BY`; the statement returns extra result sets; or the dry run truncated values. |

After validation, every series runs once with the real block-fetch path, reading only the first rows. This dry run surfaces binding problems and row-array-size changes before any timing starts.

During the benchmark, row counts must match across all DSNs and iterations. Checksums must be stable across the iterations of each series and equal across block sizes. Cross-DSN checksums are informational, because two drivers can return the same value in different C types.

## Reading the report

- **Summary.** The fastest DSN per block size and the p50 total of every DSN, as a ratio to the baseline. It also lists validation, consistency, failures and noisy series (coefficient of variation above 20%).
- **Environment.** Machine, Driver Manager, and each driver's name, version, ODBC version and connect cost.
- **Validation and column bindings.** Types per DSN and the C binding chosen for each column.
- **Results per block size.**
  - Total time: min, p50, mean, p95, max, standard deviation, CV, outliers and ratio to baseline.
  - Breakdown: execute, first batch, fetch and close.
  - Throughput and resources: rows/s, MB/s, CPU, truncations, garbage collections and effective row array size.
- **Data consistency and harness overhead.** The estimated share of the time spent in the tool's own value-reading loop.
- **Warnings and errors, and an appendix with every iteration.**

Percentiles use the nearest-rank method. Outliers, meaning samples above p50 + 3 × 1.4826 × MAD, are flagged but never dropped.

## Getting trustworthy numbers

- Use an `ORDER BY`, so validation compares the same rows and checksums are stable.
- Use enough iterations: 20 at least, 30 or more for a meaningful p95, and more when an iteration takes under 50 ms.
- Keep `interleave` on, use the High performance power plan, and keep other load off the client and the server.
- Engines with result caches, such as Dremio reflections, can make repeated executions unrealistically fast. Compare with `cacheBuster: true` if that matters.
- When DSNs point at different database engines, the comparison covers engine plus driver, not the driver alone.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `IM002` data source name not found | The DSN is missing, or it exists only in the 32-bit ODBC administrator. The tool's hint tells you which. Create it in the 64-bit administrator. |
| `IM014` architecture mismatch | The DSN points at a 32-bit driver. |
| Row array size changed | The driver capped `SQL_ATTR_ROW_ARRAY_SIZE` (SQLSTATE 01S02). The report shows the effective value. |
| Truncated values | The driver reports column sizes smaller than the data. Raise `longColumnThresholdBytes` or use `bindMode: wchar`. |
| `SQL_ATTR_QUERY_TIMEOUT ... not accepted` | Informational: some drivers do not support query timeouts. |

## Project layout

| Path | Contents |
|---|---|
| `src\OdbcBench\Odbc` | P/Invoke declarations and thin wrappers for the environment, connection and statement handles, type mapping, and registry hints. |
| `src\OdbcBench\Fetch` | The block-fetch reader, binding plan, column buffers, and chunked `SQLGetData`. |
| `src\OdbcBench\Validation` | Sample reader, value normalisation and comparison. |
| `src\OdbcBench\Bench` | Runner, statistics, analysis and system information. |
| `src\OdbcBench\Report` | Result model, JSON writer and Markdown writer. |
| `tests\OdbcBench.Tests` | Unit tests and the golden report (`Golden\report.md`). After an intended report change, regenerate it with `UPDATE_GOLDEN=1`. |
