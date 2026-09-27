using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace JoePro.Ide;

/// <summary>The Data Session window: open work areas, record pointers and orders, with Browse/Close actions.</summary>
public sealed class DataSessionPanel : UserControl
{
    private readonly IdeSession _session;

    public DataSessionPanel(IdeSession session, Action<string> run)
    {
        _session = session;
        List = new ListBox { [Avalonia.Automation.AutomationProperties.NameProperty] = Strings.T("Work areas"), FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"), FontSize = 12 };
        List.DoubleTapped += (_, _) => { if (Selected is { } w) run($"SELECT {w.Number}\nBROWSE"); };
        var browse = new Button { Content = Strings.T("Browse") };
        browse.Click += (_, _) => { if (Selected is { } w) run($"SELECT {w.Number}\nBROWSE"); };
        var select = new Button { Content = Strings.T("Select") };
        select.Click += (_, _) => { if (Selected is { } w) run($"SELECT {w.Number}"); };
        var close = new Button { Content = Strings.T("Close") };
        close.Click += (_, _) => { if (Selected is { } w) run($"USE IN {w.Number}"); };
        var buttons = new WrapPanel { Margin = new Thickness(4), Children = { browse, select, close } };
        foreach (var b in buttons.Children.OfType<Button>()) b.Margin = new Thickness(2);
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(List);
        Content = dock;
        Refresh();
    }

    public ListBox List { get; }
    private WorkAreaInfo? Selected => List.SelectedItem as WorkAreaInfo;

    public void Refresh()
    {
        var keep = Selected?.Number;
        var items = _session.WorkAreas();
        List.ItemsSource = items;
        List.SelectedItem = items.FirstOrDefault(i => i.Number == keep) ?? items.FirstOrDefault(i => i.Selected);
    }
}
