namespace JoePro.Server;

/// <summary>
/// Record and table locks for every client, as RLOCK/FLOCK see them. A lock belongs to a session and an owner
/// (the client's data session and work area). When a session ends, or stops renewing its lease, its locks go.
/// </summary>
public sealed class LeaseLockManager
{
    private readonly object _gate = new();
    // (database, table) → locks: recno (0 = the table) → (session, owner)
    private readonly Dictionary<(string Db, string Table), Dictionary<int, (long Session, string Owner)>> _locks = new();

    private static (string, string) Key(string db, string table) => (db.ToUpperInvariant(), table.ToUpperInvariant());

    public bool TryLock(string db, string table, int recNo, long session, string owner)
    {
        lock (_gate)
        {
            var k = Key(db, table);
            if (!_locks.TryGetValue(k, out var held)) _locks[k] = held = new();
            bool Mine((long Session, string Owner) o) => o.Session == session && o.Owner == owner;
            if (held.TryGetValue(0, out var tableLock) && !Mine(tableLock)) return false;
            if (recNo == 0)
            {
                if (held.Any(h => h.Key != 0 && !Mine(h.Value))) return false;
                held[0] = (session, owner);
                return true;
            }
            if (held.TryGetValue(recNo, out var r) && !Mine(r)) return false;
            held[recNo] = (session, owner);
            return true;
        }
    }

    public void Unlock(string db, string table, int? recNo, long session, string owner)
    {
        lock (_gate)
        {
            if (!_locks.TryGetValue(Key(db, table), out var held)) return;
            foreach (var k in held.Where(h => h.Value.Session == session && h.Value.Owner == owner && (recNo == null || h.Key == recNo)).Select(h => h.Key).ToList())
                held.Remove(k);
        }
    }

    public bool IsLocked(string db, string table, int recNo)
    {
        lock (_gate)
            return _locks.TryGetValue(Key(db, table), out var held) && (held.ContainsKey(0) || held.ContainsKey(recNo));
    }

    /// <summary>Releases everything a session holds (it disconnected or its lease expired). Returns the count.</summary>
    public int ReleaseSession(long session)
    {
        lock (_gate)
        {
            int n = 0;
            foreach (var held in _locks.Values)
                foreach (var k in held.Where(h => h.Value.Session == session).Select(h => h.Key).ToList()) { held.Remove(k); n++; }
            return n;
        }
    }

    public List<(string Db, string Table, int RecNo, long Session, string Owner)> Snapshot()
    {
        lock (_gate)
            return _locks.SelectMany(t => t.Value.Select(h => (t.Key.Db, t.Key.Table, h.Key, h.Value.Session, h.Value.Owner))).ToList();
    }
}
