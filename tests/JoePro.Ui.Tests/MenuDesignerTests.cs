using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using JoePro.Documents.Menus;
using JoePro.Ide;

namespace JoePro.Ui.Tests;

public class MenuDesignerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "joepro-menu-designer-tests", Guid.NewGuid().ToString("N"));
    private readonly IdeSession _session;
    private readonly MainWindow _window;

    public MenuDesignerTests()
    {
        Directory.CreateDirectory(_dir);
        _session = new IdeSession(_dir);
        _window = new MainWindow(_session);
        _window.Show();
        _session.Execute("SET TALK OFF\nPUBLIC gcLog\ngcLog = ''");
    }

    public void Dispose()
    {
        _window.Close();
        _session.Dispose();
    }

    [AvaloniaFact]
    public void Build_a_menu_preview_it_in_the_app_menu_bar_and_choose_items()
    {
        _session.Execute("CREATE MENU main");
        var d = Assert.IsType<MenuDesignerTab>(_window.Documents.SelectedItem).Designer;
        Assert.Equal(Path.Combine(_dir, "main.jpmenu"), d.FilePath);
        d.InsertItem("\\<File");
        d.AddSubItem("\\<Hello", "gcLog = gcLog + 'hello;'");
        d.UpdateItem(n => { n.KeyName = "CTRL+H"; n.KeyText = "Ctrl+H"; });
        d.InsertItem("\\-");
        d.InsertSystemBar("_MED_COPY");
        d.Select([0]);
        d.InsertItem("\\<About", "gcLog = gcLog + 'about;'");
        var doc = d.Session.Document;
        Assert.Equal(["File", "About"], doc.Items.Select(i => i.Caption));
        Assert.Equal(3, doc.Items[0].Items.Count);
        Assert.Equal("Submenu", MenuDesigner.ResultOf(doc.Items[0]));

        d.Preview();
        Dispatcher.UIThread.RunJobs();
        Assert.True(_window.AppMenu.IsVisible);
        var file = _window.AppMenu.Items.OfType<MenuItem>().First();
        Assert.Equal("_File", file.Header);
        var hello = file.Items.OfType<MenuItem>().First();
        Assert.Equal("Ctrl+H", hello.InputGesture?.ToString());
        hello.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        var about = _window.AppMenu.Items.OfType<MenuItem>().Last();
        about.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("hello;about;", _session.Runtime.Evaluate("gcLog").AsString);
        d.EndPreview();
        Dispatcher.UIThread.RunJobs();
        Assert.False(_window.AppMenu.IsVisible);

        d.Save();
        var mpr = d.Generate();
        Assert.Contains("DEFINE PAD", File.ReadAllText(mpr));
        Assert.Equal(MenuSerializer.Write(doc), File.ReadAllText(Path.Combine(_dir, "main.jpmenu")));
        _session.Execute("DO main.mpr");
        Dispatcher.UIThread.RunJobs();
        Assert.True(_window.AppMenu.IsVisible);
    }

    [AvaloniaFact]
    public void Shortcut_popups_choose_an_item()
    {
        _session.Host.PopupChooser = p => p.Bars[1].Id;
        _session.Execute("""
            DEFINE POPUP ctx SHORTCUT RELATIVE FROM MROW(), MCOL()
            DEFINE BAR 1 OF ctx PROMPT "One"
            DEFINE BAR 2 OF ctx PROMPT "Two"
            ON SELECTION POPUP ctx gcLog = gcLog + PROMPT()
            ACTIVATE POPUP ctx
            """);
        Assert.Equal("Two", _session.Runtime.Evaluate("gcLog").AsString);
    }
}
