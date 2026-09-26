# Build status

What exists today, measured against the [roadmap](../plan/03-roadmap.md). Updated with each milestone.

## Phase 0 — Foundations

| Item | Status |
|---|---|
| .NET solution skeleton, projects per the architecture | ✅ Done |
| CI (GitHub Actions, Linux + Windows) | ✅ Done (`.github/workflows/ci.yml`) |
| Legacy test corpus | 🟡 Started: real sample tables from the MIT-licensed `dbfread` project. Real customer apps still needed |
| VFP 9 oracle rig | ⛔ Blocked on D6 (needs a Windows VM with a licensed VFP 9 SP2). Behavior guesses are marked `TODO(oracle)` |
| Text format specs, report schema | 🟡 Migration report schema implemented. Form/report text formats come with their designers |

## Phase 1 — Data engine, language core, Command Window, data import

| Item | Status |
|---|---|
| Core value semantics (types, EMPTY vs NULL, SET EXACT/ANSI, dates, rounding) | ✅ Done |
| Tables and all VFP field types, autoincrement, nullable fields | ✅ Done |
| Indexes: expression keys, FOR, descending, unique, candidate/primary | ✅ Done ([ADR 0004](../adr/0004-index-keys-as-maintained-columns.md)) |
| Work areas, data sessions, GO/SKIP/SEEK/LOCATE/SCAN, filters, SET DELETED, relations | ✅ Done |
| Buffering modes 1–5, TABLEUPDATE/TABLEREVERT/OLDVAL/CURVAL/GETFLDSTATE, conflict detection | ✅ Done |
| ACID transactions (nested, across tables and databases) | ✅ Done |
| Databases: stored procedures, field rules, record rules, defaults, triggers | ✅ Done (RI builder code generation: not yet) |
| SELECT-SQL v1: joins, grouping, aggregates, HAVING, DISTINCT, UNION, TOP, subqueries, INTO | ✅ Done ([ADR 0003](../adr/0003-in-memory-sql-executor-first.md)) |
| INSERT / UPDATE / DELETE SQL, CREATE/ALTER TABLE, CREATE CURSOR | ✅ Done |
| Language: procedures, scoping, arrays, TRY/CATCH, ON ERROR, macros, TEXT/TEXTMERGE, preprocessor | ✅ Done ([ADR 0002](../adr/0002-tree-walking-interpreter-first.md)) |
| OOP: DEFINE CLASS, inheritance, ADD OBJECT, DODEFAULT, access/assign, Collection | ✅ Done (in Phase 1 scope; Phase 2 finishes BINDEVENT and visibility enforcement) |
| Built-in functions | 🟡 [263 implemented](functions.md), prioritized by frequency in typical code |
| Command Window | 🟡 Console Command Window (`joepro`). The IDE Command Window arrives with the Avalonia shell |
| Browse window | 🟡 `BROWSE` prints records in the console. The editable grid arrives with the IDE shell |
| Basic form runtime | ⛔ Not started. The object model and base-class property catalog exist; the Avalonia renderer does not |
| Minimal IDE shell (docking, themes) | ⛔ Not started |
| Data import: DBF/FPT/CDX/DBC → Joe Pro, migration report (JSON + HTML) | ✅ Done. DBC property decoding (captions, rules, triggers, relations) is still pending (judgment call J2) |
| PRG analyzer (macros, DLL declarations, COM, @SAY/GET, DO FORM, FLLs, unsupported SYS()) | ✅ Started (Phase 2 item, brought forward) |
| Cross-process locking | ⛔ In-process locks only; cross-process locks arrive with the Data Server (Phase 6) |
| Performance benchmarks | ⛔ Not started |

## Tests

Run `dotnet test`. The suite covers core semantics, legacy file reading (including real VFP files),
the data engine, the parser, the runtime (language, commands, SQL) and migration.
