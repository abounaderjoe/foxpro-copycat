using Avalonia.Headless.XUnit;
using JoePro.Core;
using JoePro.Ide;
using JoePro.Legacy.Formats;
using JoePro.Runtime.Sync;

namespace JoePro.Ui.Tests;

public class SyncDashboardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "joepro-sync-ui", Guid.NewGuid().ToString("N"));
    private readonly IdeSession _session;
    private readonly MainWindow _window;

    public SyncDashboardTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "legacy"));
        _session = new IdeSession(_dir);
        _window = new MainWindow(_session);
        _window.Show();
    }

    public void Dispose()
    {
        _window.Close();
        _session.Dispose();
    }

    private static void WriteLegacy(string path, string name) =>
        DbfWriter.Write(path, [new FieldDef("ID", 'N', 5), new FieldDef("NAME", 'C', 20)], [(false, [Value.Number(1, 0), Value.String(name)])]);

    [AvaloniaFact]
    public void Dashboard_runs_cycles_and_resolves_conflicts()
    {
        var dbf = Path.Combine(_dir, "legacy", "item.dbf");
        WriteLegacy(dbf, "bolt");
        _session.Execute("SET TALK OFF\nCREATE DATABASE shop\nCREATE TABLE item (id N(5), name C(20))\nINSERT INTO item VALUES (1, 'bolt')\nCLOSE TABLES ALL");
        var config = new SyncConfig { Legacy = "legacy", Database = "shop.jpdb" };
        config.Tables.Add(new SyncTable { Name = "item", Key = "id" });
        config.Save(Path.Combine(_dir, "joesync.json"));

        _window.OpenAny(Path.Combine(_dir, "joesync.json"));
        var dashboard = Assert.IsType<SyncDashboardTab>(_window.Documents.SelectedItem).Dashboard;
        Assert.Contains("No cycle has run yet", dashboard.Summary);
        dashboard.Engine.Initialize();

        WriteLegacy(dbf, "legacy nut");
        _session.Execute("UPDATE item SET name = 'joe nut' WHERE id = 1");
        var cycle = dashboard.RunCycle();
        Assert.Equal(1, cycle.Conflicts);
        var conflict = Assert.Single(dashboard.Conflicts);
        Assert.Equal(("legacy nut", "joe nut"), (conflict.LegacyValue, conflict.JoeValue));
        Assert.Contains("Open conflicts: 1", dashboard.Summary);

        dashboard.Resolve(conflict.Id, SyncSide.Joe);
        Assert.Empty(dashboard.Conflicts);
        Assert.Contains("Waiting for the VFP agent: 1 batch", dashboard.Summary);
        Assert.True(File.Exists(Path.Combine(_dir, "legacy", "_joesync_inbox_00000001.ready")));
        _session.Execute("SELECT name FROM item INTO CURSOR q");
        Assert.Equal("joe nut", _session.Runtime.Evaluate("TRIM(q.name)").AsString);
    }
}
