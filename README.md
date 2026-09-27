# Joe Pro

A modern, one-to-one functional replacement for Visual FoxPro 9: data engine, language, full IDE,
and a migration and sync layer for existing FoxPro applications.

- **Plan:** [docs/plan](docs/plan/README.md) (research, architecture, roadmap)
- **What works today:** [docs/status](docs/status/README.md)
- **Decisions:** [docs/adr](docs/adr)
- **Coming from Visual FoxPro:** [the guide](docs/guide/for-foxpro-developers.md), the [language reference](docs/reference/language.md) and the [coverage matrix](docs/reference/coverage.md)

## Try it

Requires the .NET 10 SDK.

```sh
dotnet build
dotnet test

# The IDE (Command Window, Browse, code editor, forms)
dotnet run --project src/JoePro.Ide

# Console Command Window
dotnet run --project src/JoePro.Cli

# Import a FoxPro table, database or whole folder and get a migration report
dotnet run --project src/JoePro.Cli -- import path/to/legacy --to converted
```

A Command Window session:

```foxpro
CREATE TABLE customer (id I AUTOINC, name C(30), balance Y)
INSERT INTO customer (name, balance) VALUES ("Acme", 120.50)
INDEX ON UPPER(name) TAG name
SEEK "ACME"
? FOUND(), name, balance
SELECT name, balance FROM customer WHERE balance > 100 INTO CURSOR rich
LIST
USE legacy.dbf            && opens a FoxPro table as a read-only snapshot
IMPORT FOXPRO legacy.dbf  && converts it (plus memo and index tags) into a Joe Pro table
```

![Joe Pro IDE](docs/images/ide-light.png)

## Layout

```
src/
  JoePro.Core            values, comparisons, formatting, index key encoding
  JoePro.Legacy.Formats  read-only DBF/FPT/DBT/CDX/IDX readers, DBF writer
  JoePro.Data            SQLite-backed tables, indexes, work areas, buffering, transactions
  JoePro.Language        preprocessor, lexer, parser, AST
  JoePro.Runtime         interpreter, object model, xBase commands, SELECT-SQL, built-ins
  JoePro.Migration       legacy importer, program analyzer, migration report
  JoePro.Ui.Runtime      Avalonia rendering of FoxPro forms and controls, Browse grid model
  JoePro.Ide             the Joe Pro IDE (Avalonia)
  JoePro.Cli             `joepro` command-line host
tests/
  JoePro.Tests           unit and end-to-end tests
  JoePro.Ui.Tests        headless UI tests (forms, IDE shell)
  corpus/                legacy sample files used by the tests
tools/
  JoePro.Screenshots     renders IDE and form screenshots headlessly
```
