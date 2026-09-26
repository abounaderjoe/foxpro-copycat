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
| Interpreter loop | FOR 1..1,000,000 with arithmetic and IF | 288.1 | 3,471,564 |
| String building | 100,000 concatenations + STRTRAN/UPPER | 86.3 | 1,158,981 |
| INSERT INTO (table) | 100,000 rows into an indexed table | 3558.1 | 28,105 |
| APPEND BLANK + REPLACE | 100,000 rows | 1095.6 | 91,275 |
| SCAN FOR + SUM | scan 100,000 rows, sum where cust < 500 | 198.5 | 503,702 |
| SEEK (indexed) | 50,000 random SEEKs | 504.9 | 99,037 |
| LOCATE (unindexed) | 20 LOCATEs, each scanning the whole table | 3893.5 | 5 |
| SELECT GROUP BY | group 100,000 rows into 1,000 groups | 290.5 | 344,180 |
| SELECT JOIN (hash) | 100,000 orders joined to 1,000 customers | 1074.4 | 93,072 |
| UPDATE-SQL | update 100,000 rows | 1468.3 | 68,107 |
| Object method calls | 100,000 method calls | 107.4 | 930,722 |

Machine: Ubuntu 24.04.4 LTS, 4 logical CPUs, .NET 10.0.12, X64.

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
