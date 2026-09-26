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
| OOP: DEFINE CLASS, inheritance, ADD OBJECT, DODEFAULT, access/assign, Collection | ✅ Done |
| Built-in functions | 🟡 [282 implemented](functions.md), prioritized by frequency in typical code |
| Command Window | ✅ In the IDE (Enter runs the line or selection; blocks collect until complete; earlier lines can be re-run), plus the console `joepro` |
| Browse window | ✅ Editable grid in the IDE: edits write back with REPLACE (rules and triggers apply), append, Ctrl+T toggles deleted. Loads up to 100,000 rows; paging is a later milestone |
| Basic form runtime | ✅ Forms from code (`DEFINE CLASS … AS Form`) or `.jpform` files (`DO FORM`) render with Avalonia: Label, TextBox, EditBox, CommandButton, CheckBox, OptionGroup, CommandGroup, ComboBox/ListBox, Spinner, Shape, Line, Image, Container, PageFrame, Grid, Timer; ControlSource binding; VFP event order; private data sessions |
| Minimal IDE shell | ✅ Resizable panes (Data Session, tabbed documents, Command Window), light/dark/system themes, high DPI, code editor with FoxPro highlighting (MODIFY COMMAND, Ctrl+E/F5 run), command palette (Ctrl+Shift+P), status bar. Floating/dockable panels are not done yet |
| Data import: DBF/FPT/CDX/DBC → Joe Pro, migration report (JSON + HTML) | ✅ Done. DBC property decoding (captions, rules, triggers, relations) is still pending (judgment call J2) |
| PRG analyzer (macros, DLL declarations, COM, @SAY/GET, DO FORM, FLLs, unsupported SYS()) | ✅ Started (Phase 2 item, brought forward) |
| Cross-process locking | ⛔ In-process locks only; cross-process locks arrive with the Data Server (Phase 6) |
| Performance benchmarks | ✅ [Benchmark suite and results](benchmarks.md) ([ADR 0005](../adr/0005-storage-performance-model.md)) |
| Crash safety | ✅ Tests kill a real `joepro` process mid-write: uncommitted transactions vanish, committed rows and indexes stay intact |

## Phase 2 — Full language, OOP, modern editor, debugger

| Item | Status |
|---|---|
| OOP: PROTECTED/HIDDEN enforced (error 1734), BINDEVENT/UNBINDEVENTS/AEVENTS (before/after, method-call flag) | ✅ Done |
| COM/.NET bridge | 🟡 `CREATEOBJECT("net:Type")` for any .NET type everywhere; COM ProgIDs on Windows through late binding (untested here: no Windows machine). `GETOBJECT` not supported |
| SQL v2: hash joins, filter pushdown, `UPDATE … FROM`, `DELETE … FROM` | ✅ Done. `ENGINEBEHAVIOR` result typing still needs the oracle |
| Local views: `CREATE SQL VIEW`, parameters (`?name`, `?(expr)`), `USE … NODATA`, `REQUERY()`, `DBSETPROP`/`CURSORSETPROP`, updates to base tables with WhereType conflict detection | ✅ Done |
| SQL pass-through: `SQLSTRINGCONNECT`/`SQLCONNECT`/`SQLEXEC`/`SQLPREPARE`/`SQLMORERESULTS`/`SQLCOMMIT`/`SQLROLLBACK`/`SQLTABLES`/`SQLCOLUMNS`/`SQLGETPROP`/`SQLSETPROP`, named connections | ✅ Done over ADO.NET: built-in SQLite provider, ODBC for `Driver=`/`DSN=` strings, and a provider registry for others |
| Remote views (`CREATE SQL VIEW … REMOTE CONNECTION`) with updates | ✅ Done |
| CursorAdapter: `CursorFill`/`CursorRefresh`/`CursorAttach`/`CursorDetach`, CursorSchema, fill/refresh events, updates via its current properties | ✅ NATIVE and ODBC data sources. ADO and XML need COM: not supported |
| Language server: diagnostics, completion, hover, go to definition, symbols, **find references, rename, signature help** | ✅ Done (LSP + IDE). Semantic highlighting: not yet |
| Debugger (engine, IDE, DAP): breakpoints (conditional, break-when-true, break-on-change), stepping, **set next statement** (within the running blocks of the current procedure), call stack, locals, watch, debug output, event tracking, coverage, `SET STEP ON`/`SUSPEND` | ✅ Done |
| VS Code extension (LSP + DAP client, grammar) | 🟡 Written; not yet tried in VS Code |
| Migration slice 2: PRG analyzer findings in the report | 🟡 Analyzer and report section done; automatic fixes not yet |
| Performance: record navigation, batching, prepared statements | ✅ 10–60× faster navigation and bulk commands ([benchmarks](benchmarks.md)) |
| Bytecode VM | ⏸ Deferred until benchmarks show interpreter overhead dominating ([ADR 0002 amendment](../adr/0002-tree-walking-interpreter-first.md)) |

## Phase 3 — Form Designer + Class Designer

| Item | Status |
|---|---|
| Canonical `.jpform`/`.jpclass` format (`JoePro.Documents`): sorted classes and properties, one property per line, members in z-order; re-saving is byte-identical, one property change is a one-line diff | ✅ Done |
| SCX → `.jpform`, VCX → `.jpclass`: data environment, implicit children (pages, columns, buttons), member code in containers, custom properties/methods/arrays, PROTECTED/HIDDEN, cross-library inheritance (`OF lib.jpclass`), #INCLUDE files; value mapping (colors → `RGB()`, paths, `=expressions`, multi-line values) with findings | ✅ Done. Checked on 51 real forms and class libraries from public VFP projects: all convert, all compile, all re-save with zero diffs. FoxUnit's libraries are in the test corpus and compared with FoxBin2Prg's text versions |
| Legacy files run directly: `DO FORM x.scx`, `SET CLASSLIB TO x.vcx`, `NEWOBJECT`, `ADD OBJECT … OF lib` (converted copies preferred when present) | ✅ Done |
| Form DataEnvironment: cursors and relations open before Load, close after Unload (AutoOpenTables/AutoCloseTables, BeforeOpenTables/AfterCloseTables, BufferModeOverride, Order, Filter, NoDataOnLoad) | ✅ Done |
| `IMPORT FOXPRO` converts forms and class libraries, mirrors the folder layout, copies programs/headers/pictures, analyzes method code | ✅ Done. A whole real application (GoFish) imports with no failures |
| Compatibility: every one of 299 real programs in the corpus compiles (about 40 parser gaps fixed: leading-dot calls, `obj.&macro`, `m.` in declarations, multiple CATCH, `CAST()`, comment continuation, textmerge lines, …) | ✅ Done |
| Form Designer: the surface is the real form built in design mode (property values apply, no event code runs) and drawn by the same controls as at run time; toolbox (click or drag to size), snap to grid, select/band-select/Shift-click, move and 8-handle resize with the mouse or arrow keys, align/same size/center, z-order, cut/copy/paste, drop into containers and the active page | ✅ Done |
| Property sheet: categories and search, stored values in bold, VFP typing rules (text is a value of the property's type, `=expr` is an expression, colors as `r,g,b`), reset to default, rename; Methods tab with the events of the base class and the methods with code | ✅ Done |
| Code pane for method code, data environment (add table → drag fields as label + bound control, check box for logical, edit box for memo; table → grid), unlimited undo/redo as one step per gesture, save to canonical `.jpform`, Run | ✅ Done |
| `CREATE FORM`, `MODIFY FORM` (legacy `.scx` opens converted; saving writes `.jpform`), `CREATE FORM … AS class FROM lib`, File → New Form, Form menu | ✅ Done |
| Class Designer: `MODIFY CLASS`/`CREATE CLASS … OF lib AS parent [FROM lib]`; container, control and non-visual classes; New Property (arrays, Access/Assign methods), New Method, Edit Property/Method (own and inherited members), Class Info (description, icons, OLE public); saving writes only that class into the library and the running session reloads it | ✅ Done |
| Class Browser: hierarchy of a library, members (own and inherited, with descriptions), class code; new, rename (references in the library follow), remove, copy to another library, redefine the parent, create an instance, export code; legacy `.vcx` read-only until saved as `.jpclass` | ✅ Done |
| Class library commands: `CREATE CLASSLIB`, `ADD CLASS … TO`, `RENAME CLASS`, `REMOVE CLASS`, `AVCXCLASSES()`; class and member descriptions and icons come over from VCX files | ✅ Done |
| Controls: InputMask and Format (masks applied while typing, R/!/K/Z/$ codes, display format when not focused), `When` returning .F. refuses the focus, grid DynamicBackColor/ForeColor/FontBold/FontItalic, Toolbar (with Separator, Dock), FormSet, Hyperlink | ✅ Done |
| OLE/ActiveX hosting | ⛔ Not planned for the cross-platform runtime; forms keep the object and report it in the migration report |

## Screenshots

Rendered headlessly by `tools/JoePro.Screenshots` (`dotnet run --project tools/JoePro.Screenshots -- docs/images`).

| IDE (light) | IDE (dark) |
|---|---|
| ![IDE light](../images/ide-light.png) | ![IDE dark](../images/ide-dark.png) |

| Code editor | A form from a .jpform file |
|---|---|
| ![Editor](../images/ide-editor.png) | ![Form](../images/form-customer.png) |

| Form Designer | Class Browser |
|---|---|
| ![Form Designer](../images/form-designer.png) | ![Class Browser](../images/class-browser.png) |

## Known gaps in the UI (Phase 1)

- The form runtime covers the common controls. Not supported: OLE/ActiveX controls. Toolbars float in their own
  window; `Dock()` records the position but does not dock the toolbar into another window yet.
- `READ EVENTS` and modal forms use nested dispatcher loops. They have only been exercised headlessly, not yet on a real desktop.
- The IDE has not been launched on a real Windows/macOS/Linux desktop in this environment; it is exercised by headless UI tests and rendered screenshots.

## Tests

Run `dotnet test`. `JoePro.Tests` covers core semantics, legacy file reading (including real VFP files),
the data engine, the parser, the runtime (language, commands, SQL) and migration. `JoePro.Ui.Tests` runs
the form runtime and the IDE shell headlessly (Avalonia headless platform).
