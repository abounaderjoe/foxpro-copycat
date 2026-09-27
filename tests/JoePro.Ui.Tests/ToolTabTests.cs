using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using JoePro.Ide;

namespace JoePro.Ui.Tests;

/// <summary>MODIFY MEMO, MODIFY CONNECTION and HELP in the IDE.</summary>
public class ToolTabTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "joepro-tooltabs", Guid.NewGuid().ToString("N"))).FullName;
    private readonly IdeSession _session;
    private readonly MainWindow _window;

    public ToolTabTests()
    {
        _session = new IdeSession(_dir);
        _window = new MainWindow(_session);
        _window.Show();
        _session.Execute("SET TALK OFF");
    }

    public void Dispose()
    {
        _window.Close();
        _session.Dispose();
    }

    private T Active<T>() where T : DocumentTab
    {
        Dispatcher.UIThread.RunJobs();
        return Assert.IsType<T>(_window.Documents.SelectedItem);
    }

    [AvaloniaFact]
    public void Modify_memo_edits_the_current_record_and_saves_it_back()
    {
        _session.Execute("CREATE CURSOR notes (id I, body M)\nINSERT INTO notes VALUES (1, \"first\")\nINSERT INTO notes VALUES (2, \"second\")\nGO 1");
        _session.Execute("MODIFY MEMO body");
        var tab = Active<MemoEditorTab>();
        Assert.Equal("first", tab.Editor.Text);
        tab.Editor.Text = "changed";
        _session.Execute("GO 2");
        tab.Save();
        Assert.Equal(2, _session.Runtime.Evaluate("RECNO()").AsNumber); // the record pointer stays where the user put it
        Assert.False(tab.IsDirty);
        Assert.Equal("changed", EvalAt(1));
        Assert.Equal("second", EvalAt(2));
    }

    private string EvalAt(int rec)
    {
        _session.Execute($"GO {rec}");
        return _session.Runtime.Evaluate("notes.body").AsString;
    }

    [AvaloniaFact]
    public void Modify_connection_creates_and_renames_a_named_connection()
    {
        _session.Execute("CREATE DATABASE conns\nCREATE CONNECTION first CONNSTRING \"Provider=sqlite;Data Source=a.db\"");
        _session.Execute("MODIFY CONNECTION first");
        var tab = Active<ConnectionEditorTab>();
        tab.SetField("name", "renamed");
        tab.SetField("connstring", "Provider=sqlite;Data Source=b.db");
        tab.Save();
        Assert.Equal(".F. .T.", _session.Runtime.Evaluate("TRANSFORM(INDBC('first', 'CONNECTION')) + ' ' + TRANSFORM(INDBC('renamed', 'CONNECTION'))").AsString);
        Assert.Equal("Provider=sqlite;Data Source=b.db", _session.Runtime.Evaluate("DBGETPROP('renamed', 'CONNECTION', 'ConnectString')").AsString);
    }

    [AvaloniaFact]
    public void Help_opens_on_a_topic_and_searches()
    {
        _session.Execute("HELP alltrim");
        var tab = Active<HelpTab>();
        Assert.Contains("ALLTRIM(cExpression", tab.DetailText);
        _session.Execute("HELP SORT");
        Assert.Same(tab, Active<HelpTab>());
        Assert.Contains("SORT TO TableName", tab.DetailText);
    }
}
