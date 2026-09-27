using Avalonia;
using Avalonia.Headless.XUnit;
using JoePro.Core;
using JoePro.Data;
using JoePro.Ide;

namespace JoePro.Ui.Tests;

public class DatabaseDesignerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "joepro-dbd-tests", Guid.NewGuid().ToString("N"));
    private readonly IdeSession _session;
    private readonly MainWindow _window;

    public DatabaseDesignerTests()
    {
        Directory.CreateDirectory(_dir);
        _session = new IdeSession(_dir);
        _window = new MainWindow(_session);
        _window.Show();
        _session.Execute("SET TALK OFF");
        _session.Execute("""
            CREATE DATABASE shop
            CREATE TABLE customer (id I AUTOINC, name C(20))
            ALTER TABLE customer ADD PRIMARY KEY id TAG id
            CREATE TABLE orders (id I AUTOINC, custid I, amount N(10,2))
            INDEX ON custid TAG custid
            INSERT INTO customer (name) VALUES ('Acme')
            INSERT INTO orders (custid, amount) VALUES (1, 10)
            CLOSE TABLES ALL
            """);
    }

    public void Dispose()
    {
        _window.Close();
        _session.Dispose();
    }

    [AvaloniaFact]
    public void Modify_database_shows_tables_relations_and_integrity()
    {
        Assert.True(_session.Execute("MODIFY DATABASE"));
        var designer = Assert.IsType<DatabaseDesignerTab>(_window.Documents.SelectedItem).Designer;
        Assert.Equal(["CUSTOMER", "ORDERS"], designer.Tables);
        // Parents are placed before children, and positions are remembered in the database.
        Assert.True(designer.PositionOf("customer").X < designer.PositionOf("orders").X);
        designer.MoveTable("orders", new Point(400, 300));
        Assert.Contains("ORDERS", designer.Database.GetMeta("designer_layout"));

        // Dragging from the child's index to the parent's key relates them the right way round.
        designer.AddRelation("orders", "custid", "customer", "id");
        var rel = Assert.Single(designer.Relations);
        Assert.Equal(("CUSTOMER", "ID", "ORDERS", "CUSTID"), (rel.ParentTable, rel.ParentTag, rel.ChildTable, rel.ChildTag));
        Assert.Same(designer.Relations[0], designer.Relations[0]);
        Assert.True(designer.SelectedRelation!.SameLink(rel));
        Assert.Throws<InvalidOperationException>(() => designer.AddRelation("customer", "id", "orders", "custid"));   // already related

        // The index rows are where relation lines start and end.
        var anchor = designer.TagAnchor("orders", "custid", right: false);
        Assert.Equal(("ORDERS", "CUSTID"), designer.TagAt(anchor + new Point(20, 0)));

        // Referential integrity (the RI Builder): cascade deletes.
        designer.SetIntegrity(rel with { RiDelete = "CASCADE", RiInsert = "RESTRICT" });
        Assert.Equal("ICR", Assert.Single(designer.Relations).RiCode);
        _session.Execute("DELETE FROM customer WHERE id = 1");
        _session.Execute("SELECT COUNT(*) AS n FROM orders WHERE !DELETED() INTO CURSOR q");
        Assert.Equal(0, _session.Runtime.Evaluate("q.n").AsNumber);
        Assert.False(_session.Execute("INSERT INTO orders (custid) VALUES (7)"));

        designer.RemoveRelation(designer.Relations[0]);
        Assert.Empty(designer.Relations);

        // The schema script recreates the definitions.
        var script = DatabaseSchema.Read(designer.Database).CreateScript();
        Assert.Contains("CREATE TABLE CUSTOMER (", script);
        Assert.Contains("ALTER TABLE CUSTOMER ADD PRIMARY KEY id TAG ID", script);
    }

    [AvaloniaFact]
    public void Table_designer_edits_fields_indexes_and_rules_then_rebuilds_once()
    {
        _session.Execute("OPEN DATABASE shop\nUSE customer");
        Assert.True(_session.Execute("MODIFY STRUCTURE"));
        var designer = Assert.IsType<TableDesignerTab>(_window.Documents.SelectedItem).Designer;
        Assert.True(designer.IsDatabaseTable);
        Assert.False(designer.IsDirty);

        designer.SelectField("name");
        designer.UpdateField(f => f with { Width = 40, Caption = "Customer" });
        designer.AddField("city", 'C', 25);
        designer.UpdateField(f => f with { DefaultExpr = "\"Boston\"", RuleExpr = "!EMPTY(city)", RuleText = "City is required" });
        designer.AddTag("name", "UPPER(name)", TagKind.Candidate);
        Assert.True(designer.IsDirty);
        Assert.Contains(designer.Changes, c => c.Kind == SchemaChangeKind.AddField);
        Assert.Contains("ALTER TABLE CUSTOMER ADD COLUMN CITY C(25) DEFAULT \"Boston\" CHECK !EMPTY(city) ERROR \"City is required\"", designer.Script());

        Assert.True(designer.Save());
        Assert.False(designer.IsDirty);
        var table = _session.Runtime.Session.CurrentDatabase!.OpenTable("customer");
        Assert.Equal(["ID", "NAME", "CITY"], table.Fields.Select(f => f.Name));
        Assert.Equal(40, table.Fields[1].Width);
        Assert.Equal("Customer", table.Fields[1].Caption);
        Assert.Equal(TagKind.Candidate, table.Schema.FindTag("name")!.Kind);
        Assert.Equal("Acme", _session.Runtime.Evaluate("TRIM(customer.name)").AsString);   // the open work area follows the rebuild
        _session.Execute("INSERT INTO customer (name) VALUES ('Globex')");
        Assert.Equal("Boston", _session.Runtime.Evaluate("TRIM(customer.city)").AsString);

        // Invalid designs are not saved.
        designer.AddField("name", 'C', 5);
        Assert.Contains("duplicated", designer.Errors);
        Assert.False(designer.Save());
        designer.Revert();
        Assert.False(designer.IsDirty);

        // Table page: record rule and triggers need no rebuild.
        designer.Design.RuleExpr = "LEN(TRIM(name)) > 1";
        designer.Design.DeleteTrigger = ".F.";
        Assert.DoesNotContain(designer.Changes, c => c.NeedsRebuild);
        Assert.True(designer.Save());
        Assert.False(_session.Execute("DELETE FROM customer WHERE id = 1"));
    }

    [AvaloniaFact]
    public void New_table_from_the_database_designer_and_stored_procedures()
    {
        var designer = _window.OpenDatabaseDesigner(Path.Combine(_dir, "shop.jpdb"));
        var tableDesigner = _window.OpenTableDesigner(designer.DatabasePath, "orders");
        Assert.Same(tableDesigner, _window.OpenTableDesigner(designer.DatabasePath, "orders"));   // one designer per table

        var fresh = new TableDesigner(TableDesign.New("regions"), designer.Database, null, _session);
        fresh.AddField("code", 'C', 3);
        fresh.AddTag("code", "code", TagKind.Primary);
        Assert.True(fresh.Save());
        designer.Refresh();
        Assert.Contains("REGIONS", designer.Tables);

        var procs = _window.OpenStoredProcedures(designer.DatabasePath);
        procs.Editor.Text = "FUNCTION Greet(c)\nRETURN 'Hello ' + c\n";
        procs.Save();
        Assert.Equal("Hello Joe", _session.Runtime.Evaluate("Greet('Joe')").AsString);
        procs.Editor.Text = "FUNCTION Broken(\nIF .T.\n";
        Assert.ThrowsAny<Exception>(procs.Save);
        Assert.Contains("Greet", designer.Database.StoredProcedures);   // not saved

        designer.RemoveTable("regions", delete: true);
        Assert.DoesNotContain("REGIONS", designer.Tables);
    }
}
