# ADR 0003 — Execute SELECT-SQL in memory first; push work down to SQLite later

- Status: Accepted
- Date: 2026-09-26

## Context

The architecture proposes translating the parts of a query whose semantics match SQLite into SQLite
SQL, and running the rest locally.

## Decision

The first SQL engine (`JoePro.Runtime.SqlEngine`) runs entirely in memory: it loads source rows,
joins (nested loops), filters, groups, sorts and projects using the same expression evaluator as the
rest of the runtime.

## Reasons

- It gives exact FoxPro semantics for every expression, including user-defined functions,
  `SET ANSI`/`SET EXACT` string comparison and FoxPro's NULL rules, with one code path.
- It is the reference implementation that the pushdown optimizer must match.

## Consequences

- Large joins are slow (nested loops over materialized rows). Pushdown and hash joins are planned for
  Phase 2 (SQL v2) and will be verified against this engine.
- Result column types are inferred from the data. Character columns take the longest value's width,
  while VFP uses the first row's width. This is recorded as a known deviation until the oracle
  confirms VFP's rules.
