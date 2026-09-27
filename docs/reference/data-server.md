# Joe Pro Data Server

The Data Server lets several users work with the same databases over the network. It owns the database files;
clients send the engine's statements to it instead of opening the files. Application code does not change.

## Setting up

```sh
joepro-server init --admin root --password '…'          # writes joepro-server.json
joepro-server add-database sales data/sales.jpdb
joepro-server add-user alice --password '…'
joepro-server grant alice sales write                     # read, write, admin or none; * means every database
joepro-server run                                          # serve (Ctrl+C stops)
```

`joepro-server.json` also sets the port (default 9150), the listen address, the lock lease (default 30 seconds) and
an optional `.pfx` certificate. With a certificate, clients connect with TLS (`?tls=1`). Without one the protocol is
plain TCP, so use it only on a trusted network.

To run it as a Windows service or a systemd unit, run `joepro-server run --config <file>` from the service manager
(for example with a service wrapper). Native service integration is not built yet.

## Connecting

```foxpro
OPEN DATABASE "joepro://alice@server:9150/sales"          && password from JOEPRO_PASSWORD
OPEN DATABASE "joepro://alice:secret@server/sales?tls=1"
```

To keep programs unchanged, put `joepro-data.json` in the application folder. `OPEN DATABASE sales` then connects to
the server:

```json
{"databases": {"sales": "joepro://server:9150/sales"}}
```

In server mode:

- **Transactions.** Each client has its own connection per database on the server. Transactions (BEGIN
  TRANSACTION, buffering) work as in embedded mode. SQLite's WAL mode gives one writer at a time and many readers.
- **Locks.** RLOCK/FLOCK, and the locks that pessimistic buffering takes, are held on the server as leases. A client
  that disconnects or stops sending heartbeats loses its locks after the lease, so a crashed workstation never
  leaves orphaned locks.
- **Change notifications.** When a client commits, the other clients using that database are notified, and their
  cached rows refresh.
- **Permissions.** A read permission opens the database read-only. Statements that could reach other files
  (ATTACH, VACUUM INTO, extensions) are refused.

## Administration

| Command | What it does |
|---|---|
| `joepro-server backup <db> <file>` | Online backup (safe while clients work) |
| `joepro-server check <db>` | SQLite integrity check plus the Joe Pro catalog (tables, fields and index columns exist) |
| `joepro-server status joepro://root@server` | Connected sessions, their databases and the locks they hold |

Administrators can also send `backup`, `check` and `status` requests over the protocol.

## Protocol

Each message is a 4-byte little-endian length followed by a UTF-8 JSON object. A client sends `hello`, then
`open` for a database. It then sends `exec`, `scalar` or `query` requests with SQL and named parameters, `lock`,
`unlock` and `islocked` for locks, and `ping` as a heartbeat. The server answers with the request id, and pushes
`changed` events. Values are JSON numbers, strings, null, or `{"$b": base64}` for binary.
