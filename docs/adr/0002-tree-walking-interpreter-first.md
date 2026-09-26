# ADR 0002 — Start with a tree-walking interpreter; the bytecode VM comes later

- Status: Accepted
- Date: 2026-09-26

## Context

The architecture proposes compiling FoxPro code to bytecode for a VM, to support the debugger
and fast execution.

## Decision

The first runtime (`JoePro.Runtime.Interpreter`) executes the syntax tree directly. The parser, AST and
built-in function library are shared with the future bytecode compiler, so the VM replaces only the
execution loop.

## Reasons

- Semantics first. FoxPro's dynamic scoping, macro substitution and field/variable name resolution are
  the hard parts, and they are the same in both designs. A tree walker lets us nail them down (and
  test them) quickly.
- The debugger (Phase 2) needs statement-level hooks, which the tree walker already has
  (`Frame.Line` is updated before each statement).

## Consequences

- Execution is slower than a VM would be. That is acceptable for Phase 1 workloads. A benchmark suite
  will decide when the VM is needed.
- The VM is scheduled for Phase 2, together with the debugger.
