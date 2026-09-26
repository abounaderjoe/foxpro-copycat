# Joe Pro — Plan & Architecture Proposal

Joe Pro is a modern, one-to-one functional replacement for Visual FoxPro 9: the data
engine plus the whole classic IDE, with modern UX, text-based source-control-friendly
artifacts, a safer multi-user model, and a migration and two-way sync layer for existing
FoxPro applications.

**Status: approved ("start do all"). The recommended options were adopted; see
[ADR 0001](../adr/0001-adopt-recommended-plan-decisions.md). Progress is tracked in
[docs/status](../status/README.md).**

| Document | Contents |
|---|---|
| [01 — Research summary](01-research.md) | VFP file formats, language, SQL dialect, object model, IDE tools, and the judgment calls made on undocumented behavior |
| [02 — Architecture](02-architecture.md) | Stack options and choice, system structure, data engine, language/runtime, designers, server, text formats, packages, migration, sync + conflict strategy, migration report format |
| [03 — Roadmap](03-roadmap.md) | Phases 0–7 with scope, "done" criteria, rough sizing and risks |

## Recommendations at a glance

- **Stack:** C# / .NET 10, **Avalonia UI** for the IDE and the app runtime, **SQLite** as
  the storage kernel under a FoxPro-semantics data engine, our own compiler +
  bytecode VM for the language, LSP for the editor, DAP for the debugger, PDFsharp for PDF.
- **Language:** a **compatible superset of VFP**. Legacy PRG code runs unmodified in
  compat mode; modern mode is opt-in per file.
- **Multi-user:** a **Joe Pro Data Server** service instead of shared files on a network
  drive. Embedded mode for single-user use; optional PostgreSQL backend.
- **Source control:** forms and classes as canonical `DEFINE CLASS` text; reports,
  menus, projects and schemas as canonical YAML; data kept out of Git.
- **Sync conflicts:** field-level merge, system of record wins on same-field conflicts,
  every losing value kept in a conflict log with a review queue.
- **Fidelity:** a **VFP 9 oracle rig** (a Windows VM running real VFP) generates the
  golden test outputs that every phase is gated on.

## Decisions needed from you

| # | Decision | Recommendation |
|---|---|---|
| D1 | UI framework | Avalonia (WPF if Windows-only forever and ActiveX-heavy customers) |
| D2 | Language posture | VFP-compatible superset with compat/modern modes |
| D3 | Text formats | `DEFINE CLASS` text for forms/classes; strict YAML for the rest |
| D4 | Sync defaults | Legacy is system of record until cutover; trigger install into legacy DBCs is opt-in |
| D5 | Team and timeline assumptions | Roadmap assumes about 5 engineers and roughly 2.5–3 years to 1.0 |
| D6 | Oracle prerequisite | Provide or approve a Windows VM with a licensed VFP 9 SP2, plus access to real legacy apps for the test corpus |
| D7 | Phase ordering | Data import moved into Phase 1; each converter ships with its designer (see roadmap intro) |
