# ADR 0005 — Storage performance model: prepared statements, a scan cache, one commit per command

- Status: Accepted
- Date: 2026-09-26

## Context

The first benchmark run exposed the cost of mapping FoxPro's record-at-a-time model onto SQLite
(ADR 0004). Every `SKIP` ran a new, unprepared query. That query fetched a 64-row batch and used
only one row, so `SCAN` and `LOCATE` cost about 110 µs per record and `SEEK` about 145 µs. Every
row written by `REPLACE ALL` or `UPDATE-SQL` was its own SQLite commit, about 50 µs each on the test
machine.

## Decision

1. **Prepared statements.** `Store.Prepared` keeps one `SqliteCommand` per statement text for hot
   paths: row reads, index scans, inserts, updates and savepoints.
2. **Scan cache.** Each table remembers the last batch a scan fetched, together with the index keys.
   A `SKIP` that continues from a cached row is served from memory. Batches grow from 1 row (`GO TOP`
   and `SEEK` rarely need more) to 16, then 256. The cache is checked against two things:
   - a write counter shared by every data session in the process that opens the same file, so writes
     from any session invalidate it exactly;
   - SQLite's `PRAGMA data_version`, for commits by other processes. This is checked at most every
     250 ms, similar in spirit to VFP's `SET REFRESH`.

   A single-row update through the same table patches the cache instead of dropping it, unless the
   update moves the row within the cached index order.
3. **One commit per multi-row command.** `REPLACE`/`DELETE`/`RECALL` with a scope, `UPDATE`/`DELETE`
   SQL, `INSERT … SELECT`, `APPEND FROM` and `SELECT … INTO TABLE` run inside `Store.Batch`. Rows
   written before an error are still committed, which matches VFP. Each command is not made atomic,
   so VFP programs that continue after an error keep their partial results.
4. **SQLite assigns record numbers.** `INSERT … RETURNING _recno` replaces the `SELECT MAX(_recno)`
   that ran before each append. The exception is index keys that call `RECNO()`.

## Consequences

- Measured on the same machine: `SCAN` became about 25× faster, unindexed `LOCATE` about 60×, `SEEK`
  about 10×, and `UPDATE-SQL` about 12×. See [benchmarks](../status/benchmarks.md).
- Single-row writes in a loop still pay one commit each; that is the price of crash safety. Wrapping a
  loop in `BEGIN TRANSACTION … END TRANSACTION` makes it one commit. The crash tests
  (`CrashSafetyTests`) kill a real process mid-write and verify integrity.
- A reader in another *process* may see rows up to 250 ms old while it navigates. Writes by other
  data sessions in the same process are always seen immediately.
