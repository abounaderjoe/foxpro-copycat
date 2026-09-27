using Avalonia.Headless.XUnit;
using JoePro.Data;
using JoePro.Documents.Queries;
using JoePro.Ide;

namespace JoePro.Ui.Tests;

public class QueryDesignerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "joepro-qd-tests", Guid.NewGuid().ToString("N"));
    private readonly IdeSession _session;
    private readonly MainWindow _window;

    public QueryDesignerTests()
    {
        Directory.CreateDirectory(_dir);
        _session = new IdeSession(_dir);
        _window = new MainWindow(_session);
        _window.Show();
        _session.Execute("""
            SET TALK OFF
            CREATE DATABASE shop
            CREATE TABLE customer (custid I, name C(20), city C(15))
            CREATE TABLE orders (orderid I, custid I, total N(10,2))
            INSERT INTO customer VALUES (1, 'Acme', 'Boston')
            INSERT INTO customer VALUES (2, 'Globex', 'Chicago')
            INSERT INTO orders VALUES (1, 1, 100)
            INSERT INTO orders VALUES (2, 1, 250)
            INSERT INTO orders VALUES (3, 2, 75)
            CLOSE TABLES ALL
            """);
    }

    public void Dispose()
    {
        _window.Close();
        _session.Dispose();
    }

    [AvaloniaFact]
    public void Build_a_query_visually_edit_its_sql_run_and_save()
    {
        Assert.True(_session.Execute("MODIFY QUERY custtotals"));
        var designer = Assert.IsType<QueryDesignerTab>(_window.Documents.SelectedItem).Designer;
        designer.AddTable("customer");
        var orders = designer.AddTable("orders");
        Assert.Equal("orders.custid = customer.custid", orders.On);   // joined on the shared key
        Assert.Contains("name", designer.FieldsOf(designer.Document.Tables[0]));
        designer.AddField("customer.name");
        designer.AddField("SUM(orders.total)", "total");
        designer.Document.GroupBy.Add("customer.name");
        designer.AddOrder("2", descending: true);
        Assert.Contains("INNER JOIN orders ON orders.custid = customer.custid", designer.SqlText);
        Assert.Contains("GROUP BY customer.name", designer.SqlText);

        Assert.Equal(2, designer.RunPreview());
        Assert.StartsWith("Acme", designer.ResultLines[1]);
        Assert.Contains("350", designer.ResultLines[1]);

        // Editing the SQL pane updates the designer (two-way sync).
        Assert.True(designer.ApplySql(designer.SqlText.Replace(" ;\n   GROUP BY", " ;\n   WHERE customer.city = 'Boston' ;\n   GROUP BY")));
        Assert.Equal(("customer.city", "=", "'Boston'"), (designer.Document.Filters[0].Left, designer.Document.Filters[0].Op, designer.Document.Filters[0].Right));
        Assert.Equal(1, designer.RunPreview());

        // A UNION can only be edited as SQL.
        Assert.False(designer.ApplySql("SELECT name FROM customer UNION SELECT name FROM customer"));
        Assert.True(designer.Document.IsSqlOnly);
        Assert.Contains("UNION", designer.Banner);
        Assert.Equal(2, designer.RunPreview());
        designer.ApplySql("SELECT customer.name FROM customer INTO CURSOR names");
        var messages = new List<string>();
        designer.Status += messages.Add;
        Assert.True(designer.Save(), designer.FilePath + " | " + string.Join("; ", messages));
        var path = Path.Combine(_dir, "custtotals.jpquery");
        Assert.True(File.Exists(path));
        Assert.Equal(QueryDestination.Cursor, QueryDocument.Load(path).Destination);
        _session.Execute("DO custtotals.jpquery");
        Assert.Equal(2, _session.Runtime.Evaluate("RECCOUNT('names')").AsNumber);
    }

    [AvaloniaFact]
    public void View_designer_saves_update_criteria_and_the_view_updates_its_table()
    {
        _session.Execute("OPEN DATABASE shop");
        Assert.True(_session.Execute("CREATE VIEW"));
        var designer = Assert.IsType<QueryDesignerTab>(_window.Documents.SelectedItem).Designer;
        Assert.True(designer.IsView);
        designer.AddTable("customer");
        designer.AddField("customer.custid");
        designer.AddField("customer.name");
        Assert.False(designer.Save());   // needs a name
        designer.ViewName = "custview";
        designer.SendUpdates = true;
        designer.KeyFields.Add("custid");
        designer.UpdatableFields.Add("name");
        Assert.True(designer.Save());

        var view = _session.Runtime.Session.CurrentDatabase!.GetView("custview")!;
        Assert.True(view.Get("SendUpdates").AsBool);
        Assert.True(view.GetField("custid", "KeyField").AsBool);
        _session.Execute("USE custview\nLOCATE FOR custid = 2\nREPLACE name WITH 'Initech'\n=TABLEUPDATE(.T.)\nUSE");
        _session.Execute("SELECT name FROM customer WHERE custid = 2 INTO CURSOR q");
        Assert.Equal("Initech", _session.Runtime.Evaluate("TRIM(q.name)").AsString);

        // MODIFY VIEW reopens it with its criteria.
        _window.CloseDocument((QueryDesignerTab)_window.Documents.SelectedItem!);
        Assert.True(_session.Execute("MODIFY VIEW custview"));
        var again = Assert.IsType<QueryDesignerTab>(_window.Documents.SelectedItem).Designer;
        Assert.Contains("custid", again.KeyFields);
        Assert.Equal(["custid", "name"], again.OutputColumns());
    }

    [AvaloniaFact]
    public void Legacy_qpr_opens_converted()
    {
        File.WriteAllText(Path.Combine(_dir, "boston.qpr"), "SELECT * ;\n FROM customer ;\n WHERE city = 'Boston'\n");
        _window.OpenAny(Path.Combine(_dir, "boston.qpr"));
        var designer = Assert.IsType<QueryDesignerTab>(_window.Documents.SelectedItem).Designer;
        Assert.EndsWith("boston.jpquery", designer.FilePath);
        Assert.Single(designer.Document.Filters);
        Assert.Equal(1, designer.RunPreview());
    }
}
