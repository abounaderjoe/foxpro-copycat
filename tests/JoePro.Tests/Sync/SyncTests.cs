using JoePro.Core;
using JoePro.Language;
using JoePro.Legacy.Formats;
using JoePro.Runtime;
using JoePro.Runtime.Sync;

namespace JoePro.Tests.Sync;

/// <summary>
/// Stands in for Visual FoxPro in tests: applies inbox batches as joesync_agent.prg does (and, in trigger mode, logs
/// changes as the installed triggers do), and makes "legacy application" edits.
/// </summary>
internal sealed class FakeVfp(string folder, bool triggers)
{
    private string Table(string name) => Path.Combine(folder, name + ".dbf");

    public List<(bool Deleted, Value[] Values)> Rows(string table, out List<FieldDef> fields)
    {
        using var dbf = DbfTable.Open(Table(table));
        fields = dbf.Fields.Select(f => f.ToFieldDef()).ToList();
        return dbf.Records().Select(r => (r.Deleted, r.Values)).ToList();
    }

    private void Save(string table, List<FieldDef> fields, List<(bool Deleted, Value[] Values)> rows) => DbfWriter.Write(Table(table), fields, rows);

    private void Log(string table, string key, string op, string origin)
    {
        if (!triggers) return;
        var path = Path.Combine(folder, "_joesync_log.dbf");
        FieldDef[] fields = [new("ID", 'I'), new("TBL", 'C', 60), new("KEYVAL", 'C', 100), new("OP", 'C', 1), new("TS", 'T'), new("ORIGIN", 'C', 10)];
        var rows = File.Exists(path) ? Rows("_joesync_log", out _) : new();
        rows.Add((false, [Value.Number(rows.Count + 1, 0), Value.String(table), Value.String(key), Value.String(op), Value.DateTimeOf(DateTime.Now), Value.String(origin)]));
        DbfWriter.Write(path, fields, rows);
    }

    /// <summary>A legacy application edit: change (or delete, or add) the row with this key.</summary>
    public void Edit(string table, string keyField, string key, Action<Dictionary<string, Value>>? change, bool delete = false)
    {
        var rows = Rows(table, out var fields);
        var k = fields.FindIndex(f => f.Name.Equals(keyField, StringComparison.OrdinalIgnoreCase));
        var i = rows.FindIndex(r => !r.Deleted && SyncRow.KeyText(r.Values[k]) == key);
        if (delete) rows[i] = (true, rows[i].Values);
        else
        {
            var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
            var values = i >= 0 ? rows[i].Values : fields.Select(f => f.Normalize().BlankValue()).ToArray();
            for (int f = 0; f < fields.Count; f++) d[fields[f].Name] = values[f];
            change!(d);
            var nv = fields.Select(f => d[f.Name]).ToArray();
            if (i >= 0) rows[i] = (false, nv); else rows.Add((false, nv));
        }
        Save(table, fields, rows);
        Log(table, key, delete ? "D" : "U", "LEGACY");
    }

    /// <summary>One pass of the agent over the ready batches.</summary>
    public int RunAgent()
    {
        var ready = Directory.GetFiles(folder, "_joesync_inbox_*.ready").OrderBy(f => f).ToList();
        foreach (var marker in ready)
        {
            var baseName = marker[..^".ready".Length];
            var done = new List<string>();
            using (var inbox = DbfTable.Open(baseName + ".dbf"))
                foreach (var r in inbox.Records())
                {
                    var (id, table, keyField, key, op, data) = ((int)r.Values[0].AsNumber, r.Values[1].AsString.Trim(), r.Values[2].AsString.Trim(), r.Values[3].AsString, r.Values[4].AsString, r.Values[5].AsString);
                    var rows = Rows(table, out var fields);
                    var k = fields.FindIndex(f => f.Name.Equals(keyField, StringComparison.OrdinalIgnoreCase));
                    var i = rows.FindIndex(x => !x.Deleted && SyncRow.KeyText(x.Values[k]) == key);
                    // As joesync_agent.prg: only apply to the row sync saw.
                    var exists = r.Values[7].AsBool;
                    var stale = (i >= 0) != exists || (i >= 0 && LegacySide.Decode(r.Values[6].AsString).Any(e =>
                        fields.FindIndex(f => f.Name.Equals(e.Key, StringComparison.OrdinalIgnoreCase)) is var fi and >= 0 && !SyncRow.Same(rows[i].Values[fi], e.Value)));
                    if (stale) { done.Add($"{id}|STALE"); continue; }
                    if (op == "D") { if (i >= 0) rows[i] = (true, rows[i].Values); }
                    else
                    {
                        var values = i >= 0 ? (Value[])rows[i].Values.Clone() : fields.Select(f => f.Normalize().BlankValue()).ToArray();
                        foreach (var (field, v) in LegacySide.Decode(data))
                        {
                            var f = fields.FindIndex(x => x.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
                            if (f >= 0) values[f] = v.IsNull ? fields[f].Normalize().BlankValue() : fields[f].Normalize().Coerce(v);
                        }
                        if (i >= 0) rows[i] = (false, values); else rows.Add((false, values));
                    }
                    Save(table, fields, rows);
                    Log(table, key, op, "JOE");
                    done.Add($"{id}|OK");
                }
            File.WriteAllLines(baseName.Replace("_joesync_inbox_", "_joesync_done_") + ".txt", done);
            foreach (var ext in new[] { ".dbf", ".fpt", ".ready" }) if (File.Exists(baseName + ext)) File.Delete(baseName + ext);
        }
        return ready.Count;
    }
}

public class SyncTests
{
    private readonly string _dir = TestPaths.TempDir();

    private (SyncEngine Engine, FakeVfp Vfp, Interpreter App) Setup(CaptureMode capture)
    {
        var legacy = Path.Combine(_dir, "legacy");
        Directory.CreateDirectory(legacy);
        FieldDef[] fields = [new("CUSTID", 'N', 6), new("NAME", 'C', 20), new("CITY", 'C', 15), new("BALANCE", 'N', 10, 2)];
        (bool, Value[]) R(int id, string name, string city, double balance) => (false, [Value.Number(id, 0), Value.String(name), Value.String(city), Value.Number(balance, 2)]);
        DbfWriter.Write(Path.Combine(legacy, "customer.dbf"), fields, [R(1, "Acme", "Boston", 100), R(2, "Globex", "Chicago", 50), R(3, "Initech", "Austin", 0)]);
        // The Joe Pro copy (as IMPORT FOXPRO made it), used by the new application.
        var app = new Interpreter(new TextWriterOutput(TextWriter.Null), _dir);
        app.ExecuteCommand("""
            CREATE DATABASE shop
            CREATE TABLE customer (custid N(6), name C(20), city C(15), balance N(10,2) CHECK balance >= 0 ERROR "No negative balances")
            ALTER TABLE customer ADD PRIMARY KEY custid TAG custid
            INSERT INTO customer VALUES (1, 'Acme', 'Boston', 100)
            INSERT INTO customer VALUES (2, 'Globex', 'Chicago', 50)
            INSERT INTO customer VALUES (3, 'Initech', 'Austin', 0)
            CLOSE TABLES ALL
            """);
        var config = new SyncConfig { Legacy = "legacy", LegacyDatabase = capture == CaptureMode.Triggers ? "legacy/app.dbc" : null, Database = "shop.jpdb", Capture = capture };
        config.Tables.Add(new SyncTable { Name = "customer", Key = "custid" });
        config.Save(Path.Combine(_dir, "joesync.json"));
        var vfp = new FakeVfp(legacy, capture == CaptureMode.Triggers);
        if (capture == CaptureMode.Triggers) vfp.Edit("customer", "custid", "1", d => { });   // creates the log (as the installer does)
        var engine = new SyncEngine(Path.Combine(_dir, "joesync.json"));
        return (engine, vfp, app);
    }

    private static Dictionary<string, string> JoeRows(Interpreter app)
    {
        app.ExecuteCommand("SELECT * FROM customer WHERE !DELETED() INTO CURSOR qc");
        var wa = app.Session.FindAlias("qc")!;
        var d = new Dictionary<string, string>();
        wa.GoTop();
        while (!wa.Eof)
        {
            d[SyncRow.KeyText(wa.Get(0))] = string.Join("|", Enumerable.Range(1, 3).Select(i => SyncRow.Canon(wa.Get(i))));
            wa.Skip();
        }
        wa.Close();
        return d;
    }

    private static Dictionary<string, string> LegacyRows(FakeVfp vfp) =>
        vfp.Rows("customer", out _).Where(r => !r.Deleted).ToDictionary(r => SyncRow.KeyText(r.Values[0]), r => string.Join("|", r.Values.Skip(1).Select(SyncRow.Canon)));

    [Fact]
    public void Snapshot_sync_merges_fields_logs_conflicts_and_converges()
    {
        var (engine, vfp, app) = Setup(CaptureMode.Snapshot);
        using var _ = engine;
        var init = engine.Initialize();
        Assert.Equal((0, 0, 0), (init.AppliedToJoe, init.AppliedToLegacy, init.Conflicts));
        Assert.Equal(3, engine.State.BaselineCount("customer"));
        Assert.True(File.Exists(Path.Combine(_dir, "legacy", "joesync_agent.prg")));

        // Both sides work: different fields of row 1, a legacy delete, Joe Pro edits and an insert.
        vfp.Edit("customer", "custid", "1", d => d["CITY"] = Value.String("Cambridge"));
        vfp.Edit("customer", "custid", "3", null, delete: true);
        app.ExecuteCommand("""
            UPDATE customer SET balance = 125 WHERE custid = 1
            UPDATE customer SET name = 'Globex Corp' WHERE custid = 2
            INSERT INTO customer VALUES (4, 'Umbrella', 'Raccoon', 10)
            """);
        var c1 = engine.RunCycle();
        Assert.Equal(0, c1.Conflicts);
        Assert.Equal((2, 3), (c1.AppliedToJoe, c1.AppliedToLegacy));   // Joe: row 1 city, row 3 deleted; legacy: rows 1, 2, 4
        Assert.Equal(1, engine.Status().PendingBatches);
        Assert.Equal(1, vfp.RunAgent());
        var c2 = engine.RunCycle();
        Assert.Equal((0, 0), (c2.LegacyChanges, c2.JoeChanges));        // nothing echoes back
        Assert.Equal(0, engine.Status().PendingBatches);
        Assert.Equal(JoeRows(app), LegacyRows(vfp));
        Assert.Equal("CCambridge|N125", string.Join("|", JoeRows(app)["1"].Split('|').Skip(1)));

        // Same field on both sides: the legacy side (system of record) wins; the Joe Pro value is in the log.
        vfp.Edit("customer", "custid", "1", d => d["NAME"] = Value.String("Acme Legacy"));
        app.ExecuteCommand("UPDATE customer SET name = 'Acme Joe' WHERE custid = 1");
        var c3 = engine.RunCycle();
        Assert.Equal(1, c3.Conflicts);
        var conflict = Assert.Single(engine.State.Conflicts());
        Assert.Equal(("NAME", "Acme Legacy", "Acme Joe", SyncSide.Legacy), (conflict.Field, conflict.LegacyValue, conflict.JoeValue, conflict.Winner));
        Assert.StartsWith("CAcme Legacy", JoeRows(app)["1"]);
        // Review queue: re-apply the losing value to both sides.
        engine.Resolve(conflict.Id, SyncSide.Joe);
        vfp.RunAgent();
        engine.RunCycle();
        Assert.Empty(engine.State.Conflicts());
        Assert.StartsWith("CAcme Joe", LegacyRows(vfp)["1"]);
        Assert.Equal(JoeRows(app), LegacyRows(vfp));

        // Delete against update: the update wins and the row comes back on the deleting side.
        vfp.Edit("customer", "custid", "2", null, delete: true);
        app.ExecuteCommand("UPDATE customer SET city = 'Evanston' WHERE custid = 2");
        engine.RunCycle();
        vfp.RunAgent();
        engine.RunCycle();
        Assert.Contains(engine.State.Conflicts(), c => c.Kind == "delete-update");
        Assert.Contains("Evanston", LegacyRows(vfp)["2"]);
        Assert.Equal(JoeRows(app), LegacyRows(vfp));

        // A legacy value that breaks a Joe Pro rule blocks the row for review instead of being forced in.
        vfp.Edit("customer", "custid", "4", d => d["BALANCE"] = Value.Number(-5, 2));
        var c4 = engine.RunCycle();
        Assert.Equal(1, c4.Errors);
        Assert.Equal(1, engine.Status().Blocked);
        Assert.Contains(engine.State.Conflicts(), c => c.Kind == "joe-error" && c.JoeValue!.Contains("negative"));

        // Cutover: Joe Pro becomes the system of record.
        var steps = engine.Cutover();
        Assert.Contains(steps, s => s.Contains("system of record"));
        Assert.True(SyncConfig.Load(Path.Combine(_dir, "joesync.json")).Tables.All(t => t.Authority == SyncSide.Joe));
        app.Session.Dispose();
    }

    [Fact]
    public void Trigger_capture_reads_the_log_and_skips_the_agents_own_changes()
    {
        var (engine, vfp, app) = Setup(CaptureMode.Triggers);
        using var _ = engine;
        engine.Initialize();
        foreach (var prg in new[] { "joesync_agent.prg", "joesync_install.prg", "joesync_uninstall.prg" })
            Parser.ParseProgram(File.ReadAllText(Path.Combine(_dir, "legacy", prg)), prg);   // the programs VFP runs compile
        Assert.Contains("CREATE TRIGGER ON customer FOR UPDATE", File.ReadAllText(Path.Combine(_dir, "legacy", "joesync_install.prg")));

        vfp.Edit("customer", "custid", "2", d => d["BALANCE"] = Value.Number(75, 2));
        app.ExecuteCommand("UPDATE customer SET city = 'Dallas' WHERE custid = 3");
        var c = engine.RunCycle();
        Assert.Equal((1, 1), (c.LegacyChanges, c.JoeChanges));
        vfp.RunAgent();                                   // its writes are logged with origin JOE
        var after = engine.RunCycle();
        Assert.Equal((0, 0), (after.LegacyChanges, after.JoeChanges));
        Assert.Equal(JoeRows(app), LegacyRows(vfp));
        app.Session.Dispose();
    }

    [Fact]
    public void Random_edits_on_both_sides_converge()
    {
        var (engine, vfp, app) = Setup(CaptureMode.Snapshot);
        using var _ = engine;
        engine.Initialize();
        var rnd = new Random(42);
        string[] cities = ["Boston", "Austin", "Denver", "Miami", "Seattle"];
        var nextId = 10;
        for (int round = 0; round < 30; round++)
        {
            var keys = LegacyRows(vfp).Keys.Intersect(JoeRows(app).Keys).ToList();
            foreach (var side in new[] { 0, 1 })
            {
                var action = rnd.Next(10);
                var key = keys[rnd.Next(keys.Count)];
                if (side == 0)
                {
                    if (action < 6) vfp.Edit("customer", "custid", key, d => d["CITY"] = Value.String(cities[rnd.Next(cities.Length)]));
                    else if (action < 8) vfp.Edit("customer", "custid", key, d => d["BALANCE"] = Value.Number(rnd.Next(1000), 2));
                    else if (action < 9) vfp.Edit("customer", "custid", (nextId++).ToString(), d => { d["CUSTID"] = Value.Number(nextId - 1, 0); d["NAME"] = Value.String("L" + round); });
                    else if (keys.Count > 3) vfp.Edit("customer", "custid", key, null, delete: true);
                }
                else
                {
                    if (action < 6) app.ExecuteCommand($"UPDATE customer SET city = '{cities[rnd.Next(cities.Length)]}' WHERE custid = {key}");
                    else if (action < 8) app.ExecuteCommand($"UPDATE customer SET name = 'J{round}' WHERE custid = {key}");
                    else if (action < 9) app.ExecuteCommand($"INSERT INTO customer VALUES ({nextId++}, 'J{round}', 'Tulsa', 1)");
                    else if (keys.Count > 3) app.ExecuteCommand($"DELETE FROM customer WHERE custid = {key}");
                }
            }
            engine.RunCycle();
            if (rnd.Next(3) > 0) vfp.RunAgent();   // the agent sometimes lags behind
        }
        // Let everything settle.
        for (int i = 0; i < 3; i++) { vfp.RunAgent(); engine.RunCycle(); }
        Assert.Equal(0, engine.Status().PendingBatches);
        Assert.Equal(0, engine.Status().Blocked);
        Assert.Equal(JoeRows(app), LegacyRows(vfp));
        app.Session.Dispose();
    }

    [Fact]
    public void A_legacy_edit_made_while_a_batch_is_pending_is_not_overwritten()
    {
        var (engine, vfp, app) = Setup(CaptureMode.Snapshot);
        using var _ = engine;
        engine.Initialize();
        app.ExecuteCommand("UPDATE customer SET balance = 999 WHERE custid = 1");
        engine.RunCycle();                                                          // batch for row 1 pending
        vfp.Edit("customer", "custid", "1", d => d["CITY"] = Value.String("Salem"));  // a user edits row 1 before the agent runs
        vfp.RunAgent();                                                             // the agent answers STALE
        engine.RunCycle();                                                          // merges again: both changes kept
        vfp.RunAgent();
        engine.RunCycle();
        var row = LegacyRows(vfp)["1"];
        Assert.Contains("CSalem", row);
        Assert.Contains("N999", row);
        Assert.Equal(JoeRows(app), LegacyRows(vfp));
        app.Session.Dispose();
    }
}
