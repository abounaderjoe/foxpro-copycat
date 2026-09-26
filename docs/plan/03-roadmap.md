# 03 — Phased Roadmap

> Status: **Draft for review**. Durations are rough and assume a **core team of about 5
> engineers**. They are there to show relative size, not to commit to dates. Every phase
> ends with a demo and a review gate before the next one starts.

## Changes from the order suggested in the brief

1. **DBF/FPT/DBC *import* moves up to Phase 1.** Real legacy data is the best test data
   for the engine, and "open my existing tables in Joe Pro" is the earliest point where
   the product is useful to a FoxPro user. The rest of migration (forms, reports, menus,
   projects) ships **with the designer it depends on**, not in a final migration phase.
   A converter needs the target document format to exist, and building them together
   keeps the format honest.
2. **The Class Designer ships with the Form Designer.** In VFP they are the same surface.
3. **Full language + debugger + editor get their own phase (Phase 2)**, before the
   designers. Designers generate code-behind, and without a good editor and debugger
   every later phase is slower to build and to test.
4. **Two-way sync and the multi-user server come late (Phase 6).** They depend on a
   stable engine and change journal, and they carry the most data-safety risk, so they
   should build on proven parts.

---

## Phase 0 — Foundations (≈ 6–8 weeks)

**Scope**
- Repository, .NET solution skeleton per the architecture layout, CI on Windows runners,
  coding standards, ADRs (architecture decision records).
- **Oracle rig:** a Windows VM with VFP 9 SP2, scripted runs, golden-output format,
  first 500 expression/function conformance cases.
- **Legacy test corpus** assembled (sample apps covering tables, DBCs, forms, reports,
  menus, projects).
- Specs: canonical text formats v0 (form/class/report/menu/project/schema), coverage
  matrix format, migration report JSON schema v0.
- Spikes (throwaway): SQLite expression indexes with custom functions for VFP keys;
  keyset `SKIP` performance over 1M rows; Avalonia design surface with adorners;
  AvaloniaEdit + LSP hello world.

**Done when**
- CI is green on an empty-but-real solution. The oracle rig produces golden files from a
  script with one command.
- Spike results written up. They confirm (or overturn) the SQLite and Avalonia decisions
  **before** Phase 1 commits to them.
- Format specs and report schema reviewed and approved.

---

## Phase 1 — Data engine core, language core, Command Window, Browse, basic form runtime, data import (≈ 5–6 months)

**Scope**
- `JoePro.Core`: value types, VFP comparison semantics (`SET EXACT/ANSI/NEAR`), empty
  vs. NULL, dates/datetimes, numeric and currency rules, collations.
- `JoePro.Data` on SQLite: tables, all field types, structural indexes (expression,
  FOR, unique/candidate/primary, descending), work areas, data sessions, navigation
  (`USE/SELECT/GO/SKIP/SEEK/LOCATE/SCAN`), `REPLACE/APPEND/INSERT/DELETE/RECALL/PACK/ZAP`,
  filters, relations, `SET DELETED/ORDER/KEY`, cursors, **transactions**, embedded-mode
  locking, buffering modes 1–5 with `TABLEUPDATE/TABLEREVERT/OLDVAL/CURVAL`.
- DBC model: long names, rules, defaults, triggers, RI, persistent relations, stored
  procedures (they execute once the language core exists).
- `JoePro.Sql` v1: SELECT with joins (all four kinds), WHERE, GROUP BY/HAVING, ORDER BY,
  DISTINCT, TOP, UNION, `INTO CURSOR/TABLE/ARRAY`; INSERT/UPDATE/DELETE; CREATE TABLE/
  CURSOR, ALTER TABLE. Rushmore-equivalent index matching.
- Language core: lexer/parser/binder/VM for procedural code: variables and scoping
  (PUBLIC/PRIVATE/LOCAL), arrays, control flow, procedures/functions/parameters, macro
  substitution, `EVALUATE`, preprocessor, `TRY/CATCH`, `ON ERROR`, the top ~200 built-in
  functions by frequency in the corpus.
- **Command Window** (history, re-execute, multi-line), output panel (`?`, `??`,
  `_SCREEN` equivalent), **Browse** window (virtualized, editable, buffer- and
  lock-aware), **Data Session** window.
- Basic OOP + form runtime: `DEFINE CLASS`, `CREATEOBJECT`, Form, Label, TextBox,
  EditBox, CommandButton, CheckBox, OptionGroup, ComboBox, ListBox, Spinner, Container,
  Shape, Line, Image, Timer; data binding (`ControlSource`); `READ EVENTS`; the
  When/Valid/focus cycle; event order per the oracle.
- Minimal IDE shell: docking, themes (dark/light), high-DPI, open/save text files.
- **Migration slice 1:** DBF/FPT/CDX/IDX/DBC readers → import into Joe Pro databases,
  with migration report sections for data.

**Done when**
- The expression/function, SQL and navigation conformance suites pass against the oracle
  for everything in scope (targets: ≥ 95% of cases, and every failure documented in the
  coverage matrix).
- A real legacy DBC from the corpus imports with verified row counts, hashes and
  index orders, and the migration report lists every property.
- A user can type in the Command Window: `OPEN DATABASE`, `USE customer ORDER name`,
  `BROWSE`, `SELECT … INTO CURSOR`, `REPLACE`, and run a PRG that defines and shows a
  data-bound form written by hand in code.
- Performance: `SKIP` through 1M indexed rows and a 3-table join on 100k rows are within
  an agreed factor of VFP (benchmarks recorded).
- Crash test: killing the process mid-transaction never corrupts a database.

---

## Phase 2 — Full language, OOP, modern editor, debugger (≈ 4–5 months)

**Scope**
- Complete OOP: visibility, `DODEFAULT/NODEFAULT`, access/assign, `BINDEVENT`,
  `ADDPROPERTY`, `NEWOBJECT`, `SET PROCEDURE/CLASSLIB`, Collection, Empty, Exception,
  Session; COM bridge (`CREATEOBJECT` of ProgIDs, `GETOBJECT`).
- Remaining high-use built-ins (target: all functions and commands used anywhere in the
  corpus), `TEXT/TEXTMERGE`, low-level file functions, string, array and date libraries.
- SQL v2: subqueries in all VFP 9 positions, derived tables, `UPDATE … FROM`,
  `ENGINEBEHAVIOR 70/80/90`, result naming and typing fully oracle-matched. Local views
  with update criteria. **SQL pass-through / remote views / CursorAdapter** over ODBC.
- **Language server:** semantic highlighting, completion (members, fields of open and
  schema tables, built-ins with signature help), inline diagnostics, go-to-definition,
  find references, rename, hover docs. Compat/modern mode switch and auto-fixes.
- **Debugger (DAP):** line/conditional breakpoints, break-when-true, break-on-change,
  step in/over/out, set next statement, call stack, locals, watch, debug output,
  **event tracking**, **coverage logging**, `SUSPEND/RESUME/SET STEP ON`.
- **Migration slice 2:** PRG analyzer + rule set (macros, shadowing, byte-length, DLLs,
  FLLs, platform `SYS()` calls, …), auto-fixes, code section of the report. DBC stored
  procedures and triggers verified by execution.

**Done when**
- Every PRG in the corpus compiles in compat mode. Findings are all in the report, none
  are silent.
- A corpus app's **non-UI business layer** (procedures, classes, SQL) runs and produces
  the same outputs as VFP on scripted test inputs.
- A developer can set breakpoints in a PRG and a form method, inspect locals and watches,
  and step through with the event-tracking log visible, all from the IDE and from VS Code.
- The editor shows completions within 50 ms on a 5k-line file.

---

## Phase 3 — Form Designer + Class Designer (≈ 5–6 months)

**Scope**
- Remaining controls: **Grid** (columns, headers, dynamic properties, virtualization,
  in-grid editing), PageFrame/Page, CommandGroup, OptionButton, Toolbar, FormSet,
  Hyperlink, Separator, OLE control hosting (Windows).
- **Form Designer:** design surface using live runtime controls, drag-and-drop from the
  toolbox, selection/alignment/size/spacing tools, snap-to-grid, tab order editor,
  **property sheet** (categories, search, favorites, inheritance indicators, reset to
  default, expression builder), inline **code-behind editor** per method with full LSP,
  **Data Environment designer** (drag a table or field to create bound controls),
  builders (Grid, Combo, AutoFormat), unlimited undo/redo, anchoring and layout preview
  at different DPIs.
- **Class Designer:** visual subclassing, new/edit property and method, protected/hidden,
  inherited members displayed, edit parent method, redefine parent, **Class Browser**.
- Round-trip-exact `.jpform`/`.jpclass` serialization (the designer never produces a
  noisy diff).
- **Migration slice 3:** SCX/VCX → `.jpform`/`.jpclass`, including cross-library
  inheritance, the property mapping table and the forms section of the report.

**Done when**
- A new user can build a master/detail data-entry form (header fields + orders grid +
  page frame) with no code except validation, run it, and commit it. A one-property
  change produces a one-line diff.
- ≥ 90% of corpus forms convert as `Converted` or `ConvertedWithChanges`, open in the
  designer, run, and pass their event-trace oracle tests. The rest are itemized in the
  report.
- Opening a form in the designer and re-saving it with no edits produces a zero-byte diff.

---

## Phase 4 — Report & Label Designer (≈ 4 months)

**Scope**
- Report engine: bands, groups (with reprint header, new page, keep together),
  multiple detail bands, stretch/float/remove-blank-line, calculated fields and
  variables with reset scopes, Print When, multi-column, page X of Y, images.
- Report Designer: band editor, toolbox, field expression builder, format masks,
  grouping and sorting dialogs, data environment, rulers, snap, undo/redo, **live
  preview panel** as a modern addition.
- Label Designer with presets.
- Output: preview (zoom, page navigation, search), print (printer selection and paper),
  **PDF export**, and later HTML/XLSX/image. `REPORT FORM … PREVIEW | TO PRINTER |
  TO FILE`. `ReportListener`-compatible extension API.
- **Migration slice 4:** FRX/LBX → `.jpreport`/`.jplabel`, font mapping, `DEVMODE`
  summary, reports section with a **visual diff** (VFP-rendered page image vs. Joe Pro
  page image) for each corpus report.

**Done when**
- Every corpus report converts and renders. Page-layout oracle comparison is within
  tolerance (object positions within 1/100 inch; same page count; same totals) for
  ≥ 90% of them, and the rest are itemized.
- PDF output passes PDF/A validation and embeds fonts.
- A new user can build a grouped invoice report with subtotals and a grand total, and
  preview and export it.

---

## Phase 5 — Query/View Designer, Menu Designer, Database Designer, Project Manager, build & packages (≈ 5 months)

**Scope**
- **Query/View Designer:** diagram of tables and joins, criteria tabs, **editable SQL
  pane with two-way sync** (edits in SQL update the diagram where representable, with a
  clear "SQL-only" mode otherwise), results preview, update criteria for views, remote
  views via connections.
- **Menu Designer:** hierarchical editor, result types (command/submenu/procedure/pad),
  shortcuts, skip-for, messages, context menus, Quick Menu, runtime menus + MPR-compatible
  code export. **Toolbar designer.**
- **Database Designer:** visual schema, table designer (fields, indexes, rules,
  triggers), persistent relations, RI builder, stored procedure editor, schema-diff
  migration scripts.
- **Project Manager:** familiar tabbed categories + modern tree/search, main program,
  include/exclude, project hooks, **project-wide search** and symbol index.
- **Build:** project → self-contained Windows app (single-file EXE + runtime), versioning
  info, icons, code signing hook, optional installer (MSIX). COM server builds are
  evaluated here and may move to a later phase.
- **Packages:** manifest, lockfile, Git and folder sources, static registry v1,
  `joe add/restore/publish` commands, available in the IDE too.
- **Migration slice 5:** MNX → `.jpmenu`, PJX → `.jpproj`, `.QPR` → `.jpquery`.
  **End-to-end "migrate a whole project" wizard** producing the full report.

**Done when**
- A corpus project migrates end to end with one wizard run and **builds into a working
  EXE** that runs its main menu, forms and reports against the imported data.
- Round-trip tests: a query built visually → SQL → reopened visually is identical. A
  menu defined in the designer runs identically to its MPR-exported code.
- A class library published as a package can be consumed by a second project with
  version pinning.

---

## Phase 6 — Multi-user Data Server + two-way sync (≈ 4–5 months)

**Scope**
- **Joe Pro Data Server** (Windows service): TCP/TLS + named pipes, auth, per-database
  permissions, central lease-based lock manager, change notifications, online backups,
  integrity checks, admin UI. Client transport inside the runtime, with transparent
  switching between embedded and server mode.
- **Change journal** in the engine (if not already done in Phase 1).
- **Sync:** DBC trigger installer + `_joesync_log` capture, snapshot-diff capture for
  free tables, 32-bit legacy adapter over VFP OLE DB, key mapping, loop prevention,
  field-level merge, authority rules, **conflict log + review queue UI**, sync dashboard
  (lag, throughput, errors), and a cutover tool that flips authority and removes legacy
  triggers.
- Optional PostgreSQL kernel (can slip to Phase 7 depending on customer demand).

**Done when**
- A soak test with 50 simulated concurrent users (mixed reads, buffered edits, row locks)
  runs 24 hours with no corruption, no orphaned locks after killed clients, and p95
  commit latency within budget.
- Sync soak test: legacy VFP 9 app instances and Joe Pro edit the same data concurrently
  for 24 hours. Final states converge, every conflict is in the log with both values,
  and no DBF/CDX corruption is found (verified by VFP `VALIDATE DATABASE` and a reindex
  comparison).
- The cutover procedure is rehearsed end to end on a corpus app and documented.

---

## Phase 7 — Hardening, parity sweep, launch (≈ 3–4 months, then ongoing)

**Scope**
- Close gaps in the coverage matrix (the long tail of commands, functions and `SET`
  options). Legacy `@SAY/GET` screen compatibility layer (decide here, based on corpus
  demand).
- Performance: IL tier for hot code paths, query planner improvements, startup time.
- Accessibility (UI Automation, screen readers, keyboard-only audit), localization
  framework.
- Documentation: a "for FoxPro developers" guide mapping every VFP concept to Joe Pro,
  a language reference generated from the built-in registry, migration playbook.
- Installer, auto-update, telemetry (opt-in), crash reporting.
- Beta program with 3–5 real legacy applications. Their migration reports drive the
  final fixes.

**Done when**
- The coverage matrix shows 100% of corpus-used features supported, and ≥ 95% of all
  documented VFP 9 commands and functions supported or explicitly listed as
  unsupported with a reason.
- Beta apps run in production on Joe Pro (at least in a parallel run with sync).
- A 1.0 release candidate has no open data-integrity bugs.

---

## Summary timeline (rough, about 5 engineers)

| Phase | Theme | Rough size |
|---|---|---|
| 0 | Foundations, oracle, specs, spikes | 1.5–2 months |
| 1 | Data engine, language core, Command Window, Browse, basic forms, data import | 5–6 months |
| 2 | Full language, OOP, editor/LSP, debugger, PRG analyzer | 4–5 months |
| 3 | Form + Class Designer, grid, SCX/VCX conversion | 5–6 months |
| 4 | Report/Label Designer + engine + PDF, FRX/LBX conversion | 4 months |
| 5 | Query/Menu/Database Designers, Project Manager, build, packages, project migration | 5 months |
| 6 | Data Server, two-way sync | 4–5 months |
| 7 | Hardening, parity sweep, beta, 1.0 | 3–4 months |
| | **Total** | **≈ 2.5–3 years** to 1.0 |

Some phases overlap once the core APIs stabilize: Phase 4 can start during Phase 3, and
the Phase 6 server work can start after Phase 2.

## Top risks

| Risk | Impact | Mitigation |
|---|---|---|
| Semantic fidelity (string comparison, SQL typing, event order) | Legacy apps behave subtly differently | Oracle rig from day one. Conformance suites gate every phase. |
| Grid control complexity | Most VFP forms depend on it | A dedicated milestone in Phase 3. Oracle event traces. |
| ActiveX / Win32 DLL dependencies in legacy apps | Can't be auto-converted | COM hosting on Windows; clear `NeedsReview` reporting; track corpus frequency early. |
| Live DBF writes during sync | Data corruption in production | Write only via VFP OLE DB (32-bit adapter). Native writer off by default. Long soak tests. |
| Scope size | Schedule slip | Phase gates; the coverage matrix is ordered by corpus usage frequency, not by the manual. |
| Access to real legacy apps | Blind spots | Recruit corpus partners in Phase 0. Anonymization tooling. |
| VFP 9 licenses / VM for the oracle | No ground truth | Secure in Phase 0 (prerequisite). |
