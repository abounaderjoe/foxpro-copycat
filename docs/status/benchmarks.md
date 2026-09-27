# Benchmarks

Typical FoxPro workloads, timed end to end through the interpreter by `tools/JoePro.Benchmarks`:

```
dotnet run -c Release --project tools/JoePro.Benchmarks -- --rows 100000 --markdown results.md
```

Each workload runs once to warm up, then three times; the best time is reported. Tables are real
`.jpt` files on disk (SQLite in WAL mode); `APPEND BLANK + REPLACE` uses a cursor (in memory).

## Results (100,000 rows)

| Benchmark | Workload | Best of 3 (ms) | Operations/s |
|---|---|---:|---:|
| Interpreter loop | FOR 1..1,000,000 with arithmetic and IF | 213.2 | 4,691,398 |
| String building | 100,000 concatenations + STRTRAN/UPPER | 65.3 | 1,532,379 |
| INSERT INTO (table) | 100,000 rows into an indexed table | 3042.2 | 32,871 |
| APPEND BLANK + REPLACE | 100,000 rows | 914.3 | 109,374 |
| SCAN FOR + SUM | scan 100,000 rows, sum where cust < 500 | 117.3 | 852,293 |
| SEEK (indexed) | 50,000 random SEEKs | 449.9 | 111,140 |
| LOCATE (unindexed) | 20 LOCATEs, each scanning the whole table | 164.9 | 121 |
| SELECT GROUP BY | group 100,000 rows into 1,000 groups | 290.7 | 344,036 |
| SELECT JOIN (hash) | 100,000 orders joined to 1,000 customers | 954.1 | 104,813 |
| UPDATE-SQL | update 100,000 rows | 1481.2 | 67,511 |
| Object method calls | 100,000 method calls | 94.5 | 1,058,747 |

Machine: Ubuntu 24.04.4 LTS, 4 logical CPUs, .NET 10.0.12, X64.

## FOR conditions pushed down to SQL (Phase 7)

`LOCATE`, `CONTINUE`, `SCAN FOR` and the scoped commands (`COUNT`, `SUM`, `REPLACE`, `DELETE`, `COPY TO`, … `FOR`)
turn the parts of the FOR condition that compare a field with a constant into a SQL condition, and jump from
candidate to candidate instead of evaluating every record. This does the job Rushmore does in VFP, without
needing an index. The SQL is only a prefilter: every record it lets through still has the whole FOR condition
evaluated, so results cannot change, and anything that cannot be expressed that way (function calls on fields,
SET COLLATE other than MACHINE, character comparisons other than `=`) is left to the record-by-record check.
It is skipped while buffered changes are pending or when a WHILE clause is given. `PrefilterTests` checks 32
conditions under five combinations of SET EXACT, SET DELETED, SET ORDER and SET FILTER against
record-by-record evaluation.

| Benchmark | Before | After | Speed-up |
|---|---:|---:|---:|
| LOCATE (unindexed), 20 × 100,000 rows | 3279.7 ms | 164.9 ms | ≈ 20× |
| SCAN FOR + SUM (half the rows match) | 208.7 ms | 117.3 ms | ≈ 1.8× |

Single-row `INSERT` was profiled too. With the table's two index tags, about 80% of the time is SQLite itself
(the B-tree inserts and, outside a transaction, the commit). On this VM, a bare SQLite autocommit insert costs
28–37 µs, which is the same order as the Joe Pro figure. Inside `BEGIN TRANSACTION` an insert costs about 20 µs,
and roughly half of that is the Microsoft.Data.Sqlite layer. Replacing that layer is a possible later gain,
not done yet.

## Before and after the storage work

The first run exposed record navigation as the bottleneck ([ADR 0005](../adr/0005-storage-performance-model.md)).
Same machine, 2,000-row run, time per operation:

| Operation | Before | After | Speed-up |
|---|---:|---:|---:|
| `SCAN FOR` (per record) | 107 µs | 4.3 µs | ≈ 25× |
| Unindexed `LOCATE` (per record visited) | 114 µs | 1.9 µs | ≈ 60× |
| `SEEK` | 145 µs | 14 µs | ≈ 10× |
| `UPDATE-SQL` (per row) | 170 µs | 14 µs | ≈ 12× |
| `INSERT INTO` a disk table (per row) | 114 µs | 72 µs | ≈ 1.6× |

At 100,000 rows the per-record figures are lower still (2 µs per `SCAN` record, 10 µs per `SEEK`), because
batches grow and the JIT has warmed up.

## Reading the numbers

- **Single-row writes pay for durability.** Each `INSERT`/`APPEND`/`REPLACE` outside a transaction is a
  SQLite commit, about 50 µs on this machine's disk. Wrapping a loop in `BEGIN TRANSACTION … END TRANSACTION`
  turns it into one commit. Multi-row commands (`REPLACE ALL`, `UPDATE-SQL`, `APPEND FROM`, …) already commit
  once. In exchange, a crash never corrupts a table or index (see `CrashSafetyTests`).
- **The interpreter is not the bottleneck** in data work. Tight loops run at 3.5 million statements per
  second, so the bytecode VM is deferred ([ADR 0002](../adr/0002-tree-walking-interpreter-first.md)).
- **SQL materialization** (writing result rows into a cursor) dominates large `SELECT … INTO CURSOR` results,
  at about 10 µs per result row.

## Comparison with VFP 9

Not measured yet. Side-by-side numbers need the VFP 9 oracle machine (decision D6). Until then these
results serve as a regression baseline: a change that makes any row noticeably slower needs a reason.
