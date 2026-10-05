# OdbcBench

OdbcBench compares ODBC drivers, or versions of one driver, on the same `SELECT` or on the same batch insert. For every configured DSN it:

1. **Validates** that the first rows (10 by default) and the result-set metadata are the same on every DSN.
2. **Benchmarks** the query with warmup and measured iterations. Each iteration executes the query and reads every row and every column value without storing them.
3. **Writes a comparison report** in Markdown, plus a JSON file with every sample.

`OdbcBench insert` does the same for writes: it inserts generated rows into a table with parameter arrays and compares the drivers. See [Batch insert benchmark](#batch-insert-benchmark).

The tool calls the ODBC API of the Driver Manager directly: `odbc32.dll` on Windows, unixODBC's `libodbc` on Linux and macOS. It uses no wrapper such as pyodbc or System.Data.Odbc, so every ODBC function in the measured path is explicit and listed below.

It is **64-bit only**. It loads the 64-bit Driver Manager and therefore sees only 64-bit drivers and DSNs. On Windows those are the ones in `C:\Windows\System32\odbcad32.exe`; on Linux and macOS they are the ones in the `odbc.ini` and `odbcinst.ini` files that `odbcinst -j` lists.

## Requirements and build

- Windows, Linux or macOS on a 64-bit CPU (x64 or ARM64), and the .NET 8 SDK (runtime 8.0 or later to run).
- On Linux and macOS, **unixODBC** 2.3 or later (`apt install unixodbc`, `dnf install unixODBC`, `brew install unixodbc`). iODBC is not supported: its `SQLWCHAR` is 4 bytes, while the tool passes UTF-16 strings.
- The 64-bit ODBC drivers you want to compare, and DSNs or connection strings for them.

```sh
dotnet build -c Release
dotnet test                       # unit tests; no database needed
dotnet publish src/OdbcBench -c Release -r win-x64   -p:PublishSingleFile=true -o publish   # Windows
dotnet publish src/OdbcBench -c Release -r linux-x64 -p:PublishSingleFile=true -o publish   # Linux (or linux-arm64, osx-arm64)
```

The executable is `publish/OdbcBench.exe` on Windows and `publish/OdbcBench` elsewhere. During development it is `src/OdbcBench/bin/Release/net8.0/OdbcBench[.exe]`.

On Linux and macOS the tool looks for `libodbc.so.2` (`libodbc.2.dylib` on macOS) on the library path. Set `ODBCBENCH_DRIVER_MANAGER` to a file name or full path to load a different Driver Manager build.

## Quick start

1. Copy `samples/config.sample.json` to `bench.json`. Set the query, then one entry per DSN.
   If the target database has no benchmark data yet, start from `samples/init.sample.json` and run
   `OdbcBench init --config bench.json`; see [Initialize benchmark data](#initialize-benchmark-data).
2. Check that every DSN connects and that the columns are bound as you expect:

   ```sh
   OdbcBench probe --config bench.json
   ```

3. Run the comparison:

   ```sh
   OdbcBench run --config bench.json
   ```

The report lands in `results/run-<date>-<time>.md`, next to the `.json` with the raw data.

Before comparing two drivers, run an **A/A test**: list the same DSN twice under two names. The spread between the two tells you the noise floor of your setup. A difference between drivers smaller than that is not a finding.

## Command line

```text
OdbcBench run    --config FILE [options]
OdbcBench insert --config FILE [options]
OdbcBench init   --config FILE [options]
OdbcBench probe  --config FILE [--dsn NAME]
OdbcBench report --json FILE [--output DIR]

  -c, --config FILE        configuration file (JSON)
  -n, --iterations N       measured iterations per series
  -w, --warmup N           warmup iterations per series
  -b, --block-size N[,N]   row array sizes to benchmark; for insert, parameter array sizes
      --batch-size N[,N]   same as --block-size; init: population parameter-array size
  -d, --dsn NAME[,NAME]    only these DSN entries
      --query-file FILE    read the query from FILE
      --table NAME         insert: target table of every DSN
      --rows N             insert: rows inserted per iteration; init: table sizes (N[,N])
      --recreate           init: replace generated tables that already exist
      --row-work-us X      run: simulated client work per fetched row, in microseconds
  -o, --output DIR         output directory
      --strict             stop before benchmarking when validation fails
      --no-validate        skip validation and the dry run
      --quiet              no progress output
```

`report` re-renders the Markdown from a saved JSON result, for either workload.

Press Ctrl+C once to stop after the current iteration and write partial results. Press it a second time to abort immediately.

| Exit code | Meaning |
|---|---|
| 0 | Completed; everything connected, validated and ran. |
| 1 | Completed with problems: a DSN or series failed, row counts differ, validation failed, initialization failed, or the run was interrupted. |
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
| `processPriority` | `normal` | Or `aboveNormal` or `high`. On Linux and macOS raising the priority needs root or `CAP_SYS_NICE`; without it the run continues at normal priority with a warning. |
| `pauseBetweenIterationsMs` | `0` | Sleep between iterations. |
| `rowProcessingMicros` | `0` | Run only: simulated client work. After every row array the reader busy-waits this many microseconds per fetched row, as CPU-bound client code would. The time is inside fetch and total time and is reported per sample as `processingMs`, so drivers that prefetch while the client works show a smaller increase. `--row-work-us` overrides it. |
| `odbcVersion` | `3.80` | Or `3.0` for a driver that misbehaves under ODBC 3.8 behaviour. |
| `validation.rows` | `10` | Rows compared. |
| `validation.strict` | `false` | Stop before benchmarking when validation fails. |
| `validation.normalization` | | `trimTrailingSpaces`, `nullEqualsEmpty`, `floatTolerance` (relative). |
| `output` | `results`, `run` | Directory, file prefix, and which formats to write. |

Passwords never reach the report, the JSON or the console. Secret-looking attributes (`PWD`, `token`, `secret` and similar) are masked, and the completed connection string returned by the driver is discarded.

## Initialize benchmark data

`OdbcBench init` creates a repeatable dataset through ODBC, so setup exercises the target driver and is not tied to
PostgreSQL client tools. It currently has DDL dialects for **PostgreSQL, SQL Server and Oracle**. It initializes one
DSN: the first enabled entry by default, or the single entry selected with `--dsn`. This avoids loading the same shared
database once through every driver that will later be compared.

```sh
cp samples/init.sample.json bench.json
export BENCH_PG_PWD=...
OdbcBench init --config bench.json
# Deliberately replace an earlier generated dataset:
OdbcBench init --config bench.json --recreate
```

The default size × shape matrix has 10,000-row and 1,000,000-row copies of each read shape:

| Shape | Purpose | Representative columns |
|---|---|---|
| `narrow` | Low row-width and call overhead | bigint key, integer, double, timestamp |
| `numeric` | Native numeric conversion | small/int/big integers, decimal, real, double, boolean |
| `text` | Character conversion and bandwidth | 32, 128 and 512-character values |
| `wide` | Application-like mixed rows | numeric, boolean, date/time, three text widths and binary |

Every read table gets a unique index on `id`; tables with `category` also get a secondary index. Indexes are named
`ix_<table>_<column>`, shortened with a hash suffix when that exceeds the DBMS identifier limit (30 bytes on Oracle, so
that databases whose `COMPATIBLE` setting is below 12.2 also work). Statistics are updated after loading where the
dialect supports it. `insert_target` has the wide shape but starts empty and has no index, so it can be used by
`OdbcBench insert`. Values are deterministic functions of the row number, just like the insert benchmark, and no
server-specific data generator is used.

Use `ORDER BY id` in read queries so driver validation and checksums see a stable order. For example:

```sql
SELECT * FROM odbcbench.read_wide_1000000 ORDER BY id
```

Typical comparisons are the same shape at different row counts (fixed ODBC overhead versus throughput), different
shapes at the same row count (conversion and row-width cost), a full ordered scan versus an `id` range, and native
versus `wchar` bind mode. The `category` index also permits selective range tests without changing the dataset.
On PostgreSQL, psqlODBC describes `bytea` as a long binary column; use `longColumnMode: "bindCapped"` (as in the init
sample) when testing row arrays on the complete wide shape, or deliberately leave the default `rowByRow` mode to test
the driver's chunked long-value path.

| `initialize` field | Default | Meaning |
|---|---|---|
| `schema` | `odbcbench` | Schema for generated objects. Empty uses the connection's default schema. Oracle schemas are users: name an existing user or use empty. |
| `existing` | `fail` | Refuse to touch an existing generated table. `recreate` (or `--recreate`) drops and rebuilds it. |
| `rowCounts` | `[10000, 1000000]` | One read table size for every selected shape. `--rows` overrides it. |
| `shapes` | all four | Any of `narrow`, `wide`, `text`, `numeric`. |
| `batchSize` | `1000` | ODBC parameter-array size used to load rows. `--batch-size` overrides it. |
| `valueLength` | `512` | Maximum generated characters per text value and bytes per binary value, capped by the column declaration. |
| `createIndexes` | `true` | Create the read-table indexes after loading. |
| `analyze` | `true` | Update optimizer statistics (PostgreSQL and SQL Server). |
| `createInsertTable` | `true` | Also create an empty mixed-type insert target. |
| `insertTable` | `insert_target` | Name of that insert target inside `schema`; at most 30 bytes on Oracle. |

Initialization is intentionally safe by default: it never drops an object without `existing: "recreate"` or
`--recreate`, and it only manages its known table names. DDL is committed table by table because Oracle implicitly
commits DDL; if setup is interrupted, rerun with `--recreate` to rebuild the complete matrix.

## What exactly is measured

**Setup, once per run.** `SQLSetEnvAttr(SQL_ATTR_CONNECTION_POOLING, SQL_CP_OFF)`, `SQLAllocHandle(ENV)` and `SQLSetEnvAttr(SQL_ATTR_ODBC_VERSION, 3.80)`.

**Per DSN.** `SQLDriverConnectW` opens one connection that every iteration reuses. `SQLGetInfoW` reads the driver and DBMS identity and the `SQL_GETDATA_EXTENSIONS` bits. On Windows the tool also checks whether the driver exports Unicode entry points; if not, the Driver Manager converts every string, and that cost is part of that driver's time. unixODBC does not expose the driver's module handle, so on Linux and macOS this shows as "unknown".

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

**Resources.** CPU is process user + kernel time (`GetProcessTimes` on Windows, `clock_gettime(CLOCK_PROCESS_CPUTIME_ID)` on Linux and macOS). Windows counts it in ticks of about 15.6 ms, so there it is coarse for short iterations. Forced garbage collections happen between iterations, outside the timers.

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
- Keep `interleave` on, use the High performance power plan on Windows or the `performance` cpufreq governor on Linux (the report records either), and keep other load off the client and the server.
- Engines with result caches, such as Dremio reflections, can make repeated executions unrealistically fast. Compare with `cacheBuster: true` if that matters.
- When DSNs point at different database engines, the comparison covers engine plus driver, not the driver alone.

## Batch insert benchmark

`OdbcBench insert` writes generated rows into a table through every DSN and compares how long the drivers take. It uses the settings above (DSNs, iterations, warmup, interleaving, `bindMode`, `reuseStatement`, `connectionPerIteration`, `maxBoundBytes`) plus an `insert` section. `blockSizes`, or `--batch-size`, gives the parameter array sizes, one series each. A single configuration file can hold both the query and the `insert` section.

1. Create an empty table that every DSN can write to. The tool never creates or drops tables. Without `cleanup`, the table must accept the same rows again in every iteration.
2. Add the `insert` section. `samples/insert.sample.json` has a complete example.
3. `OdbcBench probe --config bench.json` describes the table, prepares the INSERT and binds the parameters without executing anything. It shows the statement, the effective parameter array size, whether autocommit can be turned off, and the value each column receives for row 1.
4. `OdbcBench insert --config bench.json` runs the benchmark. The report lands in `results/run-insert-<date>-<time>.md`.

| Field | Default | Meaning |
|---|---|---|
| `insert.table` | | Target table, written as it appears in SQL, for example `dbo.orders` or `"sales"."orders"`. |
| `dsns[].insertTable` | | Per-DSN table, for naming or quoting differences. `--table` overrides both. |
| `insert.columns` | every column | Columns to fill, written as they appear in SQL. By default every column the driver describes, except auto-increment columns and types the generator has no values for (XML, intervals, UDTs). Those are listed in the report. |
| `insert.rows` | `100000` | Rows per iteration. |
| `insert.cleanup` | `none` | `delete` or `truncate` empties the table before every iteration, outside the timers. `none` leaves the rows, so the table grows. |
| `insert.transaction` | `perIteration` | `perIteration`: autocommit off and one commit at the end. `perBatch`: a commit after every SQLExecute. `autocommit`: the driver commits every SQLExecute by itself. |
| `insert.verify` | `true` | Runs `SELECT COUNT(*)` on the table before and after every iteration, outside the timers. |
| `insert.valueLength` | `32` | Characters or bytes for text and binary values, capped at the column size. |

**Values.** Every value is a function of the column type and the row number, so every DSN, batch size and iteration sends the same rows. Integers are the row number, wrapped to the column's range. Text starts with the zero-padded row number. Floats are exact quarters. Dates, times and timestamps count days or seconds from 2000-01-01. GUIDs are version 4 shaped. Decimals fit the column's precision and scale. No value is NULL.

**Per iteration.** Only these calls are timed:

| Timer | ODBC calls |
|---|---|
| prepare + bind | `SQLPrepareW` and `SQLBindParameter`, with column-wise parameter arrays. Only when the statement is built: the dry run, the first warmup, or every iteration with `reuseStatement: false`. |
| execute | Every `SQLExecute`, one per parameter array. It includes the `SQLSetStmtAttrW(SQL_ATTR_PARAMSET_SIZE)` for a shorter last array. |
| first batch | The first `SQLExecute`. |
| commit | Every `SQLEndTran(SQL_COMMIT)`. |
| total | prepare/bind + execute + commit |
| generate | Filling the parameter arrays. It happens between the timed calls, is reported separately, and is not part of the total. |

`SQL_ATTR_PARAMSET_SIZE` is read back, because drivers may lower it. A driver without parameter arrays gets one row per `SQLExecute`, and the report shows an effective batch size of 1. Rejected rows are counted from `SQL_ATTR_PARAM_STATUS_PTR`. `SQLRowCount` is not used: drivers without `SQL_PARC_BATCH`, such as psqlODBC, report only the last row of each array. The insert loop allocates no managed memory; the report shows this per iteration.

Numeric, integer, float, bit, date, timestamp and GUID columns bind as their C types. Text, decimals, times with fractional seconds and `datetimeoffset` bind as `SQL_C_WCHAR`. Binary binds as `SQL_C_BINARY`, even in `bindMode: wchar`. When a driver rejects a binding (07006, HY003, HY004 or HYC00), that column falls back to text and the report says so. psqlODBC describes `boolean` as text; those columns still receive `0` and `1`.

**Validation.** Every series first writes the first rows (`validation.rows`) in a dry run through the real batch path. With `cleanup` set, each DSN then reads its rows back with `SQLFetch` and `SQLGetData`, independently of the insert path. They are compared with the values that were sent, using the normalisation of the SELECT validation, and the report shows the sent values as the `(sent)` column. With `cleanup: none`, the table also holds rows from other iterations, so nothing is read back.

**Consistency.** Every driver must accept every row, and in every counted iteration the table must gain exactly the rows the driver accepted. A driver that reports success without writing the whole array fails this check, and the exit code is 1.

**Before comparing drivers.** Driver logging can dominate the times. psqlODBC DSNs sometimes have `Debug` or `CommLog` on; `extraAttributes: "Debug=0;CommLog=0"` turns them off for the run. Commit costs depend on the server's durability settings, so compare DSNs that point at the same server.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `IM002` data source name not found | The DSN is missing, or on Windows it exists only in the 32-bit ODBC administrator. The tool's hint tells you which. Create it in the 64-bit administrator, or on Linux and macOS add a section to `~/.odbc.ini` or the system `odbc.ini` (`odbcinst -j` shows which files unixODBC reads; `ODBCINI` and `ODBCSYSINI` move them). |
| `IM014` architecture mismatch | Windows: the DSN points at a 32-bit driver. |
| `01000` Can't open lib (unixODBC) | The driver named in the DSN is not in `odbcinst.ini`, or its `Driver=` library does not exist or has missing dependencies (check with `ldd`). |
| `ODBC Driver Manager not found` | Linux and macOS: unixODBC is not installed or not on the library path. Install it or set `ODBCBENCH_DRIVER_MANAGER`. |
| Row array size changed | The driver capped `SQL_ATTR_ROW_ARRAY_SIZE` (SQLSTATE 01S02). The report shows the effective value. |
| Truncated values | The driver reports column sizes smaller than the data. Raise `longColumnThresholdBytes` or use `bindMode: wchar`. |
| `SQL_ATTR_QUERY_TIMEOUT ... not accepted` | Informational: some drivers do not support query timeouts. |
| Insert: `SQL_ATTR_PARAMSET_SIZE ... not accepted` | The driver has no parameter arrays, for example the Access driver. Every SQLExecute sends one row, and the batch sizes then measure the same thing. |
| Insert: duplicate key (SQLSTATE 23xxx) | The table has a unique constraint and `cleanup` is `none`, so the second iteration inserts the same keys again. Use `delete` or `truncate`. |
| Insert: autocommit could not be turned off | The driver has no transactions. Every SQLExecute commits by itself and no commit time is reported. |
| Insert: a column cannot be written | Computed columns and `rowversion` accept no values. List the other columns in `insert.columns`. |

## Project layout

| Path | Contents |
|---|---|
| `src/OdbcBench/Odbc` | P/Invoke declarations and thin wrappers for the environment, connection and statement handles, type mapping, Driver Manager loading, and DSN hints (registry on Windows, `odbc.ini` on unixODBC). |
| `src/OdbcBench/Fetch` | The block-fetch reader, binding plan, column buffers, and chunked `SQLGetData`. |
| `src/OdbcBench/Insert` | The batch insert writer, target table description, parameter mapping, and value generator. |
| `src/OdbcBench/Initialize` | Portable dataset catalog, PostgreSQL/SQL Server/Oracle DDL dialects, and initializer. |
| `src/OdbcBench/Validation` | Sample reader, value normalisation and comparison. |
| `src/OdbcBench/Bench` | Runner, statistics, analysis and system information. |
| `src/OdbcBench/Report` | Result model, JSON writer and Markdown writer. |
| `tests/OdbcBench.Tests` | Unit tests and the golden reports (`Golden/report.md`, `Golden/insert-report.md`). After an intended report change, regenerate them with `UPDATE_GOLDEN=1`. |
