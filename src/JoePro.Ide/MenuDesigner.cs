using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using JoePro.Documents.Menus;

namespace JoePro.Ide;

/// <summary>A menu (.jpmenu) in the Menu Designer.</summary>
public sealed class MenuDesignerTab : DocumentTab
{
    public MenuDesignerTab(string? path, MenuDocument doc, IdeSession session, bool dark)
    {
        Designer = new MenuDesigner(path, doc, session, dark);
        Designer.Session.Changed += UpdateTitle;
        Designer.Saved += UpdateTitle;
        Content = Designer;
        UpdateTitle();
    }

    public MenuDesigner Designer { get; }
    private void UpdateTitle() => Title = (Designer.FilePath != null ? System.IO.Path.GetFileName(Designer.FilePath) : "menu1.jpmenu") + (Designer.Session.IsDirty ? " •" : "");
}

/// <summary>
/// The Menu Designer: the menu as a tree (pads, their menus, submenus), each item's prompt, action (command,
/// procedure, submenu or system bar), key, SKIP FOR, message and mark; general options (setup/cleanup code,
/// location); Quick Menu; a live preview in the IDE's application menu bar; and MPR code generation.
/// </summary>
public sealed class MenuDesigner : UserControl
{
    private readonly IdeSession _ide;
    private readonly TreeView _tree = new();
    private readonly StackPanel _props = new() { Margin = new Thickness(8) };
    private int[] _selected = [];
    private bool _loading;

    public static readonly string[] SystemBars =
    [
        "_MFI_OPEN", "_MFI_CLOSE", "_MFI_SAVE", "_MFI_SAVAS", "_MFI_PRINT", "_MFI_QUIT", "_MED_UNDO", "_MED_REDO", "_MED_CUT", "_MED_COPY",
        "_MED_PASTE", "_MED_CLEAR", "_MED_SLCTA", "_MED_FIND", "_MWI_ARRAN", "_MWI_CASCADE", "_MWI_CMD", "_MST_HELP", "_MST_ABOUT",
    ];

    public MenuDesigner(string? path, MenuDocument doc, IdeSession ide, bool dark)
    {
        _ide = ide;
        FilePath = path;
        Session = new MenuDesignSession(doc);
        Session.Changed += Rebuild;
        _tree.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _selected = _tree.SelectedItem is TreeViewItem { Tag: int[] p } ? p : [];
            ShowItem();
        };
        Button B(string text, string tip, Action act)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 2), FontSize = 12 };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) =>
            {
                try { act(); }
                catch (Exception ex) when (ex is JoePro.Core.VfpException or ArgumentException or IOException) { Status?.Invoke(ex.Message); }
            };
            return b;
        }
        var toolbar = new WrapPanel
        {
            Margin = new Thickness(4),
            Children =
            {
                B("Save", "Save (Ctrl+S)", () => { if (FilePath == null) SaveAsRequested?.Invoke(); else Save(); }),
                B("▶ Preview", "Show this menu in the application menu bar", Preview),
                B("End preview", "Restore the menu bar", EndPreview),
                B("Generate…", "Write the MPR program", () => { if (FilePath == null) SaveAsRequested?.Invoke(); else Generate(); }),
                B("↶", "Undo", Session.Undo), B("↷", "Redo", Session.Redo),
                B("+ Item", "Insert an item after the selected one", () => InsertItem("New item")),
                B("+ Separator", "Insert a separator line", () => InsertItem("\\-")),
                B("+ System bar", "Insert a system menu bar", () => InsertSystemBar("_MED_COPY")),
                B("Delete", "Delete the selected item", DeleteItem),
                B("▲", "Move up", () => MoveItem(-1)), B("▼", "Move down", () => MoveItem(1)),
                B("→ Indent", "Make the item part of the submenu of the item above", IndentItem),
                B("← Outdent", "Move the item out of its submenu", OutdentItem),
                B("Quick Menu", "Start from the standard File, Edit, Window and Help menus", QuickMenu),
                B("Options…", "Setup and cleanup code, location", () => MenuOptionsDialog.Show(this)),
            },
        };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("320,Auto,*") };
        body.Children.Add(new Border { Child = _tree, BorderThickness = new Thickness(0, 0, 1, 0), BorderBrush = Brushes.Gray });
        var split = new GridSplitter { Width = 5, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(split, 1);
        body.Children.Add(split);
        var scroller = new ScrollViewer { Content = _props };
        Grid.SetColumn(scroller, 2);
        body.Children.Add(scroller);
        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(body);
        Content = root;
        Rebuild();
    }

    public MenuDesignSession Session { get; }
    public string? FilePath { get; private set; }
    public IReadOnlyList<int> SelectedPath => _selected;
    public MenuNode? SelectedItem => Session.Get(_selected);
    public event Action<string>? Status;
    public event Action? Saved;
    public event Action? SaveAsRequested;

    // ---- Tree -------------------------------------------------------------------------------------

    private static string Label(MenuNode n) => n.IsSeparator ? "────────" : n.SystemBar != null && n.Prompt.Length == 0 ? $"[{n.SystemBar}]" : n.Caption
        + (n.KeyText is { Length: > 0 } k ? "    " + k : "") + (n.Items.Count > 0 ? "  ▸" : "");

    private void Rebuild()
    {
        _loading = true;
        try
        {
            _tree.Items.Clear();
            void Add(ItemsControl parent, List<MenuNode> items, int[] prefix)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    int[] path = [.. prefix, i];
                    var node = new TreeViewItem { Header = Label(items[i]), Tag = path, IsExpanded = true };
                    Add(node, items[i].Items, path);
                    parent.Items.Add(node);
                    if (path.SequenceEqual(_selected)) _tree.SelectedItem = node;
                }
            }
            Add(_tree, Session.Document.Items, []);
        }
        finally { _loading = false; }
        if (Session.Get(_selected) == null) _selected = [];
        ShowItem();
    }

    public void Select(IReadOnlyList<int> path)
    {
        _selected = path.ToArray();
        Rebuild();
    }

    // ---- Commands ----------------------------------------------------------------------------------

    /// <summary>Inserts an item after the selected one (at the end of the top level when nothing is selected).</summary>
    public int[] InsertItem(string prompt, string? command = null)
    {
        var node = new MenuNode { Prompt = prompt, Command = command };
        var path = _selected.Length == 0
            ? Session.Insert([], Session.Document.Items.Count, node)
            : Session.Insert(_selected[..^1], _selected[^1] + 1, node);
        Select(path);
        return path;
    }

    public int[] InsertSystemBar(string bar)
    {
        var node = new MenuNode { SystemBar = bar.ToUpperInvariant() };
        var path = _selected.Length == 0 ? Session.Insert([], Session.Document.Items.Count, node) : Session.Insert(_selected[..^1], _selected[^1] + 1, node);
        Select(path);
        return path;
    }

    /// <summary>Adds an item to the submenu of the selected item.</summary>
    public int[] AddSubItem(string prompt, string? command = null)
    {
        if (_selected.Length == 0) return InsertItem(prompt, command);
        var parent = Session.Get(_selected)!;
        var path = Session.Insert(_selected, parent.Items.Count, new MenuNode { Prompt = prompt, Command = command });
        Session.Update(_ => { parent.Command = null; parent.Procedure = null; parent.SystemBar = null; });
        Select(path);
        return path;
    }

    public void DeleteItem()
    {
        if (_selected.Length == 0) return;
        Session.Remove(_selected);
        _selected = [];
        Rebuild();
    }

    public void MoveItem(int delta) { if (_selected.Length > 0) Select(Session.Move(_selected, delta)); }
    public void IndentItem() { if (_selected.Length > 0) Select(Session.Indent(_selected)); }
    public void OutdentItem() { if (_selected.Length > 0) Select(Session.Outdent(_selected)); }

    public void QuickMenu()
    {
        var quick = MenuDesignSession.QuickMenu();
        Session.Update(d => { d.Items.Clear(); d.Items.AddRange(quick.Items); });
        _selected = [];
        Rebuild();
    }

    /// <summary>Changes the selected item (one undo step).</summary>
    public void UpdateItem(Action<MenuNode> change)
    {
        if (Session.Get(_selected) is not { } node) return;
        var keep = _selected;
        Session.Update(_ => change(node));
        _selected = keep;
    }

    /// <summary>The item's action: Command, Procedure, Submenu or Bar (system bar).</summary>
    public static string ResultOf(MenuNode n) => n.Items.Count > 0 ? "Submenu" : n.SystemBar != null ? "Bar" : n.Procedure != null ? "Procedure" : "Command";

    public void SetResult(string result) => UpdateItem(n =>
    {
        var text = n.Command ?? n.Procedure ?? "";
        n.Command = n.Procedure = null;
        if (result != "Bar") n.SystemBar = null;
        switch (result)
        {
            case "Command": n.Command = text.Split('\n')[0]; break;
            case "Procedure": n.Procedure = text.Length > 0 ? text : "\n"; break;
            case "Bar": n.SystemBar ??= "_MED_COPY"; n.Items.Clear(); break;
            case "Submenu": if (n.Items.Count == 0) n.Items.Add(new MenuNode { Prompt = "New item" }); break;
        }
    });

    // ---- Item properties ---------------------------------------------------------------------------

    private void ShowItem()
    {
        _props.Children.Clear();
        var n = Session.Get(_selected);
        if (n == null)
        {
            _props.Children.Add(new TextBlock { Text = Session.Document.Items.Count == 0 ? "Add items with + Item, or start from Quick Menu." : "Select an item to edit it.", Opacity = 0.7 });
            return;
        }
        Control Row(string label, Control editor)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), Margin = new Thickness(0, 3) };
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(editor, 1);
            g.Children.Add(editor);
            return g;
        }
        TextBox Text(string? value, Action<MenuNode, string?> set)
        {
            var box = new TextBox { Text = value ?? "" };
            var original = box.Text;
            void Commit() { if (box.Text != original) { original = box.Text; UpdateItem(x => set(x, string.IsNullOrEmpty(box.Text) ? null : box.Text)); } }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Commit(); e.Handled = true; } };
            return box;
        }
        _props.Children.Add(Row("Prompt", Text(n.Prompt, (x, v) => x.Prompt = v ?? "")));
        var result = new ComboBox { ItemsSource = new[] { "Command", "Procedure", "Submenu", "Bar" }, SelectedItem = ResultOf(n), HorizontalAlignment = HorizontalAlignment.Stretch };
        result.SelectionChanged += (_, _) => { if (result.SelectedItem is string r && r != ResultOf(Session.Get(_selected)!)) SetResult(r); };
        _props.Children.Add(Row("Result", result));
        switch (ResultOf(n))
        {
            case "Command":
                _props.Children.Add(Row("Command", Text(n.Command, (x, v) => x.Command = v)));
                break;
            case "Procedure":
            {
                var editor = new TextEditor
                {
                    Text = n.Procedure ?? "", Height = 180, ShowLineNumbers = true, SyntaxHighlighting = FoxProHighlighting.Get(false),
                    FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"), FontSize = 13,
                };
                editor.LostFocus += (_, _) => { if (editor.Text != (Session.Get(_selected)?.Procedure ?? "")) UpdateItem(x => x.Procedure = editor.Text); };
                _props.Children.Add(new TextBlock { Text = "Procedure", Margin = new Thickness(0, 6, 0, 2) });
                _props.Children.Add(new Border { Child = editor, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) });
                break;
            }
            case "Bar":
            {
                var bar = new ComboBox { ItemsSource = SystemBars, SelectedItem = n.SystemBar, HorizontalAlignment = HorizontalAlignment.Stretch };
                bar.SelectionChanged += (_, _) => { if (bar.SelectedItem is string b && b != Session.Get(_selected)?.SystemBar) UpdateItem(x => x.SystemBar = b); };
                _props.Children.Add(Row("System bar", bar));
                break;
            }
            case "Submenu":
                _props.Children.Add(Row("Submenu name", Text(n.Name, (x, v) => x.Name = v)));
                var add = new Button { Content = "+ Item in this submenu" };
                add.Click += (_, _) => AddSubItem("New item");
                _props.Children.Add(add);
                break;
        }
        if (_selected.Length == 1 && ResultOf(n) != "Submenu") _props.Children.Add(Row("Pad name", Text(n.Name, (x, v) => x.Name = v)));
        _props.Children.Add(Row("Key (CTRL+N)", Text(n.KeyName, (x, v) => x.KeyName = v?.ToUpperInvariant())));
        _props.Children.Add(Row("Key text", Text(n.KeyText, (x, v) => x.KeyText = v)));
        _props.Children.Add(Row("Skip for", Text(n.SkipFor, (x, v) => x.SkipFor = v)));
        _props.Children.Add(Row("Message", Text(n.Message, (x, v) => x.Message = v)));
        var mark = new CheckBox { Content = "Mark (check mark)", IsChecked = n.Mark };
        mark.IsCheckedChanged += (_, _) => UpdateItem(x => x.Mark = mark.IsChecked == true);
        _props.Children.Add(mark);
        _props.Children.Add(Row("Comment", Text(n.Comment, (x, v) => x.Comment = v)));
    }

    // ---- Files and preview -------------------------------------------------------------------------

    public void Save(string? path = null)
    {
        FilePath = path ?? FilePath ?? throw new InvalidOperationException("No file name.");
        Session.Document.Save(FilePath);
        Session.MarkSaved();
        Saved?.Invoke();
        Status?.Invoke($"Saved {System.IO.Path.GetFileName(FilePath)}.");
    }

    /// <summary>Writes name.mpr next to the menu (the program VFP's GENMENU would write).</summary>
    public string Generate()
    {
        var mpr = System.IO.Path.ChangeExtension(FilePath!, ".mpr");
        File.WriteAllText(mpr, MenuGenerator.Generate(Session.Document, FilePath!));
        Status?.Invoke($"Generated {System.IO.Path.GetFileName(mpr)}.");
        return mpr;
    }

    /// <summary>Runs the menu's code so it shows in the application menu bar (and its items work).</summary>
    public void Preview()
    {
        var code = MenuGenerator.Generate(Session.Document, FilePath ?? "preview.jpmenu");
        _ide.Execute(code);
        Status?.Invoke(Session.Document.Kind == MenuKind.Shortcut ? "Shortcut menu shown." : "The menu is shown under the IDE menu; End preview restores it.");
    }

    public void EndPreview() => _ide.Execute("SET SYSMENU TO DEFAULT");

    public void SetDark(bool dark) { }
}

/// <summary>General options of a menu: kind, location, setup and cleanup code.</summary>
public static class MenuOptionsDialog
{
    public static void Show(MenuDesigner d)
    {
        var doc = d.Session.Document;
        TextEditor Code(string? text) => new()
        {
            Text = text ?? "", Height = 120, ShowLineNumbers = true, SyntaxHighlighting = FoxProHighlighting.Get(false),
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"), FontSize = 13,
        };
        var kind = new ComboBox { ItemsSource = Enum.GetNames<MenuKind>(), SelectedItem = doc.Kind.ToString(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var location = new ComboBox { ItemsSource = Enum.GetNames<MenuLocation>(), SelectedItem = doc.Location.ToString(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var pad = new TextBox { Text = doc.LocationPad ?? "", Watermark = "_MEDIT (for Before/After)" };
        var setup = Code(doc.Setup);
        var cleanup = Code(doc.Cleanup);
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { ok, cancel } };
        var panel = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "Kind" }, kind, new TextBlock { Text = "Location", Margin = new Thickness(0, 6, 0, 0) }, location, pad,
                new TextBlock { Text = "Setup code", Margin = new Thickness(0, 6, 0, 0) }, new Border { Child = setup, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) },
                new TextBlock { Text = "Cleanup code", Margin = new Thickness(0, 6, 0, 0) }, new Border { Child = cleanup, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) },
                buttons,
            },
        };
        var dialog = new Window { Title = "Menu Options", Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new Border { Padding = new Thickness(12), Child = panel } };
        ok.Click += (_, _) =>
        {
            d.Session.Update(x =>
            {
                x.Kind = Enum.Parse<MenuKind>((string)kind.SelectedItem!);
                x.Location = Enum.Parse<MenuLocation>((string)location.SelectedItem!);
                x.LocationPad = string.IsNullOrWhiteSpace(pad.Text) ? null : pad.Text.Trim();
                x.Setup = string.IsNullOrWhiteSpace(setup.Text) ? null : setup.Text;
                x.Cleanup = string.IsNullOrWhiteSpace(cleanup.Text) ? null : cleanup.Text;
            });
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        if (TopLevel.GetTopLevel(d) is Window owner) _ = dialog.ShowDialog(owner); else dialog.Show();
    }
}
