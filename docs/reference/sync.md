# Two-way sync with a legacy VFP application

During a transition, the legacy VFP application and its Joe Pro replacement can run side by side on their own copies
of the data. Sync keeps the copies in step, table by table.

## Setting up

```sh
joepro sync config --legacy \\server\app\data --database sales.jpdb --table customer:cust_id --table orders:order_id
joepro sync init            # turns on the Joe Pro change journal, writes the VFP programs, reconciles every row once
joepro sync run --watch 10  # a cycle every 10 seconds
```

With `--dbc app.dbc`, sync reads a trigger log instead of comparing snapshots (see Capture). Every synced table needs a
primary or candidate key; record numbers are never used, because PACK renumbers them.

In Visual FoxPro 9, on a machine that sees the data folder, run the agent that applies Joe Pro's changes:

```foxpro
DO joesync_agent WITH "\\server\app\data"
```

## How changes travel

- **From VFP.** With snapshot capture (the default), each cycle compares the legacy tables with the last synced
  rows by key. Nothing in the legacy database changes. With trigger capture, `joesync_install.prg` adds logging
  triggers to the DBC. They are chained in front of the existing triggers and append to `_joesync_log.dbf`, which
  is exact and cheap.
- **From Joe Pro.** Sync reads the change journal.
- **To VFP.** Sync writes batch files (`_joesync_inbox_n.dbf`) to the data folder. The agent applies them through
  VFP, so VFP's own locking, indexes, rules and triggers are used, and it reports every row. Joe Pro never writes
  live DBF or CDX files. A row that a user changed after sync read it is refused as stale and merged again.
- **To Joe Pro.** Rows are applied with the database's rules and triggers.
- **No loops.** Changes made by sync are tagged: origin `sync` in the journal, `JOE` in the trigger log. Sync never
  captures them again.

## Conflicts

Rows are merged field by field against the last version both sides agreed on:

- Changes to different fields of a row are all kept.
- When both sides change the same field, the system of record wins. That is the legacy side until cutover. It is
  set per table with `authority` in `joesync.json`.
- The losing value is recorded in the conflict log. `joepro sync conflicts` lists the log, and the IDE's Sync
  dashboard (open `joesync.json`) shows it. There, **Use legacy value** or **Use Joe Pro value** writes the chosen
  value to both sides.
- Delete against update: the update wins and the row comes back, unless the table sets `deleteWins`.
- A change that fails a rule or trigger on either side blocks that row and is listed for review. It is never
  forced in.

## Cutover

Run a final cycle and let the agent finish. Then run `joepro sync cutover`: Joe Pro becomes the system of record.
Stop the legacy application. With trigger capture, run `joesync_uninstall.prg` in VFP to restore the tables'
original triggers.

## Not yet verified

This has been tested against a stand-in for VFP that follows the agent's rules. The planned 24-hour soak test
against real VFP 9 instances, with `VALIDATE DATABASE` checks, needs a Windows machine with VFP installed.
