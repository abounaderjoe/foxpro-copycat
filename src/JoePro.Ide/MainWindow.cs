using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using JoePro.Core;
using JoePro.Runtime;
using DebugAction = JoePro.Runtime.DebugAction;
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
        Debugger = new IdeDebugger(this, session);
        Language = new JoePro.Tooling.LanguageService(session.Runtime,
            () => Directory.Exists(session.Runtime.Options.Default_) ? Directory.EnumerateFiles(session.Runtime.Options.Default_, "*.prg", SearchOption.AllDirectories).Take(2000) : []);

        session.Host.Owner = this;
        session.Host.BrowseHandler = model => { OpenDocument(new BrowseTab(model, session.Host, AfterCommand)); return true; };
        session.Host.ModifyHandler = path => { OpenAny(path); return true; };
        session.Host.DesignerHandler = OpenDesigner;
        session.Reports.PreviewHandler = (report, _) => { OpenPreview(report); return true; };
        session.CommandCompleted += AfterCommand;
        session.StatusMessage += m => _status.Text = m;
        session.ErrorRaised += ex => _status.Text = $"Error {ex.Number}: {ex.Message}";
        session.QuitRequested += Close;

        BuildActions();
        Palette = new CommandPalette(_actions, Run);

        var commandDock = new DockPanel();
        var commandHeader = new TextBlock { Text = "Command", FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 4) };
        DockPanel.SetDock(commandHeader, Dock.Top);
        commandDock.Children.Add(commandHeader);
        commandDock.Children.Add(CommandWindow);
        // Bottom: Command Window beside the debugger panes.
        var commandPane = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,Auto,2*") };
        commandPane.Children.Add(commandDock);
        var bottomSplit = new GridSplitter { Width = 5, ResizeDirection = GridResizeDirection.Columns, VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetColumn(bottomSplit, 1);
        commandPane.Children.Add(bottomSplit);
        Grid.SetColumn(Debugger.Panel, 2);
        commandPane.Children.Add(Debugger.Panel);

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
        // The running program's menu bar (DEFINE MENU / _MSYSMENU) sits under the IDE's own menu.
        AppMenu.Background = new SolidColorBrush(Color.FromArgb(30, 30, 110, 200));
        AppMenu.IsVisible = false;
        DockPanel.SetDock(AppMenu, Dock.Top);
        session.Host.MenusUpdated += RebuildAppMenu;
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        root.Children.Add(menu);
        root.Children.Add(AppMenu);
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
            foreach (var d in Documents.Items.OfType<FormDesignerTab>().Where(d => d.Designer.Session.IsDirty && d.Designer.FilePath != null).ToList()) d.Designer.Save();
            foreach (var r in Documents.Items.OfType<ReportDesignerTab>().Where(r => r.Designer.Session.IsDirty && r.Designer.FilePath != null).ToList()) r.Designer.Save();
        };
        session.Screen.Write($"{Interpreter.VersionString}. Type commands in the Command window below; Ctrl+Shift+P opens the command palette.");
        session.Screen.NewLine();
        _status.Text = "Ready";
        AfterCommand();
    }

    public TabControl Documents { get; }
    /// <summary>The application menu bar of the running program (hidden when it has none).</summary>
    public Menu AppMenu { get; } = new();

    private void RebuildAppMenu()
    {
        AppMenu.Items.Clear();
        foreach (var item in _session.Host.ActiveMenuItems()) AppMenu.Items.Add(item);
        AppMenu.IsVisible = AppMenu.Items.Count > 0;
    }
    public ScreenTab Screen { get; }
    public CommandWindow CommandWindow { get; }
    public DataSessionPanel DataSession { get; }
    public CommandPalette Palette { get; }
    public IdeDebugger Debugger { get; }
    public JoePro.Tooling.LanguageService Language { get; }

    public void SetStatus(string text) => _status.Text = text;

    /// <summary>Opens (if needed) the file where execution stopped and highlights the line.</summary>
    public void ShowExecutionLine(string? file, int line)
    {
        ClearExecutionLine();
        if (file == null || !File.Exists(file)) return;
        var tab = OpenFile(file);
        tab.ShowExecutionLine(line);
    }

    public void ClearExecutionLine()
    {
        foreach (var t in Documents.Items.OfType<CodeEditorTab>()) t.ShowExecutionLine(0);
    }

    /// <summary>Opens a file (or the active one) at a line: go-to-definition results.</summary>
    public void NavigateTo(JoePro.Tooling.SymbolLocation loc)
    {
        var tab = loc.File != null ? OpenFile(loc.File) : ActiveEditor;
        if (tab == null) return;
        var line = Math.Clamp(loc.Line, 1, tab.Editor.Document.LineCount);
        tab.Editor.TextArea.Caret.Line = line;
        tab.Editor.TextArea.Caret.Column = loc.Column;
        tab.Editor.ScrollToLine(line);
        tab.Editor.Focus();
    }

    private void ToggleBreakpoint()
    {
        if (ActiveEditor is not { FilePath: { } path } tab) { SetStatus("Save the program before setting breakpoints."); return; }
        var line = tab.Editor.TextArea.Caret.Line;
        var on = Debugger.Engine.ToggleLineBreakpoint(path, line);
        tab.Breakpoints?.InvalidateVisual();
        SetStatus(on ? $"Breakpoint set at line {line}." : $"Breakpoint cleared at line {line}.");
    }

    private bool IsDark => ActualThemeVariant == ThemeVariant.Dark;

    private void ApplyTheme()
    {
        CommandWindow.SetDark(IsDark);
        foreach (var t in Documents.Items.OfType<CodeEditorTab>()) t.SetDark(IsDark);
        foreach (var d in Documents.Items.OfType<FormDesignerTab>()) d.Designer.SetDark(IsDark);
        foreach (var b in Documents.Items.OfType<ClassBrowserTab>()) b.Browser.SetDark(IsDark);
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
        if (tab is FormDesignerTab { Designer: { FilePath: not null, Session.IsDirty: true } designer }) designer.Save();
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
        var tab = new CodeEditorTab(path, IsDark, Debugger.Engine, Language);
        if (tab.Intelligence != null) tab.Intelligence.NavigateRequested += NavigateTo;
        OpenDocument(tab);
        tab.Editor.Focus();
        return tab;
    }

    private CodeEditorTab? ActiveEditor => Documents.SelectedItem as CodeEditorTab;
    private FormDesigner? ActiveDesigner => (Documents.SelectedItem as FormDesignerTab)?.Designer;

    /// <summary>CREATE/MODIFY FORM (and the other designers as they arrive). Returns false for designers this build lacks.</summary>
    public bool OpenDesigner(DesignerRequest request)
    {
        switch (request.Kind)
        {
            case "FORM":
                OpenForm(request.Path.Length == 0 ? null : request.Path, request.Create, request.BaseClass, request.BaseLibrary);
                return true;
            case "CLASS" when request.ClassName != null:
                OpenClass(request.Path, request.ClassName);
                return true;
            case "CLASSLIB" when request.Path.Length > 0:
                OpenClassBrowser(request.Path);
                return true;
            case "REPORT" or "LABEL":
                OpenReport(request.Path.Length == 0 ? null : request.Path, request.Kind == "LABEL");
                return true;
            case "MENU":
                OpenMenu(request.Path.Length == 0 ? null : request.Path);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Opens a form in the Form Designer: a .jpform as is, a legacy .scx converted in memory, or a new form.</summary>
    public FormDesignerTab OpenForm(string? path, bool create = false, string? baseClass = null, string? baseLibrary = null)
    {
        var target = path != null && Path.GetExtension(path).Equals(".scx", StringComparison.OrdinalIgnoreCase) ? Path.ChangeExtension(path, ".jpform") : path;
        var existing = Documents.Items.OfType<FormDesignerTab>().FirstOrDefault(t => t.Designer.FilePath != null && target != null
            && string.Equals(Path.GetFullPath(t.Designer.FilePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            Documents.SelectedItem = existing;
            return existing;
        }
        JoePro.Documents.ClassFile file;
        string? note = null;
        if (path != null && File.Exists(path) && !(create && target == path && baseClass != null))
        {
            if (target != path)
            {
                var converted = JoePro.Documents.LegacyFormConverter.ConvertForm(path);
                file = converted.File;
                var review = converted.Findings.Count(f => f.Status is JoePro.Documents.FindingStatus.NeedsReview or JoePro.Documents.FindingStatus.Unsupported);
                note = $"{Path.GetFileName(path)} was converted; saving writes {Path.GetFileName(target)}" + (review > 0 ? $" ({review} item(s) need review)." : ".");
            }
            else file = JoePro.Documents.ClassFileReader.Load(path);
        }
        else
        {
            var name = path != null ? Path.GetFileNameWithoutExtension(path) : "Form1";
            file = JoePro.Documents.Design.DesignSession.NewForm(name);
            if (baseClass != null)
            {
                file.Classes[0].ParentClass = baseClass;
                if (baseLibrary != null)
                    file.Classes[0].ParentLibrary = target != null ? Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(target))!, baseLibrary).Replace('\\', '/') : baseLibrary;
            }
        }
        var tab = new FormDesignerTab(target, file, _session.Runtime.Options.Default_, IsDark);
        WireDesigner(tab.Designer);
        OpenDocument(tab);
        if (note != null) SetStatus(note);
        return tab;
    }

    private void WireDesigner(FormDesigner designer)
    {
        designer.Status += SetStatus;
        designer.RunRequested += Run;
        designer.SaveAsRequested += () => _ = SaveDesignerAs(designer);
        designer.Saved += () =>
        {
            if (designer.IsClass && designer.FilePath != null)
            {
                _session.Runtime.InvalidateClassLibrary(designer.FilePath);
                foreach (var b in Documents.Items.OfType<ClassBrowserTab>().Where(b => SamePath(b.Browser.LibraryPath, designer.FilePath))) b.Browser.Reload();
            }
        };
    }

    private MenuDesigner? ActiveMenuDesigner => (Documents.SelectedItem as MenuDesignerTab)?.Designer;

    /// <summary>CREATE/MODIFY MENU: a .jpmenu as is, a legacy .mnx converted (saving writes .jpmenu), or a new menu.</summary>
    public MenuDesignerTab OpenMenu(string? path)
    {
        var legacy = path != null && System.IO.Path.GetExtension(path).Equals(".mnx", StringComparison.OrdinalIgnoreCase);
        var target = legacy ? System.IO.Path.ChangeExtension(path!, ".jpmenu") : path;
        var existing = Documents.Items.OfType<MenuDesignerTab>().FirstOrDefault(t => t.Designer.FilePath != null && target != null && SamePath(t.Designer.FilePath, target));
        if (existing != null) { Documents.SelectedItem = existing; return existing; }
        var doc = path != null && File.Exists(path)
            ? legacy ? JoePro.Documents.Menus.LegacyMenuConverter.Convert(path).Document : JoePro.Documents.Menus.MenuDocument.Load(path)
            : new JoePro.Documents.Menus.MenuDocument();
        var tab = new MenuDesignerTab(target, doc, _session, IsDark);
        tab.Designer.Status += SetStatus;
        tab.Designer.SaveAsRequested += () => _ = SaveMenuAs(tab.Designer);
        OpenDocument(tab);
        if (legacy) SetStatus($"{System.IO.Path.GetFileName(path)} was converted; saving writes {System.IO.Path.GetFileName(target)}.");
        return tab;
    }

    private async Task SaveMenuAs(MenuDesigner designer)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save menu", SuggestedFileName = "menu1.jpmenu", DefaultExtension = "jpmenu",
            FileTypeChoices = [new FilePickerFileType("Joe Pro menu") { Patterns = ["*.jpmenu"] }],
        });
        if (file?.TryGetLocalPath() is { } path) designer.Save(path);
    }

    public ReportPreviewTab OpenPreview(JoePro.Reports.RenderedReport report)
    {
        var tab = new ReportPreviewTab(report, _session.Reports);
        tab.Preview.Status += SetStatus;
        OpenDocument(tab);
        return tab;
    }

    private ReportDesigner? ActiveReportDesigner => (Documents.SelectedItem as ReportDesignerTab)?.Designer;

    /// <summary>CREATE/MODIFY REPORT or LABEL: a .jpreport/.jplabel as is, a legacy .frx/.lbx converted, or a new layout.</summary>
    public ReportDesignerTab? OpenReport(string? path, bool label = false)
    {
        var legacy = path != null && System.IO.Path.GetExtension(path).ToLowerInvariant() is ".frx" or ".lbx";
        if (path != null) label = System.IO.Path.GetExtension(path).ToLowerInvariant() is ".jplabel" or ".lbx";
        var target = legacy ? System.IO.Path.ChangeExtension(path!, label ? ".jplabel" : ".jpreport") : path;
        var existing = Documents.Items.OfType<ReportDesignerTab>().FirstOrDefault(t => t.Designer.FilePath != null && target != null && SamePath(t.Designer.FilePath, target));
        if (existing != null) { Documents.SelectedItem = existing; return existing; }
        JoePro.Documents.Reports.ReportDocument doc;
        string? note = null;
        if (path != null && File.Exists(path))
        {
            doc = JoePro.Reports.ReportEngine.LoadDocument(path, out var conversion);
            if (conversion != null)
            {
                var review = conversion.Findings.Count(f => f.Status is JoePro.Documents.FindingStatus.NeedsReview or JoePro.Documents.FindingStatus.Unsupported);
                note = $"{System.IO.Path.GetFileName(path)} was converted; saving writes {System.IO.Path.GetFileName(target)}" + (review > 0 ? $" ({review} item(s) need review)." : ".");
            }
        }
        else if (label)
        {
            // A new label: choose the sheet layout first.
            ReportDialogs.LabelLayout(this, preset => Show(JoePro.Documents.Reports.LabelPresets.Create(preset)));
            return null;
        }
        else doc = JoePro.Documents.Reports.ReportDocument.NewReport();
        var tab = Show(doc);
        if (note != null) SetStatus(note);
        return tab;

        ReportDesignerTab Show(JoePro.Documents.Reports.ReportDocument d)
        {
            var t = new ReportDesignerTab(target, d, _session, IsDark);
            t.Designer.Status += SetStatus;
            t.Designer.RunRequested += Run;
            t.Designer.SaveAsRequested += () => _ = SaveReportAs(t.Designer);
            OpenDocument(t);
            return t;
        }
    }

    private async Task SaveReportAs(ReportDesigner designer, bool run = false)
    {
        var label = designer.Session.Document.Kind == JoePro.Documents.Reports.ReportKind.Label;
        var ext = label ? "jplabel" : "jpreport";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = label ? "Save label" : "Save report",
            SuggestedFileName = (label ? "label1." : "report1.") + ext,
            DefaultExtension = ext,
            FileTypeChoices = [new FilePickerFileType(label ? "Joe Pro label" : "Joe Pro report") { Patterns = ["*." + ext] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        designer.Save(path);
        if (run) designer.Run();
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>MODIFY CLASS name OF lib: the Class Designer on one class of a .jpclass library.</summary>
    public FormDesignerTab OpenClass(string libraryPath, string className)
    {
        var existing = Documents.Items.OfType<FormDesignerTab>().FirstOrDefault(t => t.Designer.IsClass && t.Designer.FilePath != null
            && SamePath(t.Designer.FilePath, libraryPath) && t.Designer.Session.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            Documents.SelectedItem = existing;
            return existing;
        }
        if (Path.GetExtension(libraryPath).Equals(".vcx", StringComparison.OrdinalIgnoreCase))
            throw new VfpException(1102, $"{Path.GetFileName(libraryPath)} is a legacy class library and is read-only. Open it in the Class Browser and save it as .jpclass to edit its classes.", libraryPath);
        var file = JoePro.Documents.ClassLibrary.Load(libraryPath);
        if (file.Find(className) == null) throw new VfpException(1733, $"Class definition {className.ToUpperInvariant()} is not found.", className);
        var tab = new FormDesignerTab(libraryPath, file, _session.Runtime.Options.Default_, IsDark, file.Find(className)!.Name);
        WireDesigner(tab.Designer);
        OpenDocument(tab);
        return tab;
    }

    /// <summary>The Class Browser on a library (.jpclass, or a legacy .vcx shown read-only).</summary>
    public ClassBrowserTab OpenClassBrowser(string libraryPath)
    {
        var existing = Documents.Items.OfType<ClassBrowserTab>().FirstOrDefault(t => SamePath(t.Browser.LibraryPath, libraryPath));
        if (existing != null)
        {
            existing.Browser.Reload();
            Documents.SelectedItem = existing;
            return existing;
        }
        var browser = new ClassBrowser(libraryPath, _session.Runtime, IsDark);
        browser.Status += SetStatus;
        browser.RunRequested += Run;
        browser.ModifyRequested += (lib, cls) =>
        {
            try { OpenClass(lib, cls); }
            catch (VfpException ex) { SetStatus(ex.Message); }
        };
        browser.LibraryChanged += path => _session.Runtime.InvalidateClassLibrary(path);
        var tab = new ClassBrowserTab(browser);
        OpenDocument(tab);
        return tab;
    }

    private void SetNextStatement()
    {
        if (!Debugger.IsPaused || ActiveEditor is not { } tab) return;
        var line = tab.Editor.TextArea.Caret.Line;
        if (!Debugger.SetNextStatement(line)) SetStatus($"Line {line} is not a statement in a block that is running now.");
    }

    /// <summary>Saves the active program and runs it (DO for .prg, DO FORM for .jpform).</summary>
    public void RunActive()
    {
        if (ActiveReportDesigner is { } report)
        {
            if (report.FilePath == null) _ = SaveReportAs(report, run: true);
            else report.Run();
            return;
        }
        if (ActiveDesigner is { } designer)
        {
            if (designer.FilePath == null) _ = SaveDesignerAs(designer, run: true);
            else designer.Run();
            return;
        }
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

    private async Task SaveDesignerAs(FormDesigner designer, bool run = false)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save form",
            SuggestedFileName = designer.Session.ClassName.ToLowerInvariant() + ".jpform",
            DefaultExtension = "jpform",
            FileTypeChoices = [new FilePickerFileType("Joe Pro form") { Patterns = ["*.jpform"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        designer.Save(path);
        if (run) designer.Run();
    }

    private void SaveActive()
    {
        if (ActiveMenuDesigner is { } menu)
        {
            if (menu.FilePath == null) _ = SaveMenuAs(menu);
            else menu.Save();
            return;
        }
        if (ActiveReportDesigner is { } report)
        {
            if (report.FilePath == null) _ = SaveReportAs(report);
            else report.Save();
            return;
        }
        if (ActiveDesigner is { } designer)
        {
            if (designer.FilePath == null) _ = SaveDesignerAs(designer);
            else designer.Save();
        }
        else if (ActiveEditor is { } t)
        {
            if (t.FilePath == null) _ = SaveAs(t);
            else t.Save();
        }
    }

    private async Task OpenWithPicker()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("FoxPro and Joe Pro files") { Patterns = ["*.prg", "*.jpform", "*.scx", "*.jpclass", "*.vcx", "*.jpreport", "*.frx", "*.jplabel", "*.lbx", "*.jpmenu", "*.mnx", "*.h", "*.jpt", "*.jpdb", "*.dbf", "*.txt"] },
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
            case ".jpform" or ".scx":
                OpenForm(path);
                break;
            case ".jpclass" or ".vcx":
                OpenClassBrowser(path);
                break;
            case ".jpreport" or ".frx" or ".jplabel" or ".lbx":
                OpenReport(path);
                break;
            case ".jpmenu" or ".mnx":
                OpenMenu(path);
                break;
            default:
                OpenFile(path);
                break;
        }
    }

    private async Task AddTableToDataEnvironment()
    {
        if (ActiveDesigner is not { } designer) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add table to the data environment",
            FileTypeFilter = [new FilePickerFileType("Tables") { Patterns = ["*.jpt", "*.dbf"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) designer.AddTable(path);
    }

    private async Task OpenClassLibraryWithPicker()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Class library",
            FileTypeFilter = [new FilePickerFileType("Class libraries") { Patterns = ["*.jpclass", "*.vcx"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) OpenClassBrowser(path);
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
        A("New program", "Ctrl+N", () => OpenDocument(new CodeEditorTab(null, IsDark, Debugger.Engine, Language)));
        A("New form", "", () => OpenForm(null, create: true));
        A("New report", "", () => OpenReport(null));
        A("New label", "", () => OpenReport(null, label: true));
        A("New menu", "", () => OpenMenu(null));
        A("Class browser…", "", () => _ = OpenClassLibraryWithPicker());
        A("Class: new property…", "", () => { if (ActiveDesigner is { } d) MemberDialogs.NewMember(d, isMethod: false); });
        A("Class: new method…", "", () => { if (ActiveDesigner is { } d) MemberDialogs.NewMember(d, isMethod: true); });
        A("Class: edit property/method…", "", () => { if (ActiveDesigner is { } d) MemberDialogs.EditMembers(d); });
        A("Class: class info…", "", () => { if (ActiveDesigner is { } d) MemberDialogs.ClassInfo(d); });
        A("Edit form as code", "", () => { if (ActiveDesigner is { FilePath: { } p } d) { if (d.Session.IsDirty) d.Save(); OpenFile(p); } });
        A("Form: undo", "Ctrl+Z", () => ActiveDesigner?.Undo());
        A("Form: redo", "Ctrl+Y", () => ActiveDesigner?.Redo());
        A("Form: align left", "", () => ActiveDesigner?.Align("Left"));
        A("Form: align right", "", () => ActiveDesigner?.Align("Right"));
        A("Form: align top", "", () => ActiveDesigner?.Align("Top"));
        A("Form: align bottom", "", () => ActiveDesigner?.Align("Bottom"));
        A("Form: same width", "", () => ActiveDesigner?.Align("SameWidth"));
        A("Form: same height", "", () => ActiveDesigner?.Align("SameHeight"));
        A("Form: center horizontally", "", () => ActiveDesigner?.Align("CenterHorizontally"));
        A("Form: center vertically", "", () => ActiveDesigner?.Align("CenterVertically"));
        A("Form: bring to front", "", () => ActiveDesigner?.ZOrder(true));
        A("Form: send to back", "", () => ActiveDesigner?.ZOrder(false));
        A("Form: snap to grid", "", () => { if (ActiveDesigner is { } d) { d.SnapToGrid = !d.SnapToGrid; SetStatus($"Snap to grid: {(d.SnapToGrid ? "on" : "off")}"); } });
        A("Form: add table to data environment…", "", () => _ = AddTableToDataEnvironment());
        A("Go to definition", "F12", () => ActiveEditor?.Intelligence?.GoToDefinition());
        A("Show completions", "Ctrl+Space", () => ActiveEditor?.Intelligence?.ShowCompletion());
        A("Open…", "Ctrl+O", () => _ = OpenWithPicker());
        A("Save", "Ctrl+S", SaveActive);
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
        A("Debug: continue", "F5", () => Debugger.Resume(DebugAction.Continue));
        A("Debug: step over", "F10", () => Debugger.Resume(DebugAction.StepOver));
        A("Debug: step into", "F11", () => Debugger.Resume(DebugAction.StepInto));
        A("Debug: step out", "Shift+F11", () => Debugger.Resume(DebugAction.StepOut));
        A("Debug: stop", "Shift+F5", () => Debugger.Resume(DebugAction.Cancel));
        A("Debug: set next statement", "Ctrl+Shift+F10", SetNextStatement);
        A("Debug: toggle breakpoint", "F9", ToggleBreakpoint);
        A("Debug: break on errors", "", () => { Debugger.Engine.BreakOnErrors = !Debugger.Engine.BreakOnErrors; SetStatus($"Break on unhandled errors: {(Debugger.Engine.BreakOnErrors ? "on" : "off")}"); });
        A("Debug: step into program", "", () => { Debugger.Engine.RequestPause(); RunActive(); });
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
                    Item("New _Form", "New form"),
                    Item("New _Report", "New report"),
                    Item("New _Label", "New label"),
                    Item("New _Menu", "New menu"),
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
                new MenuItem { Header = "F_orm", Items =
                {
                    Item("_Undo", "Form: undo"),
                    Item("_Redo", "Form: redo"),
                    new Separator(),
                    Item("Align _Left", "Form: align left"),
                    Item("Align _Right", "Form: align right"),
                    Item("Align _Top", "Form: align top"),
                    Item("Align _Bottom", "Form: align bottom"),
                    Item("Same _Width", "Form: same width"),
                    Item("Same _Height", "Form: same height"),
                    Item("Center _Horizontally", "Form: center horizontally"),
                    Item("Center _Vertically", "Form: center vertically"),
                    new Separator(),
                    Item("Bring to _Front", "Form: bring to front"),
                    Item("Send to Bac_k", "Form: send to back"),
                    Item("_Snap to Grid", "Form: snap to grid"),
                    new Separator(),
                    Item("Add Table to _Data Environment…", "Form: add table to data environment…"),
                    Item("Edit as _Code", "Edit form as code"),
                } },
                new MenuItem { Header = "_Class", Items =
                {
                    Item("New _Property…", "Class: new property…"),
                    Item("New _Method…", "Class: new method…"),
                    Item("_Edit Property/Method…", "Class: edit property/method…"),
                    Item("Class _Info…", "Class: class info…"),
                    new Separator(),
                    Item("Class _Browser…", "Class browser…"),
                } },
                new MenuItem { Header = "_Program", Items =
                {
                    Item("_Run", "Run program", "Ctrl+E"),
                    Item("Clear _Screen", "Clear screen"),
                } },
                new MenuItem { Header = "_Debug", Items =
                {
                    Item("_Continue", "Debug: continue", "F5"),
                    Item("Step _Over", "Debug: step over", "F10"),
                    Item("Step _Into", "Debug: step into", "F11"),
                    Item("Step O_ut", "Debug: step out", "Shift+F11"),
                    Item("_Stop", "Debug: stop", "Shift+F5"),
                    Item("Set _Next Statement", "Debug: set next statement", "Ctrl+Shift+F10"),
                    new Separator(),
                    Item("Toggle _Breakpoint", "Debug: toggle breakpoint", "F9"),
                    Item("Break on _Errors", "Debug: break on errors"),
                    Item("Step Into _Program", "Debug: step into program"),
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
            (false, false, Key.F5) => Debugger.IsPaused ? () => Debugger.Resume(DebugAction.Continue) : RunActive,
            (false, true, Key.F5) => () => Debugger.Resume(DebugAction.Cancel),
            (false, false, Key.F10) => () => Debugger.Resume(DebugAction.StepOver),
            (true, true, Key.F10) => SetNextStatement,
            (false, false, Key.F11) => () => Debugger.Resume(DebugAction.StepInto),
            (false, true, Key.F11) => () => Debugger.Resume(DebugAction.StepOut),
            (false, false, Key.F9) => ToggleBreakpoint,
            (false, false, Key.F12) => () => ActiveEditor?.Intelligence?.GoToDefinition(),
            (true, false, Key.F2) => () => CommandWindow.Editor.Focus(),
            (true, false, Key.W) => Action("Close document").Run,
            _ => null,
        };
        if (act == null) return;
        e.Handled = true;
        act();
    }
}
