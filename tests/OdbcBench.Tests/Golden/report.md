# ODBC driver performance comparison

**Outcome:** completed with problems (exit code 1) · **Validation:** FAIL · **Consistency:** row counts match

| Item | Value |
| --- | --- |
| Run | 2026-09-25 18:00:00 UTC, 2 min 30 s |
| Host | BENCHBOX, 64-bit process |
| Tool | OdbcBench 0.1.0 |
| Configuration | `C:\bench\bench.json` (SHA-256 0123456789ab) |
| Command line | `OdbcBench run --config bench.json` |
| Iterations | 1 warmup + 5 measured per series; statistics use measured iterations only |
| Order | interleaved: each round runs every series once, alternating direction between rounds |
| Connections | one connection per DSN, reused by every iteration |
| Statements | one statement per series, reused (describe and bind once) |
| Binding | native C types; long columns read row by row with SQLGetData (block fetch off for that query) |
| Total time | SQLExecDirectW + describe/bind (only when the statement is built) + every SQLFetchScroll and value read + SQLFreeStmt(SQL_CLOSE) |
| Baseline | legacy |

## Summary

- **Block size 1,000:** flight is fastest with a p50 total of 606.00 ms: 0.75× the time of the baseline legacy (808.00 ms), or 1.33× its speed.
- **Block size 1:** flight is fastest with a p50 total of 1,919.0 ms: 0.79× the time of the baseline legacy (2,424.0 ms), or 1.26× its speed.
- Every successful iteration of every DSN returned 100,000 rows.
- Validation of the first 10 rows: **FAIL** (1 failure, 0 warnings).
- broken could not be benchmarked: connect failed [IM002].
- flight @1: partial, 1 failed measured iteration. See [Warnings and errors](#warnings-and-errors).

p50 of the total time per iteration (execute, read every row and value, close). Ratios are relative to the baseline; below 1.00× is faster.

| DSN | Driver | Block 1,000 | Block 1 |
| --- | --- | ---: | ---: |
| legacy (baseline) | sqlsrv32.dll 10.00.19041 | 808.00 ms | 2,424.0 ms |
| flight | arrow-flight-sql-odbc.dll 0.9.1 | 606.00 ms (0.75×) | 1,919.0 ms (0.79×) |
| broken | – | connect failed | connect failed |

## Environment

| Item | Value |
| --- | --- |
| OS | Microsoft Windows 10.0.19045 (X64) |
| CPU | Test CPU (8 logical processors) |
| Memory | 32.0 GB |
| Power plan | High performance |
| Runtime | .NET 8.0.8 X64; tiered compilation off; high-resolution timer yes |
| ODBC | Driver Manager 03.81.19041.0000; ODBC 3.80 behaviour; connection pooling off |

### DSNs and drivers

| DSN | Source | Driver | Version | ODBC | Unicode | SQLGetData extensions |
| --- | --- | --- | --- | --- | --- | --- |
| legacy | DSN=SalesLegacy | sqlsrv32.dll | 10.00.19041 | 03.52 | yes | ANY\_COLUMN\|ANY\_ORDER |
| flight | Driver={Arrow Flight SQL ODBC Driver};Host=h;token=\*\*\* | arrow-flight-sql-odbc.dll | 0.9.1 | 03.80 | yes | ANY\_COLUMN |
| broken | DSN=Nope | – | – | – | – | – |

| DSN | DBMS | DBMS version | Server | First connect ms | Connect p50 ms (n) | Status |
| --- | --- | --- | --- | ---: | ---: | --- |
| legacy | Microsoft SQL Server | 16.00.1000 | db01 | 40.50 | 12.00 (3) | connected |
| flight | Dremio | 25.0.0 | h | 95.25 | 30.00 (3) | connected |
| broken | – | – | – | – | – | connect failed [IM002] |

First connect includes loading the driver DLL. Connect p50 is the median of extra connections opened and closed afterwards (DNS, TCP, TLS and authentication).

- **broken**: SQLDriverConnectW failed (SQL\_ERROR): [IM002] (0) Data source name not found
  - Hint: DSN 'Nope' is not defined in any ODBC administrator.

## Query

```sql
SELECT id, name, amount FROM sales ORDER BY id
```

## Validation (first 10 rows)

**FAIL**: 10 rows compared across 2 DSNs against legacy. Each DSN's rows were read with SQLFetch and SQLGetData as text, independently of the block-fetch path. Before comparing: trailing spaces trimmed; NULL differs from the empty string; decimals compared by value; floats with a relative tolerance of 1E-09; dates and times parsed, with fractional seconds compared at the coarser precision of the two.

| # | Column | Compared as | legacy | flight |
| ---: | --- | --- | --- | --- |
| 1 | id | integer | INTEGER(10) int | BIGINT(19) BIGINT |
| 2 | name | text | VARCHAR(40) varchar | WVARCHAR(65536) VARCHAR |

### Differing values

One line per differing row: column, then the value on legacy → the value on the other DSN.

| Row | DSN | Differences |
| ---: | --- | --- |
| 4 | flight | name: `Ana ` → `Ana\|` |

- INFO column 'id': INTEGER(10) int on legacy vs BIGINT(19) BIGINT on flight

## Column bindings

How each column was described by the driver (SQLDescribeColW) and bound for block fetch (SQLBindCol target type × element size). Columns marked SQLGetData are read in chunks after each fetch.

| # | Column | legacy | flight |
| ---: | --- | --- | --- |
| 1 | id | INTEGER(10) → SQL\_C\_SLONG x 4 B | INTEGER(10) → SQL\_C\_SBIGINT x 8 B |
| 2 | name | VARCHAR(40) → SQL\_C\_WCHAR x 82 B | VARCHAR(40) → SQL\_C\_WCHAR x 131074 B |

## Results: block size 1,000

Requested row array size 1,000 (SQL_ATTR_ROW_ARRAY_SIZE). Effective row array size: legacy 1,000, flight 500.

### Total time per iteration (ms)

| DSN | Status | OK | Min | p50 | Mean | p95 | Max | Std dev | CV | Outliers | vs baseline |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| legacy (baseline) | ok | 5/5 | 792.00 | 808.00 | 808.00 | 824.00 | 824.00 | 12.65 | 1.6% | 0 | baseline |
| flight | ok | 5/5 | 594.00 | 606.00 | 606.00 | 618.00 | 618.00 | 9.487 | 1.6% | 0 | 0.75× (-25.0%) |

### Where the time goes (p50, ms)

| DSN | Execute | First batch | Fetch | Close |
| --- | ---: | ---: | ---: | ---: |
| legacy | 202.00 | 8.080 | 597.92 | 8.080 |
| flight | 151.50 | 6.060 | 448.44 | 6.060 |

Execute is SQLExecDirectW. First batch is the first SQLFetchScroll call, which returns the first row array. Fetch covers every SQLFetchScroll call plus reading every value, first batch included. Close is SQLFreeStmt(SQL_CLOSE).

### Throughput and resources (medians per iteration)

| DSN | Rows | Data MB | Rows/s | MB/s | CPU | Truncated | GCs | Fetch-loop alloc B | Effective block |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| legacy | 100,000 | 3.00 | 123,762 | 3.71 | 90.0% | 0 | 0 | 0 | 1,000 |
| flight | 100,000 | 3.00 | 165,017 | 4.95 | 90.0% | 0 | 0 | 0 | 500 |

Data MB counts the bytes of every non-NULL value as delivered in its C type (10^6 bytes), not network bytes. Rows/s and MB/s divide by the total time. CPU is process user + kernel time over wall time; above 100% means the driver used several threads. Windows accounts CPU time in ticks of about 15.6 ms, so CPU figures are coarse for short iterations.

Notes:

- flight: driver changed SQL\_ATTR\_ROW\_ARRAY\_SIZE from 1000 to 500

## Results: block size 1

Requested row array size 1 (SQL_ATTR_ROW_ARRAY_SIZE). Effective row array size: legacy 1, flight 1.

### Total time per iteration (ms)

| DSN | Status | OK | Min | p50 | Mean | p95 | Max | Std dev | CV | Outliers | vs baseline |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| legacy (baseline) | ok | 5/5 | 2,376.0 | 2,424.0 | 2,424.0 | 2,472.0 | 2,472.0 | 37.95 | 1.6% | 0 | baseline |
| flight | partial | 4/5 | 1,900.0 | 1,919.0 | 1,928.5 | 1,957.0 | 1,957.0 | 24.53 | 1.3% | 0 | 0.79× (-20.8%) |

### Where the time goes (p50, ms)

| DSN | Execute | First batch | Fetch | Close |
| --- | ---: | ---: | ---: | ---: |
| legacy | 606.00 | 24.24 | 1,793.8 | 24.24 |
| flight | 479.75 | 19.19 | 1,420.1 | 19.19 |

Execute is SQLExecDirectW. First batch is the first SQLFetchScroll call, which returns the first row array. Fetch covers every SQLFetchScroll call plus reading every value, first batch included. Close is SQLFreeStmt(SQL_CLOSE).

### Throughput and resources (medians per iteration)

| DSN | Rows | Data MB | Rows/s | MB/s | CPU | Truncated | GCs | Fetch-loop alloc B | Effective block |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| legacy | 100,000 | 3.00 | 41,254 | 1.24 | 90.0% | 0 | 0 | 0 | 1 |
| flight | 100,000 | 3.00 | 51,600 | 1.55 | 90.0% | 0 | 0 | 0 | 1 |

Data MB counts the bytes of every non-NULL value as delivered in its C type (10^6 bytes), not network bytes. Rows/s and MB/s divide by the total time. CPU is process user + kernel time over wall time; above 100% means the driver used several threads. Windows accounts CPU time in ticks of about 15.6 ms, so CPU figures are coarse for short iterations.

## Data consistency

- **Row counts:** every successful iteration returned 100,000 rows.
- **Stability:** every series produced the same checksum in every iteration.
- **Block sizes:** each DSN produced the same checksum at every block size.
- **Across DSNs (informational):** checksums differ. They only match when drivers return byte-identical values in the same C types; a different binding (for example a timestamp delivered as text) or text formatting changes the checksum even for equal data. The validation section compares values properly.

| Series | Rows | Checksum | Stable |
| --- | ---: | --- | --- |
| legacy @1000 | 100,000 | `00000000000000aa` | yes |
| flight @1000 | 100,000 | `00000000000000bb` | yes |
| legacy @1 | 100,000 | `00000000000000aa` | yes |
| flight @1 | 100,000 | `00000000000000bb` | yes |

### Harness overhead

After the benchmark, the value-reading loop was replayed over the bound buffers without calling ODBC. The estimate is rows × bound columns × time per value; it is included in every DSN's fetch time in the same way.

| Series | Bound columns | ns per value | Estimated ms per iteration | Share of p50 total |
| --- | ---: | ---: | ---: | ---: |
| legacy @1000 | 2 | 4.50 | 0.900 | 0.1% |

## Warnings and errors

| Phase | Severity | DSN | Series | Iteration | SQLSTATE | Message |
| --- | --- | --- | --- | --- | --- | --- |
| connect | **error** | broken |   |   | IM002 | SQLDriverConnectW failed (SQL\_ERROR): [IM002] (0) Data source name not found |
| benchmark | **error** | flight | flight @1 | 3 | 08S01 | SQLFetchScroll failed (SQL\_ERROR): [08S01] (0) Communication link failure |

## Appendix: every iteration

Times in ms. Warmup iterations are listed but never enter the statistics.

<details>
<summary>legacy @1000: 6 iterations</summary>

| Phase | # | Total | Execute | First batch | Fetch | Close | Rows | CPU | Checksum | Result |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| warmup | 0 | 1,200.0 | 300.00 | 12.00 | 888.00 | 12.00 | 100,000 | 1,080.0 | `00000000000000aa` | ok |
| measured | 1 | 800.00 | 200.00 | 8.000 | 592.00 | 8.000 | 100,000 | 720.00 | `00000000000000aa` | ok |
| measured | 2 | 816.00 | 204.00 | 8.160 | 603.84 | 8.160 | 100,000 | 734.40 | `00000000000000aa` | ok |
| measured | 3 | 792.00 | 198.00 | 7.920 | 586.08 | 7.920 | 100,000 | 712.80 | `00000000000000aa` | ok |
| measured | 4 | 808.00 | 202.00 | 8.080 | 597.92 | 8.080 | 100,000 | 727.20 | `00000000000000aa` | ok |
| measured | 5 | 824.00 | 206.00 | 8.240 | 609.76 | 8.240 | 100,000 | 741.60 | `00000000000000aa` | ok |

</details>

<details>
<summary>flight @1000: 6 iterations</summary>

| Phase | # | Total | Execute | First batch | Fetch | Close | Rows | CPU | Checksum | Result |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| warmup | 0 | 900.00 | 225.00 | 9.000 | 666.00 | 9.000 | 100,000 | 810.00 | `00000000000000bb` | ok |
| measured | 1 | 600.00 | 150.00 | 6.000 | 444.00 | 6.000 | 100,000 | 540.00 | `00000000000000bb` | ok |
| measured | 2 | 612.00 | 153.00 | 6.120 | 452.88 | 6.120 | 100,000 | 550.80 | `00000000000000bb` | ok |
| measured | 3 | 594.00 | 148.50 | 5.940 | 439.56 | 5.940 | 100,000 | 534.60 | `00000000000000bb` | ok |
| measured | 4 | 606.00 | 151.50 | 6.060 | 448.44 | 6.060 | 100,000 | 545.40 | `00000000000000bb` | ok |
| measured | 5 | 618.00 | 154.50 | 6.180 | 457.32 | 6.180 | 100,000 | 556.20 | `00000000000000bb` | ok |

</details>

<details>
<summary>legacy @1: 6 iterations</summary>

| Phase | # | Total | Execute | First batch | Fetch | Close | Rows | CPU | Checksum | Result |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| warmup | 0 | 3,600.0 | 900.00 | 36.00 | 2,664.0 | 36.00 | 100,000 | 3,240.0 | `00000000000000aa` | ok |
| measured | 1 | 2,400.0 | 600.00 | 24.00 | 1,776.0 | 24.00 | 100,000 | 2,160.0 | `00000000000000aa` | ok |
| measured | 2 | 2,448.0 | 612.00 | 24.48 | 1,811.5 | 24.48 | 100,000 | 2,203.2 | `00000000000000aa` | ok |
| measured | 3 | 2,376.0 | 594.00 | 23.76 | 1,758.2 | 23.76 | 100,000 | 2,138.4 | `00000000000000aa` | ok |
| measured | 4 | 2,424.0 | 606.00 | 24.24 | 1,793.8 | 24.24 | 100,000 | 2,181.6 | `00000000000000aa` | ok |
| measured | 5 | 2,472.0 | 618.00 | 24.72 | 1,829.3 | 24.72 | 100,000 | 2,224.8 | `00000000000000aa` | ok |

</details>

<details>
<summary>flight @1: 6 iterations</summary>

| Phase | # | Total | Execute | First batch | Fetch | Close | Rows | CPU | Checksum | Result |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| warmup | 0 | 2,850.0 | 712.50 | 28.50 | 2,109.0 | 28.50 | 100,000 | 2,565.0 | `00000000000000bb` | ok |
| measured | 1 | 1,900.0 | 475.00 | 19.00 | 1,406.0 | 19.00 | 100,000 | 1,710.0 | `00000000000000bb` | ok |
| measured | 2 | 1,938.0 | 484.50 | 19.38 | 1,434.1 | 19.38 | 100,000 | 1,744.2 | `00000000000000bb` | ok |
| measured | 3 | 1,881.0 | 470.25 | 18.81 | 1,391.9 | 18.81 | 100,000 | 1,692.9 |   | **failed** [08S01] SQLFetchScroll failed (SQL\_ERROR): [08S01] (0) Communication link failure |
| measured | 4 | 1,919.0 | 479.75 | 19.19 | 1,420.1 | 19.19 | 100,000 | 1,727.1 | `00000000000000bb` | ok |
| measured | 5 | 1,957.0 | 489.25 | 19.57 | 1,448.2 | 19.57 | 100,000 | 1,761.3 | `00000000000000bb` | ok |

</details>

