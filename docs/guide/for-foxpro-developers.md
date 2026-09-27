# Joe Pro for FoxPro developers

This guide maps what you know from Visual FoxPro 9 to Joe Pro. Most of it is the same: the
language, the commands, the work areas, the forms and the designers. This page is about the
differences.

- For the status of every VFP 9 command and function, see the [coverage matrix](../reference/coverage.md).
- For the functions and their syntax, see the [language reference](../reference/language.md).

## In one minute

- Your code runs as it is. PRG files are not converted. `DO`, `SET PROCEDURE`, `SET CLASSLIB`,
  `DO FORM`, `REPORT FORM` and the rest work the same way.
- Tables live in SQLite. A database (DBC) becomes a single `.jpdb` file, and a free table
  becomes a `.jpt` file. Record numbers, the deleted flag, index tags, `SET ORDER`, `SEEK`,
  buffering, `TABLEUPDATE()` and transactions behave as in VFP.
- Forms, class libraries, reports, menus, queries and projects are text files, so you can diff,
  merge and review them. Legacy `.scx`, `.vcx`, `.frx`, `.lbx`, `.mnx`, `.qpr` and `.pjx` files
  still open: they are converted when loaded, and the converted text is saved when you save.
- Joe Pro runs on Windows, macOS and Linux.
- Strings are Unicode. There are no code pages to manage.

## Files

| Visual FoxPro | Joe Pro | Notes |
|---|---|---|
| `.dbc` / `.dct` / `.dcx` | `.jpdb` | One SQLite file holds the tables, indexes, views, connections, relations, stored procedures and properties. |
| `.dbf` / `.fpt` / `.cdx` in a database | inside the `.jpdb` | |
| free `.dbf` / `.fpt` / `.cdx` | `.jpt` | `USE legacy.dbf` still opens a FoxPro table read-only; `IMPORT` converts it. |
| `.prg` | `.prg` | Unchanged. No `.fxp` files: programs are compiled when loaded. |
| `.scx` / `.sct` | `.jpform` | Canonical text: properties, then methods as code. |
| `.vcx` / `.vct` | `.jpclass` | One text file per library. |
| `.frx` / `.lbx` | `.jpreport` / `.jplabel` | A strict YAML subset ([format](../reference/report-format.md)). |
| `.mnx` / `.mpr` | `.jpmenu` | `DO menu.mpr` still works; the menu generates code at run time. |
| `.qpr` | `.jpquery` | Opens in the Query Designer; `DO query.jpquery` runs it. |
| `.pjx` | `.jpproj` | Files, main program, version, dependencies. |
| `.app` / `.exe` | `.jpapp` + launcher scripts | See [Building](#building-and-running). |
| `.mem` | `.mem` (JSON) | `SAVE TO` / `RESTORE FROM` work; VFP's binary `.mem` files are not read. |

## Data

- **Engine.** Tables are SQLite tables with extra columns for the record number, the deleted
  flag and each index key. `GO`, `SKIP`, `RECNO()`, `DELETED()`, `PACK` and `RECALL` work as in
  VFP. Rushmore-style optimization comes from SQLite indexes on the key columns.
- **Indexes.** `INDEX ON … TAG` creates a tag in the table's structural index. There are no
  separate `.idx`/`.cdx` files, so `SET INDEX TO` and `COPY INDEXES` have nothing to do.
- **Databases.** Long names, captions, defaults, field and record rules, triggers, referential
  integrity (cascade, restrict, ignore), stored procedures, local and remote views, named
  connections and `DBGETPROP()`/`DBSETPROP()` all work. RI is enforced by the engine, not by
  generated code.
- **Buffering.** Row and table buffering, `OLDVAL()`, `CURVAL()`, `GETFLDSTATE()`,
  `SETFLDSTATE()`, `TABLEUPDATE()` and `TABLEREVERT()` behave as in VFP.
- **SQL.** `SELECT-SQL` runs on the engine with VFP semantics (including `SET ANSI`, `SET EXACT`
  and `SET ENGINEBEHAVIOR`). `SQLEXEC()` and remote views go to ODBC sources or, with
  `Provider=sqlite`, to SQLite files.
- **Code pages.** Text is Unicode. `CPCONVERT()` and `OEMTOANSI()` return text unchanged, and
  the double-byte functions (`LEFTC()`, `ATCC()`, …) are the same as their single-byte forms.
- **Change journal.** `DBSETPROP(DBC(), "DATABASE", "Journal", .T.)` records every insert,
  update and delete with old and new values. Two-way sync uses it.

## Multi-user

In VFP, users share tables through a file share. Joe Pro's `.jpdb` files are SQLite databases,
and SQLite's locking is not reliable over network file shares. Opening a database directly is
right for one machine; for several users, run the **Data Server** (`joepro-server`):

```foxpro
OPEN DATABASE joepro://ann@dataserver/sales
```

The code does not change: `RLOCK()`, `FLOCK()`, buffering conflicts and transactions work
against the server. To keep `OPEN DATABASE sales` unchanged, put the address in
`joepro-data.json`. See [data server](../reference/data-server.md).

## Language

The language is VFP's: procedures, parameters by value or reference, `PRIVATE`/`LOCAL`/`PUBLIC`,
arrays, macros (`&`), name expressions, `EVALUATE()`, `EXECSCRIPT()`, `TEXT … ENDTEXT`,
`TRY … CATCH`, `DEFINE CLASS`, `DODEFAULT()`, `BINDEVENT()`, `#DEFINE`/`#INCLUDE` (including
`foxpro.h`) and the rest.

The differences:

| In VFP | In Joe Pro |
|---|---|
| `DECLARE … IN win32api` | Not available (Windows DLLs). Most uses have a built-in function or a .NET equivalent. |
| COM: `CREATEOBJECT("Excel.Application")` | Windows COM is not available yet. .NET types are: `CREATEOBJECT("net:System.Text.StringBuilder")`. |
| `@ … SAY/GET`, `READ`, `DEFINE WINDOW` | Not supported. Use forms. The runtime tells you when it skips one of these. |
| `SET PRINTER`, `???`, `EJECT` | Not supported. Print through `REPORT FORM`. |
| General (OLE) fields | Not supported. Store files in Blob fields. |
| `.fxp`, `COMPILE` | Programs compile when they load; `COMPILE` checks the syntax. |

`ON KEY LABEL`, `ON SHUTDOWN`, `ON ESCAPE`, `KEYBOARD`, `INKEY()`, `ACCEPT` and `INPUT` work.
`ON KEY LABEL` hotkeys fire in running forms.

## Forms and controls

- Forms run on Avalonia and look native on each platform. The VFP base classes, their
  properties, events and methods, the data environment, control sources, grids, page frames,
  toolbars, form sets and modal forms with `TO` return values are supported.
- `DO FORM customer` finds `customer.jpform`, or converts `customer.scx` in memory.
- `THISFORM`, `THIS`, `Parent`, `Controls`, `Objects`, `SetAll()`, `AddObject()`,
  `NEWOBJECT()` and access/assign methods behave as in VFP.
- `_SCREEN` is the main window. `MODIFY WINDOW SCREEN TITLE` and `ZOOM WINDOW SCREEN` work.

## Reports

`REPORT FORM` and `LABEL FORM` print, preview, or write to a file. With `TO FILE`, the extension
picks the format: `.pdf`, `.html`, `.xml`, `.png` or `.txt`. Report variables, groups, data
grouping, calculated fields, `PRINT WHEN` and the report listener events work.

## The IDE

| VFP | Joe Pro |
|---|---|
| Command Window | Command Window (Ctrl+F2), with history and completion |
| Project Manager | Project Manager (`MODIFY PROJECT`), with packages |
| Form, Class, Report, Label, Menu, Query, View, Database and Table Designers | The same designers; the forms and classes can also be edited as code |
| Class Browser | Class Browser |
| Debugger (Trace, Watch, Locals, Call Stack) | Debugger panel: breakpoints, stepping, locals, watches, call stack |
| Data Session window | Data Session panel |
| IntelliSense | Completion, hover, signature help, go to definition, find references, rename |
| Code References | Find references and rename |

Editors outside the IDE can use the language server (`joepro lsp`) and the debug adapter
(`joepro dap`).

## Building and running

```foxpro
BUILD PROJECT sales                  && checks every file
BUILD APP sales FROM sales           && sales.jpapp
BUILD EXE dist FROM sales            && dist/ with dist.jpapp and launch scripts
```

A `.jpapp` holds the compiled application and its files. It runs with `joepro-app sales.jpapp`
or `joepro run sales.jpapp`. `BUILD EXE` writes `.cmd` and `.sh` launchers next to it and copies
the excluded data files. There are no DLL or COM server builds (`BUILD DLL` produces a `.jpapp`).

## Packages

Shared libraries (the VFPX kind) are packages: `joepro add <name>` adds one to the project,
`joepro restore` downloads what the project needs, and `joepro publish` shares yours. Packages
join `SET PATH` and ship with builds. See [packages](../reference/packages.md).

## Moving an application

1. **Import.** In the IDE, choose File > Import FoxPro Application, or run
   `joepro import path/to/app --to converted`. Databases, tables, forms, class libraries,
   reports, menus, queries and the project are converted. Programs are copied.
2. **Read the migration report.** It lists what was converted, what changed and what needs a
   look, such as DLL declarations, COM objects and `@ SAY/GET` screens.
3. **Run it side by side.** Two-way sync keeps the legacy DBF data and the Joe Pro database in
   step while both applications are used, with a conflict log and a review queue. When you are
   ready, `joepro sync cutover` makes Joe Pro the system of record. See [sync](../reference/sync.md).

## Things to know

- Paths: FoxPro code often builds Windows paths with backslashes. Joe Pro accepts both `\` and
  `/`, and file lookups ignore case on case-sensitive file systems.
- `SYS()` returns sensible values for the common codes. Codes that only describe VFP internals
  return empty strings.
- `VERSION()` returns Joe Pro's version, and `VERSION(4)`/`VERSION(5)` return VFP 9 values so
  version checks in existing code pass.
- Settings that have no meaning here, such as `SET RESOURCE` and `SET TYPEAHEAD`, are accepted
  and ignored.
