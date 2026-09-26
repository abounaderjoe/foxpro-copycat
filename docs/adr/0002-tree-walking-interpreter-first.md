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

## Amendment (Phase 2): the debugger runs on the tree walker; the VM waits for evidence

- Date: 2026-09-26

The debugger shipped on the tree walker. `Interpreter.Exec` calls `Debugger.OnStatement` before
each statement, which gives breakpoints, conditions, break-on-change, stepping, call stacks, locals
and coverage without a compiler. The DAP server and the IDE use the same engine.

The [benchmarks](../status/benchmarks.md) show where time actually goes in typical FoxPro work.
Tight interpreter loops run at about 3.5 million statements per second. Data work is dominated
by storage: SQLite commits, row decoding and index lookups. Record navigation and multi-row commands
were made 10–60× faster by changes in the data layer ([ADR 0005](0005-storage-performance-model.md)).
No interpreter change was needed for that.

Decision: the bytecode VM is **deferred**. It is no longer tied to the debugger. It will be built
when a benchmark or a corpus application shows interpreter overhead dominating real work. The
likely first step is caching name resolution in the tree, not a new execution model. The parser,
AST and function library stay shared, so a VM can still replace only the execution loop.

