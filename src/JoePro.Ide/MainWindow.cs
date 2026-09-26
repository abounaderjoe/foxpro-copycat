using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using JoePro.Runtime;
using JoePro.Ui.Runtime;

namespace JoePro.Ide;

/// <summary>
/// The Joe Pro IDE shell: menu, Data Session panel, tabbed documents (Screen, code editors, Browse
/// windows), the Command Window and a status bar. Panels are resizable; keyboard shortcuts follow
/// FoxPro where one exists (Ctrl+F2 Command Window, Ctrl+E run).
/// </summary>
public sealed class MainWindow : Window
{
    private readonly IdeSession _session;
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _position = new() { Margin = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
    private readonly List<PaletteAction> _actions = new();

    public MainWindow(IdeSession session)
    {
        _session = session;
        Title = "Joe Pro";
        Width = 1280;
        Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Documents = new TabControl { Padding = new Thickness(0) };
        Screen = new ScreenTab(session.Screen);
        Documents.Items.Add(Screen);
        CommandWindow = new CommandWindow(session);
        DataSession = new DataSessionPanel(session, Run);

        session.Host.Owner = this;
        session.Host.BrowseHandler = model => { OpenDocument(new BrowseTab(model, session.Host, AfterCommand)); return true; };
        session.Host.ModifyHandler = path => { OpenFile(path); return true; };
        session.CommandCompleted += AfterCommand;
        session.StatusMessage += m => _status.Text = m;
        session.ErrorRaised += ex => _status.Text = $"Error {ex.Number}: {ex.Message}";
        session.QuitRequested += Close;

        BuildActions();
        Palette = new CommandPalette(_actions, Run);

        var commandPane = new DockPanel();
        var commandHeader = new TextBlock { Text = "Command", FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 4) };
        DockPanel.SetDock(commandHeader, Dock.Top);
        commandPane.Children.Add(commandHeader);
        commandPane.Children.Add(CommandWindow);

        // Center: documents over the Command Window, separated by a splitter.
        var center = new Grid { RowDefinitions = new RowDefinitions("3*,Auto,2*") };
        center.Children.Add(Documents);
        var hSplit = new GridSplitter { Height = 5, ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(hSplit, 1);
        center.Children.Add(hSplit);
        Grid.SetRow(commandPane, 2);
        center.Children.Add(commandPane);

        // Left: Data Session panel.
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("260,Auto,*") };
        body.Children.Add(DataSession);
        var vSplit = new GridSplitter { Width = 5, ResizeDirection = GridResizeDirection.Columns, VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetColumn(vSplit, 1);
        body.Children.Add(vSplit);
        Grid.SetColumn(center, 2);
        body.Children.Add(center);

        var statusBar = new DockPanel { Children = { _position, _status } };
        DockPanel.SetDock(_position, Dock.Right);
        var statusBorder = new Border { Child = statusBar, BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Brushes.Gray, Padding = new Thickness(0, 2) };

        var root = new DockPanel();
        var menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        root.Children.Add(menu);
        root.Children.Add(statusBorder);
        root.Children.Add(body);
        Content = new Panel { Children = { root, Palette } };

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
        Opened += (_, _) =>
        {
            ApplyTheme();
            CommandWindow.Editor.Focus();
        };
        Closing += (_, e) =>
        {
            var dirty = Documents.Items.OfType<CodeEditorTab>().Where(t => t.IsDirty).ToList();
            foreach (var t in dirty.Where(t => t.FilePath != null)) t.Save();
        };
        session.Screen.Write($"{Interpreter.VersionString}. Type commands in the Command window below; Ctrl+Shift+P opens the command palette.");
        session.Screen.NewLine();
        _status.Text = "Ready";
        AfterCommand();
    }

    public TabControl Documents { get; }
    public ScreenTab Screen { get; }
    public CommandWindow CommandWindow { get; }
    public DataSessionPanel DataSession { get; }
    public CommandPalette Palette { get; }

    private bool IsDark => ActualThemeVariant == ThemeVariant.Dark;

    private void ApplyTheme()
    {
        CommandWindow.SetDark(IsDark);
        foreach (var t in Documents.Items.OfType<CodeEditorTab>()) t.SetDark(IsDark);
    }

    public void Run(string command)
    {
        CommandWindow.Run(command);
    }

    private void AfterCommand()
    {
        DataSession.Refresh();
        var wa = _session.Runtime.Session.Current;
        _position.Text = wa.InUse
            ? $"{wa.Alias}  Record {(wa.Eof ? "EOF" : wa.RecNo.ToString())}/{wa.RecordCount}{(wa.Order != null ? "  Order " + wa.Order.Name : "")}  ·  Session {_session.Runtime.Session.Id}"
            : $"No table in work area {wa.Number}  ·  Session {_session.Runtime.Session.Id}";
    }

    // ---- Documents -------------------------------------------------------------------

    public void OpenDocument(DocumentTab tab)
    {
        tab.CloseRequested += CloseDocument;
        Documents.Items.Add(tab);
        Documents.SelectedItem = tab;
    }

    public void CloseDocument(DocumentTab tab)
    {
        if (tab is CodeEditorTab { IsDirty: true, FilePath: not null } code) code.Save();
        Documents.Items.Remove(tab);
    }

    public CodeEditorTab OpenFile(string path)
    {
        var existing = Documents.Items.OfType<CodeEditorTab>().FirstOrDefault(t => t.FilePath != null && string.Equals(Path.GetFullPath(t.FilePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            Documents.SelectedItem = existing;
            return existing;
        }
        var tab = new CodeEditorTab(path, IsDark);
        OpenDocument(tab);
        tab.Editor.Focus();
        return tab;
    }

    private CodeEditorTab? ActiveEditor => Documents.SelectedItem as CodeEditorTab;

    /// <summary>Saves the active program and runs it (DO for .prg, DO FORM for .jpform).</summary>
    public void RunActive()
    {
        if (ActiveEditor is not { } tab) return;
        if (tab.FilePath == null) { _ = SaveAs(tab); return; }
        tab.Save();
        var cmd = tab.FilePath.EndsWith(".jpform", StringComparison.OrdinalIgnoreCase) ? "DO FORM" : "DO";
        Run($"{cmd} \"{tab.FilePath}\"");
    }

    private async Task SaveAs(CodeEditorTab tab)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save program",
            SuggestedFileName = "program.prg",
            DefaultExtension = "prg",
            FileTypeChoices = [new FilePickerFileType("FoxPro program") { Patterns = ["*.prg"] }, new FilePickerFileType("Joe Pro form") { Patterns = ["*.jpform"] }],
        });
        if (file?.TryGetLocalPath() is { } path) tab.Save(path);
    }

    private async Task OpenWithPicker()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("FoxPro and Joe Pro files") { Patterns = ["*.prg", "*.jpform", "*.h", "*.jpt", "*.jpdb", "*.dbf", "*.txt"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) OpenAny(path);
    }

    /// <summary>Opens a file the way VFP would: programs in the editor, tables with USE + BROWSE, databases with OPEN DATABASE.</summary>
    public void OpenAny(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".jpt" or ".dbf":
                Run($"USE \"{path}\" IN 0\nBROWSE");
                break;
            case ".jpdb":
                Run($"OPEN DATABASE \"{path}\"");
                break;
            default:
                OpenFile(path);
                break;
        }
    }

    private async Task ImportFolder()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Import a FoxPro application folder" });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
        var target = path.TrimEnd(Path.DirectorySeparatorChar) + "-joepro";
        Run($"IMPORT FOXPRO \"{path}\" TO \"{target}\"");
        var report = Path.Combine(target, "migration-report.html");
        if (File.Exists(report)) await Launcher.LaunchFileInfoAsync(new FileInfo(report));
    }

    // ---- Menu, shortcuts, palette ------------------------------------------------------

    private void BuildActions()
    {
        void A(string title, string shortcut, Action run) => _actions.Add(new PaletteAction(title, shortcut, run));
        A("New program", "Ctrl+N", () => OpenDocument(new CodeEditorTab(null, IsDark)));
        A("Open…", "Ctrl+O", () => _ = OpenWithPicker());
        A("Save", "Ctrl+S", () => { if (ActiveEditor is { } t) { if (t.FilePath == null) _ = SaveAs(t); else t.Save(); } });
        A("Run program", "Ctrl+E", RunActive);
        A("Import FoxPro application…", "", () => _ = ImportFolder());
        A("Command window", "Ctrl+F2", () => CommandWindow.Editor.Focus());
        A("Close document", "Ctrl+W", () => { if (Documents.SelectedItem is DocumentTab d) CloseDocument(d); });
        A("Browse current table", "", () => Run("BROWSE"));
        A("Display structure", "", () => Run("DISPLAY STRUCTURE"));
        A("Close all tables", "", () => Run("CLOSE TABLES"));
        A("Clear screen", "", () => Run("CLEAR"));
        A("Theme: light", "", () => SetTheme(ThemeVariant.Light));
        A("Theme: dark", "", () => SetTheme(ThemeVariant.Dark));
        A("Theme: follow system", "", () => SetTheme(ThemeVariant.Default));
        A("About Joe Pro", "", () => Run("? VERSION()"));
    }

    private static void SetTheme(ThemeVariant v)
    {
        if (Application.Current != null) Application.Current.RequestedThemeVariant = v;
    }

    private PaletteAction Action(string title) => _actions.First(a => a.Title == title);

    private Menu BuildMenu()
    {
        MenuItem Item(string header, string? action = null, string? gesture = null, Action? run = null)
        {
            var mi = new MenuItem { Header = header };
            if (gesture != null) mi.InputGesture = KeyGesture.Parse(gesture);
            var r = run ?? (action != null ? Action(action).Run : null);
            if (r != null) mi.Click += (_, _) => r();
            return mi;
        }
        return new Menu
        {
            Items =
            {
                new MenuItem { Header = "_File", Items =
                {
                    Item("_New Program", "New program", "Ctrl+N"),
                    Item("_Open…", "Open…", "Ctrl+O"),
                    Item("_Save", "Save", "Ctrl+S"),
                    new Separator(),
                    Item("_Import FoxPro Application…", "Import FoxPro application…"),
                    new Separator(),
                    Item("E_xit", run: Close),
                } },
                new MenuItem { Header = "_View", Items =
                {
                    Item("_Command Window", "Command window", "Ctrl+F2"),
                    Item("Command _Palette", gesture: "Ctrl+Shift+P", run: () => Palette.Open()),
                    new Separator(),
                    Item("_Light Theme", "Theme: light"),
                    Item("_Dark Theme", "Theme: dark"),
                    Item("_System Theme", "Theme: follow system"),
                } },
                new MenuItem { Header = "_Table", Items =
                {
                    Item("_Browse", "Browse current table"),
                    Item("Display _Structure", "Display structure"),
                    Item("_Close All Tables", "Close all tables"),
                } },
                new MenuItem { Header = "_Program", Items =
                {
                    Item("_Run", "Run program", "Ctrl+E"),
                    Item("Clear _Screen", "Clear screen"),
                } },
                new MenuItem { Header = "_Help", Items = { Item("_About Joe Pro", "About Joe Pro") } },
            },
        };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        Action? act = (ctrl, shift, e.Key) switch
        {
            (true, true, Key.P) => () => Palette.Open(),
            (true, false, Key.N) => Action("New program").Run,
            (true, false, Key.O) => Action("Open…").Run,
            (true, false, Key.S) => Action("Save").Run,
            (true, false, Key.E) => RunActive,
            (false, false, Key.F5) => RunActive,
            (true, false, Key.F2) => () => CommandWindow.Editor.Focus(),
            (true, false, Key.W) => Action("Close document").Run,
            _ => null,
        };
        if (act == null) return;
        e.Handled = true;
        act();
    }
}
