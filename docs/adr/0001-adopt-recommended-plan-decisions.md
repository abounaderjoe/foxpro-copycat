# ADR 0001 — Adopt the recommended plan decisions

- Status: Accepted
- Date: 2026-09-26

## Context

The plan ([docs/plan](../plan/README.md)) listed seven decisions (D1–D7) for the product owner.
The owner replied "start do all", which we read as approval of the plan with its recommended options.

## Decision

| # | Decision | Adopted option |
|---|---|---|
| D1 | UI framework | Avalonia UI (IDE work starts in the IDE-shell milestone; nothing in the runtime depends on it) |
| D2 | Language posture | VFP-compatible superset; compatibility mode is the only mode implemented so far |
| D3 | Text formats | `DEFINE CLASS` text for forms/classes, strict YAML for reports, menus, projects and schemas (implemented with the designers) |
| D4 | Sync defaults | Legacy data is the system of record until cutover; installing triggers into legacy DBCs is opt-in |
| D5 | Team/timeline | Roadmap estimates unchanged |
| D6 | Oracle prerequisite | Still required. Until a Windows VM with licensed VFP 9 SP2 is available, behavior questions are marked `TODO(oracle)` in the code |
| D7 | Phase ordering | Data import moved into Phase 1; each converter ships with its designer |

## Consequences

Anything marked `TODO(oracle)` is a best guess that the conformance suite must confirm or correct.
Search the code for that marker to see the list.
