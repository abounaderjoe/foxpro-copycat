using Avalonia.Headless.XUnit;
using JoePro.Runtime;
using JoePro.Ui.Runtime;

namespace JoePro.Ui.Tests;

public class BrowseModelTests
{
    [AvaloniaFact]
    public void Loads_rows_commits_edits_and_rejects_bad_values()
    {
        var dir = Path.Combine(Path.GetTempPath(), "joepro-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var rt = new Interpreter(new TextWriterOutput(TextWriter.Null), dir);
        rt.ExecuteCommand("""
            SET TALK OFF
            CREATE TABLE item (code C(5), price N(8,2), added D)
            INSERT INTO item VALUES ("A1", 9.99, {^2024-01-02})
            INSERT INTO item VALUES ("B2", 5, {^2024-02-03})
            DELETE FOR code = "B2"
            """);
        var model = new BrowseModel(rt, rt.Session.Current);
        model.Load();
        Assert.Equal(2, model.Rows.Count);
        Assert.Equal(["code", "price", "added"], model.Columns.Select(c => c.Header.ToLowerInvariant()));
        Assert.Equal("9.99", model.Rows[0][1]);
        Assert.True(model.Rows[1].Deleted);

        Assert.Null(model.Commit(0, 1, "12.5"));
        Assert.Equal(12.5, rt.Evaluate("item.price").AsNumber);
        Assert.Equal("12.50", model.Rows[0][1]);
        Assert.NotNull(model.Commit(0, 1, "abc"));
        Assert.Null(model.Commit(1, 2, "12/25/2024"));
        rt.ExecuteCommand("GO 2");
        Assert.Equal("20241225", rt.Evaluate("DTOS(added)").AsString);

        model.ToggleDelete(1);
        Assert.False(model.Rows[1].Deleted);
        model.AppendRow();
        Assert.Equal(3, model.Rows.Count);
        Assert.Equal(3, rt.Evaluate("RECCOUNT()").AsNumber);

        rt.ExecuteCommand("SET DELETED ON\nDELETE FOR code = 'A1'");
        model.Load();
        Assert.Equal(2, model.Rows.Count);
        rt.Session.Dispose();
    }
}
