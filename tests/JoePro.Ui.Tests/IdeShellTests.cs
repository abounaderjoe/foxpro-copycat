using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using JoePro.Ide;

namespace JoePro.Ui.Tests;

public class IdeShellTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "joepro-ide-tests", Guid.NewGuid().ToString("N"));
    private readonly IdeSession _session;
    private readonly MainWindow _window;

    public IdeShellTests()
    {
        Directory.CreateDirectory(_dir);
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

    private string ScreenText()
    {
        Dispatcher.UIThread.RunJobs();
        return _window.Screen.Box.Text ?? "";
    }

    private void TypeAndEnter(string line)
    {
        var doc = _window.CommandWindow.Editor.Document;
        var last = doc.GetLineByNumber(doc.LineCount);
        if (last.Length > 0) doc.Insert(doc.TextLength, "\n");
        doc.Insert(doc.TextLength, line);
        _window.CommandWindow.Editor.CaretOffset = doc.TextLength;
        if (!_window.CommandWindow.ExecuteAtCaret()) doc.Insert(doc.TextLength, "\n");
    }

    [AvaloniaFact]
    public void Command_window_executes_the_current_line()
    {
        TypeAndEnter("x = 20 + 22");
        TypeAndEnter("? x");
        Assert.Contains("        42", ScreenText());
        Assert.Equal(42, _session.Runtime.GetVariable("x").AsNumber);
        Assert.Contains("? x", _window.CommandWindow.Editor.Text);
    }

    [AvaloniaFact]
    public void Command_window_collects_block_statements()
    {
        TypeAndEnter("FOR i = 1 TO 3");
        TypeAndEnter("?? TRANSFORM(i)");
        Assert.DoesNotContain("123", ScreenText());
        TypeAndEnter("ENDFOR");
        Assert.Contains("123", ScreenText());
    }

    [AvaloniaFact]
    public void Errors_are_reported_on_the_screen()
    {
        TypeAndEnter("? nosuchvariable");
        Assert.Contains("Error 12: Variable 'NOSUCHVARIABLE' is not found.", ScreenText());
    }

    [AvaloniaFact]
    public void Data_session_panel_tracks_open_tables_and_browse_opens_a_grid()
    {
        _window.Run("CREATE TABLE people (name C(20), age I)");
        _window.Run("INSERT INTO people VALUES ('Ann', 31)");
        var items = _window.DataSession.List.ItemsSource!.Cast<WorkAreaInfo>().ToList();
        Assert.Contains(items, w => w.Alias == "PEOPLE" && w.RecordCount == 1);

        _window.Run("BROWSE");
        var browse = Assert.IsType<BrowseTab>(_window.Documents.SelectedItem);
        Assert.Single(browse.Model.Rows);
        Assert.Null(browse.Model.Commit(0, 1, "32"));
        Assert.Equal(32, _session.Runtime.Evaluate("people.age").AsNumber);
    }

    [AvaloniaFact]
    public void Modify_command_opens_the_editor_and_ctrl_e_runs_it()
    {
        _window.Run("MODIFY COMMAND hello");
        var editor = Assert.IsType<CodeEditorTab>(_window.Documents.SelectedItem);
        Assert.EndsWith("hello.prg", editor.FilePath);
        editor.Editor.Text = "? 'Hello from the editor'";
        Assert.True(editor.IsDirty);
        _window.RunActive();
        Assert.False(editor.IsDirty);
        Assert.True(File.Exists(Path.Combine(_dir, "hello.prg")));
        Assert.Contains("Hello from the editor", ScreenText());
    }

    [AvaloniaFact]
    public void Command_palette_runs_actions_and_commands()
    {
        _window.Palette.Open();
        _window.Palette.Input.Text = "? 6 * 7";
        _window.Palette.Accept();
        Assert.Contains("        42", ScreenText());

        _window.Palette.Open();
        _window.Palette.Input.Text = "new program";
        _window.Palette.Accept();
        Assert.IsType<CodeEditorTab>(_window.Documents.SelectedItem);
    }

    [AvaloniaFact]
    public void Clear_empties_the_screen()
    {
        _window.Run("? 'something'");
        Assert.Contains("something", ScreenText());
        _window.Run("CLEAR");
        Assert.DoesNotContain("something", ScreenText());
    }
}
