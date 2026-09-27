using JoePro.Data;

namespace JoePro.Runtime.Sync;

/// <summary>
/// What sync remembers between cycles (joesync.db): the last agreed version of every row (the base of three-way
/// merges, and the snapshot legacy changes are found against), how far each side's change log has been read,
/// batches sent to the legacy agent and not yet confirmed, rows blocked by an error, the conflict log and cycle stats.
/// </summary>
public sealed class SyncState : IDisposable
{
    private readonly SqliteStoreConnection _db;

    public SyncState(string path)
    {
        _db = SqliteStoreConnection.Open(path, create: true);
        _db.Execute("""
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS baseline(tbl TEXT NOT NULL COLLATE NOCASE, key TEXT NOT NULL, data TEXT NOT NULL, hash TEXT NOT NULL, PRIMARY KEY(tbl, key));
            CREATE TABLE IF NOT EXISTS pending(batch INTEGER NOT NULL, id INTEGER NOT NULL, tbl TEXT NOT NULL COLLATE NOCASE, key TEXT NOT NULL, expect TEXT, PRIMARY KEY(batch, id));
            CREATE TABLE IF NOT EXISTS blocked(tbl TEXT NOT NULL COLLATE NOCASE, key TEXT NOT NULL, reason TEXT, ts INTEGER, PRIMARY KEY(tbl, key));
            CREATE TABLE IF NOT EXISTS conflicts(id INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL, tbl TEXT NOT NULL, key TEXT NOT NULL, field TEXT NOT NULL,
                kind TEXT NOT NULL, legacy TEXT, joe TEXT, winner TEXT NOT NULL, resolved INTEGER NOT NULL DEFAULT 0, note TEXT);
            CREATE TABLE IF NOT EXISTS recheck(tbl TEXT NOT NULL COLLATE NOCASE, key TEXT NOT NULL, PRIMARY KEY(tbl, key));
            CREATE TABLE IF NOT EXISTS cycles(ts INTEGER NOT NULL, legacy INTEGER, joe INTEGER, to_legacy INTEGER, to_joe INTEGER, conflicts INTEGER, errors INTEGER, ms INTEGER);
            """, []);
    }

    public string? Get(string key) => _db.Scalar("SELECT value FROM meta WHERE key=$k", [("$k", key)]) as string;
    public void Set(string key, string value) => _db.Execute("INSERT INTO meta VALUES ($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", [("$k", key), ("$v", value)]);
    public long GetLong(string key) => long.TryParse(Get(key), out var v) ? v : 0;

    public void Atomic(Action a)
    {
        _db.Execute("BEGIN", []);
        try { a(); _db.Execute("COMMIT", []); }
        catch { _db.Execute("ROLLBACK", []); throw; }
    }

    // ---- Baseline ----------------------------------------------------------------------------------

    public SyncRow? Baseline(string table, string key) =>
        _db.Scalar("SELECT data FROM baseline WHERE tbl=$t AND key=$k", [("$t", table), ("$k", key)]) is string json ? SyncRow.FromJson(json) : null;

    public Dictionary<string, string> BaselineHashes(string table) =>
        _db.Query("SELECT key, hash FROM baseline WHERE tbl=$t", [("$t", table)]).ToDictionary(r => (string)r[0]!, r => (string)r[1]!);

    public void SetBaseline(string table, string key, SyncRow? row)
    {
        if (row == null) _db.Execute("DELETE FROM baseline WHERE tbl=$t AND key=$k", [("$t", table), ("$k", key)]);
        else _db.Execute("INSERT INTO baseline VALUES ($t,$k,$d,$h) ON CONFLICT(tbl, key) DO UPDATE SET data=excluded.data, hash=excluded.hash",
            [("$t", table), ("$k", key), ("$d", row.ToJson()), ("$h", row.Hash())]);
    }

    public int BaselineCount(string table) => Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM baseline WHERE tbl=$t", [("$t", table)]));

    // ---- Pending legacy batches and blocked rows ---------------------------------------------------

    public long NextBatch()
    {
        var n = GetLong("batch") + 1;
        Set("batch", n.ToString());
        return n;
    }

    /// <summary>A row sent to the agent, with the legacy row it expects to find (the common ancestor if a user changes it meanwhile).</summary>
    public void AddPending(long batch, int id, string table, string key, SyncRow? expected) =>
        _db.Execute("INSERT INTO pending VALUES ($b,$i,$t,$k,$e)", [("$b", batch), ("$i", id), ("$t", table), ("$k", key), ("$e", expected?.ToJson())]);

    public List<long> PendingBatches() => _db.Query("SELECT DISTINCT batch FROM pending ORDER BY batch", []).Select(r => Convert.ToInt64(r[0])).ToList();

    public List<(int Id, string Table, string Key, SyncRow? Expected)> PendingRows(long batch) =>
        _db.Query("SELECT id, tbl, key, expect FROM pending WHERE batch=$b", [("$b", batch)])
            .Select(r => (Convert.ToInt32(r[0]), (string)r[1]!, (string)r[2]!, r[3] is string json ? SyncRow.FromJson(json) : null)).ToList();

    public HashSet<string> PendingKeys(string table) =>
        _db.Query("SELECT key FROM pending WHERE tbl=$t", [("$t", table)]).Select(r => (string)r[0]!).ToHashSet();

    public void ClearPending(long batch) => _db.Execute("DELETE FROM pending WHERE batch=$b", [("$b", batch)]);

    public int PendingRowCount() => Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM pending", []));

    public void Block(string table, string key, string reason) =>
        _db.Execute("INSERT INTO blocked VALUES ($t,$k,$r,$ts) ON CONFLICT(tbl, key) DO UPDATE SET reason=excluded.reason, ts=excluded.ts",
            [("$t", table), ("$k", key), ("$r", reason), ("$ts", DateTime.UtcNow.Ticks)]);

    public void Unblock(string table, string key) => _db.Execute("DELETE FROM blocked WHERE tbl=$t AND key=$k", [("$t", table), ("$k", key)]);

    public HashSet<string> BlockedKeys(string table) =>
        _db.Query("SELECT key FROM blocked WHERE tbl=$t", [("$t", table)]).Select(r => (string)r[0]!).ToHashSet();

    public List<(string Table, string Key, string Reason)> Blocked() =>
        _db.Query("SELECT tbl, key, reason FROM blocked ORDER BY ts", []).Select(r => ((string)r[0]!, (string)r[1]!, r[2] as string ?? "")).ToList();

    /// <summary>Keys whose legacy row must be looked at again (it changed while a batch for it was pending).</summary>
    public void Recheck(string table, string key) =>
        _db.Execute("INSERT OR IGNORE INTO recheck VALUES ($t,$k)", [("$t", table), ("$k", key)]);

    public HashSet<string> RecheckKeys(string table) =>
        _db.Query("SELECT key FROM recheck WHERE tbl=$t", [("$t", table)]).Select(r => (string)r[0]!).ToHashSet();

    public void ClearRecheck(string table, string key) => _db.Execute("DELETE FROM recheck WHERE tbl=$t AND key=$k", [("$t", table), ("$k", key)]);

    // ---- Conflicts and stats -----------------------------------------------------------------------

    public long AddConflict(string table, string key, string field, string kind, string? legacy, string? joe, SyncSide winner, string? note = null)
    {
        _db.Execute("INSERT INTO conflicts(ts, tbl, key, field, kind, legacy, joe, winner, note) VALUES ($ts,$t,$k,$f,$kind,$l,$j,$w,$n)",
            [("$ts", DateTime.UtcNow.Ticks), ("$t", table), ("$k", key), ("$f", field), ("$kind", kind), ("$l", legacy), ("$j", joe), ("$w", winner.ToString()), ("$n", note)]);
        return Convert.ToInt64(_db.Scalar("SELECT last_insert_rowid()", []));
    }

    public List<SyncConflict> Conflicts(bool openOnly = true) =>
        _db.Query($"SELECT id, ts, tbl, key, field, kind, legacy, joe, winner, resolved, note FROM conflicts{(openOnly ? " WHERE resolved = 0" : "")} ORDER BY id", [])
            .Select(r => new SyncConflict(Convert.ToInt64(r[0]), new DateTime(Convert.ToInt64(r[1]), DateTimeKind.Utc), (string)r[2]!, (string)r[3]!, (string)r[4]!, (string)r[5]!,
                r[6] as string, r[7] as string, Enum.Parse<SyncSide>((string)r[8]!), Convert.ToInt64(r[9]) != 0, r[10] as string)).ToList();

    public SyncConflict? Conflict(long id) => Conflicts(openOnly: false).FirstOrDefault(c => c.Id == id);

    public void ResolveConflict(long id, string note) =>
        _db.Execute("UPDATE conflicts SET resolved = 1, note = $n WHERE id = $i", [("$i", id), ("$n", note)]);

    public void AddCycle(SyncCycle c) =>
        _db.Execute("INSERT INTO cycles VALUES ($ts,$l,$j,$tl,$tj,$c,$e,$ms)",
            [("$ts", c.TimeUtc.Ticks), ("$l", c.LegacyChanges), ("$j", c.JoeChanges), ("$tl", c.AppliedToLegacy), ("$tj", c.AppliedToJoe), ("$c", c.Conflicts), ("$e", c.Errors), ("$ms", c.Milliseconds)]);

    public List<SyncCycle> RecentCycles(int n = 20) =>
        _db.Query("SELECT ts, legacy, joe, to_legacy, to_joe, conflicts, errors, ms FROM cycles ORDER BY ts DESC LIMIT $n", [("$n", n)])
            .Select(r => new SyncCycle(new DateTime(Convert.ToInt64(r[0]), DateTimeKind.Utc), Convert.ToInt32(r[1]), Convert.ToInt32(r[2]), Convert.ToInt32(r[3]),
                Convert.ToInt32(r[4]), Convert.ToInt32(r[5]), Convert.ToInt32(r[6]), Convert.ToInt64(r[7]))).ToList();

    public void Dispose() => _db.Dispose();
}
