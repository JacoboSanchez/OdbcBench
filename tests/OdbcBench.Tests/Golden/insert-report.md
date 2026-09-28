# ODBC driver batch insert comparison

**Outcome:** completed with problems (exit code 1) · **Validation:** PASS · **Consistency:** **TABLE ROW COUNT DIFFERS**

| Item | Value |
| --- | --- |
| Run | 2026-09-28 18:00:00 UTC, 1 min 5 s |
| Host | BENCHBOX, 64-bit process |
| Tool | OdbcBench 0.1.0 |
| Configuration | `C:\bench\bench.json` (SHA-256 0123456789ab) |
| Command line | `OdbcBench insert --config bench.json` |
| Target | `bench.orders`, 10,000 rows per iteration; columns: every column that accepts a value |
| Values | generated from the row number, the same rows in every iteration; text and binary values of 32 characters or bytes, capped at the column size |
| Iterations | 1 warmup + 5 measured per series; statistics use measured iterations only |
| Order | interleaved: each round runs every series once, alternating direction between rounds |
| Connections | one connection per DSN, reused by every iteration |
| Statements | one statement per series, reused (prepare and bind once) |
| Binding | column-wise parameter arrays, native C types |
| Transactions | autocommit off, one SQLEndTran(SQL_COMMIT) at the end of the iteration |
| Cleanup | TRUNCATE TABLE before every iteration, not timed |
| Row count check | SELECT COUNT(*) on the table before and after every iteration, not timed |
| Total time | prepare/bind (only when the statement is built) + every SQLExecute + SQLEndTran(SQL_COMMIT); generating the values is not included |
| Baseline | psql |

## Summary

- **Batch size 1,000:** the baseline psql is fastest with a p50 total of 282.80 ms. Next is vendor at 3,939.0 ms, 13.93× the baseline time.
- **Batch size 1:** the baseline psql is fastest with a p50 total of 727.20 ms. Next is vendor at 3,989.5 ms, 5.49× the baseline time.
- Every successful iteration of every DSN inserted 10,000 rows.
- **The table did not gain the rows a driver reported as inserted.** See [Data consistency](#data-consistency).
- Validation of the first 10 rows: **PASS** (0 failures, 0 warnings).

p50 of the total time per iteration (prepare when the statement is built, every SQLExecute, commit). Ratios are relative to the baseline; below 1.00× is faster.

| DSN | Driver | Batch 1,000 | Batch 1 |
| --- | --- | ---: | ---: |
| psql (baseline) | PSQLODBC35W.DLL 17.00.0007 | 282.80 ms | 727.20 ms |
| vendor | vendorodbc.dll 2.1.0 | 3,939.0 ms (13.93×) | 3,989.5 ms (5.49×) |

## Environment

| Item | Value |
| --- | --- |
| OS | Microsoft Windows 10.0.26100 (X64) |
| CPU | Test CPU (16 logical processors) |
| Memory | 64.0 GB |
| Power plan | High performance |
| Runtime | .NET 8.0.31 X64; tiered compilation off; high-resolution timer yes |
| ODBC | Driver Manager 03.81.26100.0000; ODBC 3.80 behaviour; connection pooling off |

### DSNs and drivers

| DSN | Source | Driver | Version | ODBC | Unicode | SQLGetData extensions |
| --- | --- | --- | --- | --- | --- | --- |
| psql | DSN=PostgreSQL35W | PSQLODBC35W.DLL | 17.00.0007 | 03.51 | yes | ANY\_COLUMN\|ANY\_ORDER\|BLOCK\|BOUND |
| vendor | Driver={Vendor ODBC};Host=h;token=\*\*\* | vendorodbc.dll | 2.1.0 | 03.80 | yes | ANY\_COLUMN |

| DSN | DBMS | DBMS version | Server | First connect ms | Connect p50 ms (n) | Status |
| --- | --- | --- | --- | ---: | ---: | --- |
| psql | PostgreSQL | 18.0.4 | localhost | 60.50 | 38.00 (2) | connected |
| vendor | PostgreSQL | 18.0.4 | localhost | 80.25 | 44.00 (2) | connected |

First connect includes loading the driver DLL. Connect p50 is the median of extra connections opened and closed afterwards (DNS, TCP, TLS and authentication).

## Insert statement

Built from the columns the driver describes for the table. One SQLExecute sends one array of rows.

```sql
INSERT INTO bench.orders ("id", "customer", "amount") VALUES (?, ?, ?)
```

## Validation (first 10 rows)

**PASS**: every series wrote its first rows in a dry run through the batch insert path. The 10 rows each DSN wrote last were then read back through that DSN with SQLFetch and SQLGetData as text, and compared with the values that were sent, shown as (sent). 2 DSNs compared. Before comparing: trailing spaces trimmed; NULL differs from the empty string; decimals compared by value; floats with a relative tolerance of 1E-09; dates and times parsed, with fractional seconds compared at the coarser precision of the two.

| # | Column | Compared as | (sent) | psql | vendor |
| ---: | --- | --- | --- | --- | --- |
| 1 | id | integer | INTEGER(10) int4 | INTEGER(10) int4 | INTEGER(10) int4 |
| 2 | customer | text | WVARCHAR(40) varchar | WVARCHAR(40) varchar | VARCHAR(40) varchar |
| 3 | amount | decimal | NUMERIC(12,2) numeric | NUMERIC(12,2) numeric | NUMERIC(12,2) numeric |

- INFO column 'customer': WVARCHAR(40) varchar on (sent) vs VARCHAR(40) varchar on vendor

## Parameter bindings

How each target column was described by the driver (SQLDescribeColW on an empty SELECT of the table) and bound as a parameter array (SQLBindParameter C type × element size).

| # | Column | psql | vendor |
| ---: | --- | --- | --- |
| 1 | id | INTEGER(10) → SQL\_C\_SLONG x 4 B | INTEGER(10) → SQL\_C\_SLONG x 4 B |
| 2 | customer | WVARCHAR(40) → SQL\_C\_WCHAR x 66 B | WVARCHAR(40) → SQL\_C\_CHAR x 33 B |
| 3 | amount | NUMERIC(12,2) → SQL\_C\_WCHAR x 28 B | NUMERIC(12,2) → SQL\_C\_WCHAR x 28 B |

## Results: batch size 1,000

Requested parameter array size 1,000 (SQL_ATTR_PARAMSET_SIZE). Effective parameter array size: psql 1,000, vendor 1.

### Total time per iteration (ms)

| DSN | Status | OK | Min | p50 | Mean | p95 | Max | Std dev | CV | Outliers | vs baseline |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| psql (baseline) | ok | 5/5 | 277.20 | 282.80 | 282.80 | 288.40 | 288.40 | 4.427 | 1.6% | 0 | baseline |
| vendor | ok | 5/5 | 3,861.0 | 3,939.0 | 3,939.0 | 4,017.0 | 4,017.0 | 61.66 | 1.6% | 0 | 13.93× (+1292.9%) |

### Where the time goes (p50, ms)

| DSN | Execute | First batch | Commit | Generate (not in total) |
| --- | ---: | ---: | ---: | ---: |
| psql | 274.32 | 27.43 | 8.484 | 2.500 |
| vendor | 3,820.8 | 0.382 | 118.17 | 2.500 |

Execute covers every SQLExecute call, one per parameter array, first batch included. First batch is the first SQLExecute. Commit is every SQLEndTran(SQL_COMMIT); a dash means the driver commits by itself. Generate is the time the tool took to fill the parameter arrays, which happens between the timed calls.

### Throughput and resources (medians per iteration)

| DSN | Rows | Data MB | Rows/s | MB/s | CPU | Rejected rows | GCs | Insert-loop alloc B | Effective batch |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| psql | 10,000 | 0.90 | 35,361 | 3.18 | 40.0% | 0 | 0 | 0 | 1,000 |
| vendor | 10,000 | 0.90 | 2,539 | 0.23 | 40.0% | 0 | 0 | 0 | 1 |

Data MB counts the bytes of every value as sent in its C type (10^6 bytes), not network bytes. Rows/s and MB/s divide by the total time. CPU is process user + kernel time, generating the values included, over the total time; above 100% means the driver used several threads. Windows accounts CPU time in ticks of about 15.6 ms, so CPU figures are coarse for short iterations.

Notes:

- vendor: SQL\_ATTR\_PARAMSET\_SIZE=1000 not accepted by the driver: [HYC00] Optional feature not implemented; every SQLExecute sends one row

## Results: batch size 1

Requested parameter array size 1 (SQL_ATTR_PARAMSET_SIZE). Effective parameter array size: psql 1, vendor 1.

### Total time per iteration (ms)

| DSN | Status | OK | Min | p50 | Mean | p95 | Max | Std dev | CV | Outliers | vs baseline |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| psql (baseline) | ok | 5/5 | 712.80 | 727.20 | 727.20 | 741.60 | 741.60 | 11.38 | 1.6% | 0 | baseline |
| vendor | ok | 5/5 | 3,910.5 | 3,989.5 | 3,989.5 | 4,068.5 | 4,068.5 | 62.45 | 1.6% | 0 | 5.49× (+448.6%) |

### Where the time goes (p50, ms)

| DSN | Execute | First batch | Commit | Generate (not in total) |
| --- | ---: | ---: | ---: | ---: |
| psql | 705.38 | 0.071 | 21.82 | 2.500 |
| vendor | 3,869.8 | 0.387 | 119.69 | 2.500 |

Execute covers every SQLExecute call, one per parameter array, first batch included. First batch is the first SQLExecute. Commit is every SQLEndTran(SQL_COMMIT); a dash means the driver commits by itself. Generate is the time the tool took to fill the parameter arrays, which happens between the timed calls.

### Throughput and resources (medians per iteration)

| DSN | Rows | Data MB | Rows/s | MB/s | CPU | Rejected rows | GCs | Insert-loop alloc B | Effective batch |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| psql | 10,000 | 0.90 | 13,751 | 1.24 | 40.0% | 0 | 0 | 0 | 1 |
| vendor | 10,000 | 0.90 | 2,507 | 0.23 | 40.0% | 0 | 0 | 0 | 1 |

Data MB counts the bytes of every value as sent in its C type (10^6 bytes), not network bytes. Rows/s and MB/s divide by the total time. CPU is process user + kernel time, generating the values included, over the total time; above 100% means the driver used several threads. Windows accounts CPU time in ticks of about 15.6 ms, so CPU figures are coarse for short iterations.

## Data consistency

- **Row counts:** every successful iteration inserted 10,000 rows.
- **Accepted:** every driver accepted every row it was sent.
- **Table: FAIL.** The table did not gain the rows a driver reported as inserted: that driver does not write the whole parameter array, or something else writes to the table.
- **Values:** every series sent the same values in every iteration.
- **Batch sizes:** each DSN was sent the same values at every batch size.
- **Across DSNs:** all DSNs were sent byte-identical values.
- vendor @1: the table gained 9,999 rows where the driver accepted 10,000 (1 of 6 counted iterations)

| Series | Rows accepted | Rows the table gained | Checksum of the values sent | Stable |
| --- | ---: | ---: | --- | --- |
| psql @1000 | 10,000 | 10,000 | `00000000000000cc` | yes |
| vendor @1000 | 10,000 | 10,000 | `00000000000000cc` | yes |
| psql @1 | 10,000 | 10,000 | `00000000000000cc` | yes |
| vendor @1 | 10,000 | 10,000 (varies) | `00000000000000cc` | yes |

## Warnings and errors

None.

## Appendix: every iteration

Times in ms. Warmup iterations are listed but never enter the statistics.

<details>
<summary>psql @1000: 6 iterations</summary>

| Phase | # | Total | Execute | First batch | Commit | Generate | Rows | Table gained | CPU | Result |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| warmup | 0 | 392.00 | 380.24 | 38.02 | 11.76 | 2.500 | 10,000 | 10,000 | 156.80 | ok |
| measured | 1 | 280.00 | 271.60 | 27.16 | 8.400 | 2.500 | 10,000 | 10,000 | 112.00 | ok |
| measured | 2 | 285.60 | 277.03 | 27.70 | 8.568 | 2.500 | 10,000 | 10,000 | 114.24 | ok |
| measured | 3 | 277.20 | 268.88 | 26.89 | 8.316 | 2.500 | 10,000 | 10,000 | 110.88 | ok |
| measured | 4 | 282.80 | 274.32 | 27.43 | 8.484 | 2.500 | 10,000 | 10,000 | 113.12 | ok |
| measured | 5 | 288.40 | 279.75 | 27.97 | 8.652 | 2.500 | 10,000 | 10,000 | 115.36 | ok |

</details>

<details>
<summary>vendor @1000: 6 iterations</summary>

| Phase | # | Total | Execute | First batch | Commit | Generate | Rows | Table gained | CPU | Result |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| warmup | 0 | 5,460.0 | 5,296.2 | 0.530 | 163.80 | 2.500 | 10,000 | 10,000 | 2,184.0 | ok |
| measured | 1 | 3,900.0 | 3,783.0 | 0.378 | 117.00 | 2.500 | 10,000 | 10,000 | 1,560.0 | ok |
| measured | 2 | 3,978.0 | 3,858.7 | 0.386 | 119.34 | 2.500 | 10,000 | 10,000 | 1,591.2 | ok |
| measured | 3 | 3,861.0 | 3,745.2 | 0.375 | 115.83 | 2.500 | 10,000 | 10,000 | 1,544.4 | ok |
| measured | 4 | 3,939.0 | 3,820.8 | 0.382 | 118.17 | 2.500 | 10,000 | 10,000 | 1,575.6 | ok |
| measured | 5 | 4,017.0 | 3,896.5 | 0.390 | 120.51 | 2.500 | 10,000 | 10,000 | 1,606.8 | ok |

</details>

<details>
<summary>psql @1: 6 iterations</summary>

| Phase | # | Total | Execute | First batch | Commit | Generate | Rows | Table gained | CPU | Result |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| warmup | 0 | 1,008.0 | 977.76 | 0.098 | 30.24 | 2.500 | 10,000 | 10,000 | 403.20 | ok |
| measured | 1 | 720.00 | 698.40 | 0.070 | 21.60 | 2.500 | 10,000 | 10,000 | 288.00 | ok |
| measured | 2 | 734.40 | 712.37 | 0.071 | 22.03 | 2.500 | 10,000 | 10,000 | 293.76 | ok |
| measured | 3 | 712.80 | 691.42 | 0.069 | 21.38 | 2.500 | 10,000 | 10,000 | 285.12 | ok |
| measured | 4 | 727.20 | 705.38 | 0.071 | 21.82 | 2.500 | 10,000 | 10,000 | 290.88 | ok |
| measured | 5 | 741.60 | 719.35 | 0.072 | 22.25 | 2.500 | 10,000 | 10,000 | 296.64 | ok |

</details>

<details>
<summary>vendor @1: 6 iterations</summary>

| Phase | # | Total | Execute | First batch | Commit | Generate | Rows | Table gained | CPU | Result |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| warmup | 0 | 5,530.0 | 5,364.1 | 0.536 | 165.90 | 2.500 | 10,000 | 10,000 | 2,212.0 | ok |
| measured | 1 | 3,950.0 | 3,831.5 | 0.383 | 118.50 | 2.500 | 10,000 | 10,000 | 1,580.0 | ok |
| measured | 2 | 4,029.0 | 3,908.1 | 0.391 | 120.87 | 2.500 | 10,000 | 9,999 | 1,611.6 | ok |
| measured | 3 | 3,910.5 | 3,793.2 | 0.379 | 117.32 | 2.500 | 10,000 | 10,000 | 1,564.2 | ok |
| measured | 4 | 3,989.5 | 3,869.8 | 0.387 | 119.69 | 2.500 | 10,000 | 10,000 | 1,595.8 | ok |
| measured | 5 | 4,068.5 | 3,946.4 | 0.395 | 122.06 | 2.500 | 10,000 | 10,000 | 1,627.4 | ok |

</details>

