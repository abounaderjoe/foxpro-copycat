using System.Diagnostics;
using JoePro.Core;
using JoePro.Data;

namespace JoePro.Runtime.Sync;

/// <summary>
/// Two-way sync between a legacy VFP application's tables and a Joe Pro database during a transition.
/// Each cycle reads what changed on each side since the last one (the legacy snapshot or trigger log, the Joe Pro
/// change journal), matches rows by key, and merges field by field against the last agreed version: changes to
/// different fields both survive; when both sides changed the same field the system of record wins and the losing
/// value goes to the conflict log (with a one-step re-apply). An update beats a delete (the row comes back) unless the
/// table says otherwise. Changes made by sync itself carry the origin "sync"/"JOE", so they are never captured again.
/// Rules and triggers run when changes are applied; a row that fails is blocked and listed for review.
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private readonly string _configPath;
    private readonly Interpreter _rt;
    private readonly Store _db;
    private readonly LegacySide _legacy;

    public SyncEngine(string configPath, Interpreter? runtime = null)
    {
        _configPath = Path.GetFullPath(configPath);
        Config = SyncConfig.Load(_configPath);
        var dir = Path.GetDirectoryName(_configPath)!;
        if (!Path.IsPathRooted(Config.Legacy)) Config.Legacy = Path.GetFullPath(Path.Combine(dir, Config.Legacy));
        if (Config.LegacyDatabase != null && !Path.IsPathRooted(Config.LegacyDatabase)) Config.LegacyDatabase = Path.GetFullPath(Path.Combine(dir, Config.LegacyDatabase));
        _rt = runtime ?? new Interpreter(new TextWriterOutput(TextWriter.Null), dir);
        var database = JoePro.Data.Remote.DataServerAddress.IsServerPath(Config.Database) || Path.IsPathRooted(Config.Database) ? Config.Database : Path.GetFullPath(Path.Combine(dir, Config.Database));
        _db = _rt.Session.OpenDatabase(database);
        _db.Origin = "sync";
        _legacy = new LegacySide(Config);
        State = new SyncState(Path.Combine(dir, "joesync.db"));
    }

    public SyncConfig Config { get; }
    public SyncState State { get; }
    public Store Database => _db;
    public LegacySide Legacy => _legacy;

    // ---- Setup -----------------------------------------------------------------------------------------

    /// <summary>
    /// Starts sync: turns on the Joe Pro journal, writes the VFP programs into the legacy folder, and reconciles every
    /// row once (equal rows become the agreed baseline; differences are merged like a cycle, authority first).
    /// </summary>
    public SyncCycle Initialize()
    {
        _db.JournalEnabled = true;
        State.Set("joe_seq", _db.JournalHead.ToString());
        WriteLegacyPrograms();
        if (Config.Capture == CaptureMode.Triggers)
            State.Set("legacy_log", (_legacy.ReadLog(0).Select(e => e.Id).DefaultIfEmpty(0).Max()).ToString());
        return RunCycle(full: true);
    }

    /// <summary>Writes joesync_agent.prg (and the trigger installer/uninstaller for trigger capture) into the legacy folder.</summary>
    public void WriteLegacyPrograms()
    {
        File.WriteAllText(Path.Combine(Config.Legacy, "joesync_agent.prg"), LegacySide.AgentProgram());
        if (Config.Capture == CaptureMode.Triggers)
        {
            File.WriteAllText(Path.Combine(Config.Legacy, "joesync_install.prg"), LegacySide.InstallProgram(Config));
            File.WriteAllText(Path.Combine(Config.Legacy, "joesync_uninstall.prg"), LegacySide.UninstallProgram(Config));
        }
    }

    // ---- The Joe Pro side ------------------------------------------------------------------------------

    private Table JoeTable(SyncTable t) => _db.HasTable(t.Name) ? _db.OpenTable(t.Name, _rt) : throw new VfpException(ErrorCodes.FileDoesNotExist, $"The Joe Pro database has no table {t.Name}.");

    private Dictionary<string, SyncRow> ReadJoe(SyncTable t)
    {
        var table = JoeTable(t);
        var key = table.Schema.FieldIndex(t.Key);
        if (key < 0) throw new VfpException(ErrorCodes.InvalidArgument, $"{t.Name} has no key field {t.Key} in the Joe Pro database.");
        var rows = new Dictionary<string, SyncRow>();
        foreach (var r in table.Scan(null, forward: true, skipDeleted: true))
        {
            if (r.Deleted) continue;
            var row = new SyncRow();
            for (int i = 0; i < table.Fields.Count; i++) row[table.Fields[i].Name] = r.Values[i];
            rows[SyncRow.KeyText(r.Values[key])] = row;
        }
        return rows;
    }

    private WorkArea? _area;

    private WorkArea AreaFor(SyncTable t)
    {
        if (_area is { InUse: true } a && a.Table.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)) return a;
        _area?.Close();
        return _area = _rt.Session.Use(_db.Name + "!" + t.Name, _rt.Session.FreeArea(), "__JOESYNC", again: true, exclusive: false);
    }

    /// <summary>Applies a row (or its deletion) to the Joe Pro table, with its rules and triggers. Returns the error, or null.</summary>
    private string? ApplyJoe(SyncTable t, string key, SyncRow? row, IReadOnlyCollection<string> fields)
    {
        try
        {
            var wa = AreaFor(t);
            var table = wa.Table;
            var keyIndex = table.Schema.FieldIndex(t.Key);
            int recNo = 0;
            foreach (var r in table.Scan(null, forward: true, skipDeleted: true))
                if (!r.Deleted && SyncRow.KeyText(r.Values[keyIndex]) == key) { recNo = r.RecNo; break; }
            if (row == null)
            {
                if (recNo == 0) return null;
                wa.Go(recNo);
                _rt.DeleteWithRules(wa);
                return null;
            }
            Value Convert(FieldDef f, Value v) => v.IsNull ? (f.Nullable ? v : f.BlankValue()) : f.Coerce(v.Kind == ValueKind.Currency && f.Type is 'N' or 'F' or 'B' or 'I' ? Value.Number((double)v.AsCurrency) : v);
            if (recNo > 0)
            {
                wa.Go(recNo);
                var changes = new List<(int, Value)>();
                foreach (var name in fields)
                {
                    var i = table.Schema.FieldIndex(name);
                    if (i < 0 || !row.TryGetValue(name, out var v)) continue;
                    var nv = Convert(table.Fields[i], v);
                    if (!SyncRow.Same(wa.Get(i), nv)) changes.Add((i, nv));
                }
                if (changes.Count > 0) _rt.ReplaceWithRules(wa, changes);
            }
            else
            {
                var values = table.Fields.Select(f => row.TryGetValue(f.Name, out var v) && fields.Contains(f.Name, StringComparer.OrdinalIgnoreCase) ? Convert(f, v) : f.BlankValue()).ToArray();
                _rt.AppendWithDefaults(wa, values);
            }
            return null;
        }
        catch (VfpException ex) { return ex.Message; }
    }

    // ---- The cycle -------------------------------------------------------------------------------------

    private static SyncRow Restrict(SyncRow? row, IReadOnlyCollection<string> fields)
    {
        var r = new SyncRow();
        if (row == null) return r;
        foreach (var f in fields) if (row.TryGetValue(f, out var v)) r[f] = v;
        return r;
    }

    private static string? Show(Value? v) => v is not { } x ? null : x.IsNull ? ".NULL." : x.Kind == ValueKind.Character ? x.AsString.TrimEnd() : SyncRow.Canon(x)[1..];

    /// <summary>Runs one cycle (or, with <paramref name="full"/>, reconciles every row). Returns its numbers.</summary>
    public SyncCycle RunCycle(bool full = false)
    {
        var watch = Stopwatch.StartNew();
        int legacyChanges = 0, joeChanges = 0, toLegacy = 0, toJoe = 0, conflicts = 0, errors = 0;

        // 1. Results of the batches the agent has applied.
        foreach (var batch in State.PendingBatches())
        {
            var done = _legacy.ReadDone(batch);
            if (done == null) continue;
            foreach (var (id, table, key, expected) in State.PendingRows(batch))
            {
                if (!done.TryGetValue(id, out var result) || result.Applied) continue;
                if (result.Stale)
                {
                    // A user changed the row before the agent got to it: merge again from the row both sides last shared.
                    State.SetBaseline(table, key, expected);
                    State.Recheck(table, key);
                    continue;
                }
                errors++;
                State.Block(table, key, "Legacy: " + result.Error);
                State.AddConflict(table, key, "*", "legacy-error", result.Error, null, SyncSide.Joe, "The VFP agent could not apply the change; the row is blocked until it is resolved.");
            }
            State.ClearPending(batch);
            _legacy.RemoveDone(batch);
        }

        // 2. What changed on each side.
        var joeSeq = State.GetLong("joe_seq");
        var journal = new List<JournalEntry>();
        for (var page = _db.ReadJournal(joeSeq, 5000); page.Count > 0; page = _db.ReadJournal(joeSeq, 5000))
        {
            journal.AddRange(page);
            joeSeq = page[^1].Seq;
        }
        var logEntries = Config.Capture == CaptureMode.Triggers ? _legacy.ReadLog(State.GetLong("legacy_log")) : [];

        var outgoing = new List<LegacySide.Change>();
        var baselineUpdates = new List<(string Table, string Key, SyncRow? Row)>();
        int changeId = 0;
        foreach (var t in Config.Tables)
        {
            var legacyRows = _legacy.ReadTable(t);
            var joeRows = ReadJoe(t);
            var fields = _legacy.FieldNames(t).Intersect(JoeTable(t).Fields.Select(f => f.Name.ToUpperInvariant())).ToList();
            var pending = State.PendingKeys(t.Name);
            var blocked = State.BlockedKeys(t.Name);

            // Legacy changes.
            var legacyChanged = new HashSet<string>();
            if (full) legacyChanged.UnionWith(legacyRows.Keys);
            else if (Config.Capture == CaptureMode.Snapshot)
            {
                var hashes = State.BaselineHashes(t.Name);
                foreach (var (key, row) in legacyRows)
                    if (!hashes.TryGetValue(key, out var h) || h != Restrict(row, fields).Hash()) legacyChanged.Add(key);
                foreach (var key in hashes.Keys) if (!legacyRows.ContainsKey(key)) legacyChanged.Add(key);
            }
            else legacyChanged.UnionWith(logEntries.Where(e => e.Table.Equals(t.Name, StringComparison.OrdinalIgnoreCase)).Select(e => e.Key));
            var recheck = State.RecheckKeys(t.Name);
            legacyChanged.UnionWith(recheck);
            // A row with a batch still pending is looked at again once the agent has answered.
            foreach (var key in legacyChanged.Where(pending.Contains).ToList())
            {
                if (Config.Capture == CaptureMode.Triggers) State.Recheck(t.Name, key);
                legacyChanged.Remove(key);
            }
            foreach (var key in recheck.Where(k => !pending.Contains(k))) State.ClearRecheck(t.Name, key);

            // Joe Pro changes (not made by sync).
            var joeChanged = new HashSet<string>(recheck.Where(k => !pending.Contains(k)));
            if (full) joeChanged.UnionWith(joeRows.Keys);
            else
                foreach (var e in journal.Where(e => e.Origin != "sync" && e.Table.Equals(t.Name, StringComparison.OrdinalIgnoreCase)))
                    if ((e.New ?? e.Old) is { } vals && vals.TryGetValue(t.Key, out var kv)) joeChanged.Add(SyncRow.KeyText(kv));
            legacyChanges += legacyChanged.Count;
            joeChanges += joeChanged.Count;

            foreach (var key in legacyChanged.Union(joeChanged).OrderBy(k => k, StringComparer.Ordinal))
            {
                if (blocked.Contains(key)) continue;
                var baseRow = State.Baseline(t.Name, key);
                legacyRows.TryGetValue(key, out var l0);
                joeRows.TryGetValue(key, out var j0);
                var l = l0 == null ? null : Restrict(l0, fields);
                var j = j0 == null ? null : Restrict(j0, fields);
                var lc = legacyChanged.Contains(key);
                var jc = joeChanged.Contains(key);
                if (full && l != null && j != null && l.Hash() == j.Hash()) { baselineUpdates.Add((t.Name, key, l)); continue; }
                if (full && baseRow == null) { lc = l != null; jc = j != null; }

                SyncRow? merged;
                if (lc && !jc) merged = l;
                else if (jc && !lc) merged = j;
                else if (l == null && j == null) merged = null;
                else if (l == null || j == null)
                {
                    // One side deleted, the other changed: the update wins unless the table prefers deletes.
                    var updated = l ?? j!;
                    var deleter = l == null ? SyncSide.Legacy : SyncSide.Joe;
                    merged = t.DeleteWins && baseRow != null ? null : updated;
                    conflicts++;
                    State.AddConflict(t.Name, key, "*", baseRow == null ? "insert-missing" : "delete-update",
                        l == null ? "(deleted)" : "(updated)", j == null ? "(deleted)" : "(updated)", merged == null ? deleter : deleter == SyncSide.Legacy ? SyncSide.Joe : SyncSide.Legacy);
                }
                else
                {
                    merged = new SyncRow(baseRow ?? new SyncRow());
                    foreach (var f in fields)
                    {
                        l.TryGetValue(f, out var lv);
                        j.TryGetValue(f, out var jv);
                        Value? bv = baseRow != null && baseRow.TryGetValue(f, out var b) ? b : null;
                        var lDiff = bv is not { } bb ? true : !SyncRow.Same(lv, bb);
                        var jDiff = bv is not { } bj ? true : !SyncRow.Same(jv, bj);
                        if (SyncRow.Same(lv, jv)) merged[f] = lv;
                        else if (lDiff && !jDiff) merged[f] = lv;
                        else if (jDiff && !lDiff) merged[f] = jv;
                        else
                        {
                            // Both changed the field: the system of record wins; the other value is kept in the log.
                            merged[f] = t.Authority == SyncSide.Legacy ? lv : jv;
                            conflicts++;
                            State.AddConflict(t.Name, key, f, baseRow == null ? "insert-insert" : "update-update", Show(lv), Show(jv), t.Authority);
                        }
                    }
                }

                // Apply where a side differs from the merged row.
                if (!SameRow(j, merged))
                {
                    var error = ApplyJoe(t, key, merged, fields);
                    if (error != null)
                    {
                        errors++;
                        State.Block(t.Name, key, "Joe Pro: " + error);
                        State.AddConflict(t.Name, key, "*", "joe-error", merged == null ? "(deleted)" : "(changed)", error, SyncSide.Legacy, "The change failed a rule or trigger in Joe Pro; the row is blocked until it is resolved.");
                        continue;
                    }
                    toJoe++;
                }
                if (!SameRow(l, merged))
                {
                    outgoing.Add(new LegacySide.Change(++changeId, t.LegacyFile is { } lf ? Path.GetFileNameWithoutExtension(lf) : t.Name.ToLowerInvariant(), t.Key, key, merged == null ? 'D' : 'U', merged, l));
                    toLegacy++;
                }
                baselineUpdates.Add((t.Name, key, merged));
            }
        }
        _area?.Close();
        _area = null;

        // 3. Send the legacy batch and remember the agreed rows.
        State.Atomic(() =>
        {
            if (outgoing.Count > 0)
            {
                var batch = State.NextBatch();
                foreach (var c in outgoing)
                    State.AddPending(batch, c.Id, Config.Tables.First(t => (t.LegacyFile is { } lf ? Path.GetFileNameWithoutExtension(lf) : t.Name.ToLowerInvariant()).Equals(c.Table, StringComparison.OrdinalIgnoreCase)).Name, c.Key, c.Expected);
                _legacy.WriteBatch(batch, outgoing);
            }
            foreach (var (table, key, row) in baselineUpdates) State.SetBaseline(table, key, row);
            State.Set("joe_seq", joeSeq.ToString());
            if (logEntries.Count > 0) State.Set("legacy_log", logEntries.Max(e => e.Id).ToString());
            State.Set("last_cycle", DateTime.UtcNow.Ticks.ToString());
        });
        var cycle = new SyncCycle(DateTime.UtcNow, legacyChanges, joeChanges, toLegacy, toJoe, conflicts, errors, watch.ElapsedMilliseconds);
        State.AddCycle(cycle);
        return cycle;
    }

    private static bool SameRow(SyncRow? a, SyncRow? b) => a == null ? b == null : b != null && a.Hash() == b.Hash();

    // ---- Review queue ----------------------------------------------------------------------------------

    /// <summary>Re-applies the losing value of a conflict (or either side's value) to both sides, and marks it reviewed.</summary>
    public void Resolve(long conflictId, SyncSide use)
    {
        var c = State.Conflict(conflictId) ?? throw new VfpException(ErrorCodes.InvalidArgument, $"No conflict {conflictId}.");
        var t = Config.Find(c.Table) ?? throw new VfpException(ErrorCodes.InvalidArgument, $"{c.Table} is not synced.");
        var fields = _legacy.FieldNames(t).Intersect(JoeTable(t).Fields.Select(f => f.Name.ToUpperInvariant())).ToList();
        ReadJoe(t).TryGetValue(c.Key, out var current);
        _legacy.ReadTable(t).TryGetValue(c.Key, out var legacyRow);
        var row = Restrict(use == SyncSide.Joe ? current ?? legacyRow : legacyRow ?? current, fields);
        if (c.Field != "*")
        {
            row = Restrict(current ?? legacyRow, fields);
            var text = use == SyncSide.Legacy ? c.LegacyValue : c.JoeValue;
            var field = JoeTable(t).Fields.First(f => f.Name.Equals(c.Field, StringComparison.OrdinalIgnoreCase));
            row[c.Field] = FromShown(text, field);
        }
        State.Unblock(t.Name, c.Key);
        var error = ApplyJoe(t, c.Key, row, fields);
        _area?.Close();
        _area = null;
        if (error != null) throw new VfpException(ErrorCodes.TriggerFailed, error);
        var batch = State.NextBatch();
        State.AddPending(batch, 1, t.Name, c.Key, legacyRow == null ? null : Restrict(legacyRow, fields));
        _legacy.WriteBatch(batch, [new LegacySide.Change(1, t.LegacyFile is { } lf ? Path.GetFileNameWithoutExtension(lf) : t.Name.ToLowerInvariant(), t.Key, c.Key, 'U', row, legacyRow == null ? null : Restrict(legacyRow, fields))]);
        State.SetBaseline(t.Name, c.Key, row);
        // The change sync just made in Joe Pro must not come back as a Joe Pro change.
        State.Set("joe_seq", _db.JournalHead.ToString());
        State.ResolveConflict(conflictId, $"Re-applied the {use.ToString().ToLowerInvariant()} value to both sides.");
    }

    public void Dismiss(long conflictId) => State.ResolveConflict(conflictId, "Reviewed; kept as merged.");

    private static Value FromShown(string? text, FieldDef f)
    {
        if (text == null || text == ".NULL.") return Value.Null;
        return f.Type switch
        {
            'C' or 'V' or 'M' => Value.String(text),
            'L' => Value.Logical(text == "T"),
            'D' => Value.FromJulian(long.Parse(text)),
            'T' => Value.DateTimeFromJulianMs(long.Parse(text)),
            'Y' => Value.Currency((decimal)double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)),
            _ => Value.Number(double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)),
        };
    }

    // ---- Dashboard and cutover -------------------------------------------------------------------------

    public SyncStatus Status()
    {
        var last = State.GetLong("last_cycle");
        DateTime? lastCycle = last > 0 ? new DateTime(last, DateTimeKind.Utc) : null;
        return new SyncStatus(lastCycle, lastCycle is { } lc ? DateTime.UtcNow - lc : null, State.PendingBatches().Count, State.PendingRowCount(),
            State.Blocked().Count, State.Conflicts().Count, State.RecentCycles(), Config.CutOver);
    }

    /// <summary>
    /// Cutover: Joe Pro becomes the system of record for every table (its values win conflicts from now on), and
    /// joesync_uninstall.prg is written for removing the legacy triggers once the legacy application is retired.
    /// Run a last cycle first, with no pending batches.
    /// </summary>
    public IReadOnlyList<string> Cutover()
    {
        var steps = new List<string>();
        if (State.PendingBatches().Count > 0) steps.Add("Warning: the VFP agent still has batches to apply; run the agent and another cycle before retiring the legacy application.");
        foreach (var t in Config.Tables) t.Authority = SyncSide.Joe;
        Config.CutOver = true;
        var saved = Config.Legacy;
        var raw = SyncConfig.Load(_configPath);
        foreach (var t in raw.Tables) t.Authority = SyncSide.Joe;
        raw.CutOver = true;
        raw.Save(_configPath);
        steps.Add("Joe Pro is now the system of record for every table.");
        if (Config.Capture == CaptureMode.Triggers)
        {
            File.WriteAllText(Path.Combine(saved, "joesync_uninstall.prg"), LegacySide.UninstallProgram(Config));
            steps.Add("Stop the legacy application, run joesync_uninstall.prg in VFP to remove the sync triggers, then stop the agent.");
        }
        else steps.Add("Stop the legacy application and the VFP agent; no legacy change needs undoing (snapshot capture).");
        return steps;
    }

    public void Dispose()
    {
        _area?.Close();
        State.Dispose();
    }
}
