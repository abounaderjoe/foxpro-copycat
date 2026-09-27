using JoePro.Core;
namespace JoePro.Data;

/// <summary>
/// Record and table locks (RLOCK/FLOCK) shared by all data sessions in this process. Tables of a Data Server
/// database are locked on the server (leases that a crashed client cannot leave behind).
/// </summary>
public sealed class LockManager
{
    public static readonly LockManager Process = new();

    private readonly object _gate = new();
    private readonly Dictionary<(string Table, int RecNo), WorkArea> _records = new();
    private readonly Dictionary<string, WorkArea> _tables = new();

    private static string Key(WorkArea wa) => wa.Table.Store.Path + "|" + wa.Table.Name;

    private static bool SameOwner(WorkArea a, WorkArea b) => a.Session == b.Session && a.Table == b.Table;

    private static Remote.RemoteStoreConnection? RemoteOf(WorkArea wa) => wa.TableOrNull?.Store.Link as Remote.RemoteStoreConnection;

    // A lock's owner on the server: the data session and work area, as in VFP.
    private static string Owner(WorkArea wa) => $"{wa.Session.Uid}:{wa.Number}";

    public bool TryLock(WorkArea wa, int recNo)
    {
        if (recNo <= 0) return true;
        if (RemoteOf(wa) is { } remote) return remote.TryLock(wa.Table.Name, recNo, Owner(wa));
        var k = Key(wa);
        lock (_gate)
        {
            if (_tables.TryGetValue(k, out var owner) && !SameOwner(owner, wa)) return false;
            if (_records.TryGetValue((k, recNo), out var r) && !SameOwner(r, wa)) return false;
            _records[(k, recNo)] = wa;
            return true;
        }
    }

    public bool TryLockTable(WorkArea wa)
    {
        if (RemoteOf(wa) is { } remote) return remote.TryLock(wa.Table.Name, 0, Owner(wa));
        var k = Key(wa);
        lock (_gate)
        {
            if (_tables.TryGetValue(k, out var owner) && !SameOwner(owner, wa)) return false;
            if (_records.Any(kv => kv.Key.Table == k && !SameOwner(kv.Value, wa))) return false;
            _tables[k] = wa;
            return true;
        }
    }

    public bool IsLocked(WorkArea wa, int recNo)
    {
        if (RemoteOf(wa) is { } remote) return remote.IsLocked(wa.Table.Name, recNo);
        var k = Key(wa);
        lock (_gate) return _tables.ContainsKey(k) || _records.ContainsKey((k, recNo));
    }

    public void Release(WorkArea wa, int recNo)
    {
        if (wa.TableOrNull == null) return;
        if (RemoteOf(wa) is { } remote) { remote.Unlock(wa.Table.Name, recNo, Owner(wa)); return; }
        lock (_gate) _records.Remove((Key(wa), recNo));
    }

    public void ReleaseAll(WorkArea wa)
    {
        if (wa.TableOrNull == null) return;
        if (RemoteOf(wa) is { } remote)
        {
            try { remote.Unlock(wa.Table.Name, null, Owner(wa)); }
            catch (VfpException) { }   // the server releases the session's leases anyway
            return;
        }
        var k = Key(wa);
        lock (_gate)
        {
            foreach (var key in _records.Where(kv => kv.Key.Table == k && kv.Value == wa).Select(kv => kv.Key).ToList())
                _records.Remove(key);
            if (_tables.TryGetValue(k, out var o) && o == wa) _tables.Remove(k);
        }
    }
}
