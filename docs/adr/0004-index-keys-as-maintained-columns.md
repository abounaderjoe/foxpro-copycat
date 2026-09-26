# ADR 0004 — Store index keys as engine-maintained key columns

- Status: Accepted
- Date: 2026-09-26

## Context

The architecture proposed SQLite *expression indexes* over registered deterministic functions (for
example `CREATE INDEX … ON t(jp_upper(c_name))`) to mirror CDX tags.

## Decision

Each index tag gets a hidden BLOB column (`k_<tag>`) holding order-preserving key bytes
(`JoePro.Core.KeyEncoder`). The engine computes the key with the FoxPro runtime on every insert and
update, and SQLite indexes the column. A FOR clause writes NULL for excluded rows and becomes a partial
index. Candidate and primary keys become UNIQUE indexes.

## Reasons

- Any FoxPro key expression works, including ones that call user-defined functions. VFP allows
  those, and legacy apps use them.
- Database files stay readable by standard SQLite tools, because no custom functions need to be
  registered to open or query them.
- Seeks, partial matches (`SET EXACT OFF`) and descending order become simple byte-range queries.

## Consequences

- Extra storage per tag, and key computation cost on writes.
- Changing a function used in an index expression requires `REINDEX`, the same as in VFP.
- Non-MACHINE collations currently fold case only. Real collation weights await the oracle
  (judgment call J1).
