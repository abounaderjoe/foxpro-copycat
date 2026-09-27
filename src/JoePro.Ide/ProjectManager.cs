using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using JoePro.Core;
using JoePro.Documents.Projects;
using JoePro.Runtime;

namespace JoePro.Ide;

public sealed class ProjectManagerTab : DocumentTab
{
    public ProjectManagerTab(ProjectManager pm)
    {
        Manager = pm;
        Content = pm;
        void Update() => Title = "Project: " + System.IO.Path.GetFileName(pm.ProjectPath) + (pm.IsDirty ? " •" : "");
        pm.Changed += Update;
        Update();
    }

    public ProjectManager Manager { get; }
}

/// <summary>
/// The Project Manager: the project's files by category (All, Data, Documents, Classes, Code, Other) with search,
/// New/Add/Modify/Run/Remove, main program, include/exclude, build, and a project-wide text search.
/// </summary>
public sealed class ProjectManager : UserControl
{
    private readonly IdeSession _ide;
    private readonly TabControl _tabs = new() { Padding = new Thickness(0) };
    private readonly TextBox _filter = new() { Watermark = "Filter files", Margin = new Thickness(4) };
    private readonly TextBox _search = new() { Watermark = "Find in project files", Margin = new Thickness(4) };
    private readonly ListBox _results = new() { MaxHeight = 220 };
    private readonly TextBlock _info = new() { Margin = new Thickness(6, 2), Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
    private string? _selected;

    public static readonly (string Tab, (string Group, ProjectFileType[] Types)[] Groups)[] Categories =
    [
        ("Data", [("Databases", [ProjectFileType.Database]), ("Free tables", [ProjectFileType.Table]), ("Queries", [ProjectFileType.Query])]),
        ("Documents", [("Forms", [ProjectFileType.Form]), ("Reports", [ProjectFileType.Report]), ("Labels", [ProjectFileType.Label])]),
        ("Classes", [("Class libraries", [ProjectFileType.ClassLibrary])]),
        ("Code", [("Programs", [ProjectFileType.Program]), ("API libraries", [ProjectFileType.Library]), ("Applications", [ProjectFileType.Application])]),
        ("Other", [("Menus", [ProjectFileType.Menu]), ("Text files", [ProjectFileType.Text]), ("Other files", [ProjectFileType.Other])]),
    ];

    public ProjectManager(string path, IdeSession ide)
    {
        _ide = ide;
        ProjectPath = System.IO.Path.GetFullPath(path);
        Project = File.Exists(ProjectPath) ? ide.Runtime.LoadProject(ProjectPath) : new ProjectDocument { Name = System.IO.Path.GetFileNameWithoutExtension(ProjectPath) };
        if (System.IO.Path.GetExtension(ProjectPath).Equals(".pjx", StringComparison.OrdinalIgnoreCase))
        {
            ProjectPath = System.IO.Path.ChangeExtension(ProjectPath, ".jpproj");
            IsDirty = true;
        }
        _filter.TextChanged += (_, _) => Fill();
        _search.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) ShowSearch(_search.Text ?? ""); };
        _results.DoubleTapped += (_, _) => { if (_results.SelectedItem is ListBoxItem { Tag: SearchHit hit }) OpenAt(hit); };
        Button B(string text, string tip, Action act)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 2), FontSize = 12 };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) =>
            {
                try { act(); }
                catch (Exception ex) when (ex is VfpException or ArgumentException or IOException or InvalidOperationException) { Status?.Invoke(ex.Message); }
            };
            return b;
        }
        var toolbar = new WrapPanel
        {
            Margin = new Thickness(4),
            Children =
            {
                B("New…", "New file of the selected kind", () => NewFileDialog()),
                B("Add…", "Add existing files", () => _ = AddWithPicker()),
                B("Modify", "Open the selected file", () => { if (_selected != null) Open(_selected); }),
                B("▶ Run", "Run the selected file (or the main program)", () => Run(_selected ?? Project.Main ?? throw new InvalidOperationException("Select a file."))),
                B("Remove", "Remove the selected file from the project", () => { if (_selected != null) RemoveFile(_selected); }),
                B("Set main", "Make the selected program the main program", () => { if (_selected != null) SetMain(_selected); }),
                B("Exclude/Include", "Toggle whether the file is built into the application", () => { if (_selected != null) ToggleExclude(_selected); }),
                B("Build…", "Build the project, an application or an executable", BuildDialog),
                B("Save", "Save the project", Save),
            },
        };
        var searchPanel = new DockPanel();
        DockPanel.SetDock(_search, Dock.Top);
        searchPanel.Children.Add(_search);
        searchPanel.Children.Add(_results);
        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        DockPanel.SetDock(_filter, Dock.Top);
        root.Children.Add(_filter);
        DockPanel.SetDock(_info, Dock.Bottom);
        root.Children.Add(_info);
        DockPanel.SetDock(searchPanel, Dock.Bottom);
        root.Children.Add(searchPanel);
        root.Children.Add(_tabs);
        Content = root;
        Fill();
    }

    public string ProjectPath { get; private set; }
    public string ProjectDir => System.IO.Path.GetDirectoryName(ProjectPath)!;
    public ProjectDocument Project { get; }
    public bool IsDirty { get; private set; }
    public string Filter { get => _filter.Text ?? ""; set { _filter.Text = value; Fill(); } }
    public event Action<string>? Status;
    public event Action? Changed;
    /// <summary>Opens a file in the IDE (hosted by the main window).</summary>
    public event Action<string>? OpenRequested;
    public event Action<string>? RunRequested;

    private void Touch()
    {
        IsDirty = true;
        Fill();
        Changed?.Invoke();
    }

    // ---- Lists ------------------------------------------------------------------------------------

    /// <summary>The files a tab shows (All, Data, Documents, Classes, Code, Other), filtered.</summary>
    public IReadOnlyList<string> Visible(string tab)
    {
        var types = tab == "All" ? null : Categories.First(c => c.Tab == tab).Groups.SelectMany(g => g.Types).ToHashSet();
        return Project.Files.Where(f => types == null || types.Contains(f.Type))
            .Where(f => Filter.Length == 0 || f.Path.Contains(Filter, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Path).ToList();
    }

    private void Fill()
    {
        var keep = _tabs.SelectedIndex;
        _tabs.Items.Clear();
        TreeViewItem FileNode(ProjectFile f) => new()
        {
            Header = (f.Path.Equals(Project.Main, StringComparison.OrdinalIgnoreCase) ? "★ " : "") + f.Path + (f.Exclude ? "  (excluded)" : ""),
            Tag = f.Path,
            Opacity = f.Exclude ? 0.65 : 1,
        };
        TreeView Tree() { var t = new TreeView(); t.SelectionChanged += (_, _) => { if (t.SelectedItem is TreeViewItem { Tag: string p }) { _selected = p; ShowInfo(); } }; t.DoubleTapped += (_, _) => { if (_selected != null) Open(_selected); }; return t; }
        var all = Tree();
        foreach (var path in Visible("All")) all.Items.Add(FileNode(Project.Find(path)!));
        _tabs.Items.Add(new TabItem { Header = "All", Content = all, FontSize = 13 });
        foreach (var (tab, groups) in Categories)
        {
            var tree = Tree();
            var visible = Visible(tab).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (group, types) in groups)
            {
                var node = new TreeViewItem { Header = group, IsExpanded = true };
                foreach (var f in Project.Files.Where(f => types.Contains(f.Type) && visible.Contains(f.Path))) node.Items.Add(FileNode(f));
                tree.Items.Add(node);
            }
            _tabs.Items.Add(new TabItem { Header = tab, Content = tree, FontSize = 13 });
        }
        _tabs.SelectedIndex = Math.Max(0, keep);
        ShowInfo();
    }

    private void ShowInfo() =>
        _info.Text = $"Main program: {Project.Main ?? "(none)"}   ·   {Project.Files.Count} file(s)" + (_selected is { } s && Project.Find(s) is { } f ? $"   ·   {f.Path} ({f.Type}{(f.Exclude ? ", excluded" : "")})" : "");

    // ---- Commands ----------------------------------------------------------------------------------

    public ProjectFile AddFile(string path)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(ProjectDir, path));
        var rel = System.IO.Path.GetRelativePath(ProjectDir, full).Replace('\\', '/');
        var f = Project.Add(rel);
        Touch();
        return f;
    }

    public void RemoveFile(string path)
    {
        Project.Files.RemoveAll(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (path.Equals(Project.Main, StringComparison.OrdinalIgnoreCase)) Project.Main = null;
        if (path.Equals(_selected, StringComparison.OrdinalIgnoreCase)) _selected = null;
        Touch();
    }

    public void SetMain(string path)
    {
        var f = Project.Find(path) ?? throw new ArgumentException($"{path} is not in the project.");
        if (f.Type != ProjectFileType.Program) throw new ArgumentException("The main file must be a program.");
        Project.Main = f.Path;
        f.Exclude = false;
        Touch();
    }

    public void ToggleExclude(string path)
    {
        var f = Project.Find(path) ?? throw new ArgumentException($"{path} is not in the project.");
        f.Exclude = !f.Exclude;
        Touch();
    }

    public void Select(string path) { _selected = Project.Find(path)?.Path; ShowInfo(); }

    public void Save()
    {
        Project.Save(ProjectPath);
        IsDirty = false;
        Changed?.Invoke();
        Status?.Invoke($"Saved {System.IO.Path.GetFileName(ProjectPath)}.");
    }

    private string Full(string rel) => System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectDir, rel));

    public void Open(string rel) => OpenRequested?.Invoke(Full(rel));

    /// <summary>What Run does for a file: DO a program or menu, DO FORM, REPORT/LABEL FORM PREVIEW, USE + BROWSE a table.</summary>
    public string RunCommand(string rel)
    {
        var full = Full(rel);
        var f = Project.Find(rel);
        return (f?.Type ?? ProjectDocument.TypeOf(rel)) switch
        {
            ProjectFileType.Form => $"DO FORM \"{full}\"",
            ProjectFileType.Report => $"REPORT FORM \"{full}\" PREVIEW",
            ProjectFileType.Label => $"LABEL FORM \"{full}\" PREVIEW",
            ProjectFileType.Table => $"USE \"{full}\" IN 0 AGAIN\nBROWSE",
            ProjectFileType.Database => $"OPEN DATABASE \"{full}\"",
            ProjectFileType.Query => $"DO \"{full}\"",
            ProjectFileType.ClassLibrary => $"SET CLASSLIB TO \"{full}\" ADDITIVE",
            _ => $"DO \"{full}\"",
        };
    }

    public void Run(string rel) => RunRequested?.Invoke(RunCommand(rel));

    /// <summary>Creates a new file of a kind in the project folder, adds it and opens it.</summary>
    public string NewFile(ProjectFileType type, string name)
    {
        var ext = type switch
        {
            ProjectFileType.Form => ".jpform", ProjectFileType.Report => ".jpreport", ProjectFileType.Label => ".jplabel", ProjectFileType.Menu => ".jpmenu",
            ProjectFileType.ClassLibrary => ".jpclass", ProjectFileType.Text => ".txt", _ => ".prg",
        };
        var rel = System.IO.Path.HasExtension(name) ? name : name + ext;
        var full = Full(rel);
        if (File.Exists(full)) throw new ArgumentException($"{rel} already exists.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        switch (type)
        {
            case ProjectFileType.Form:
                JoePro.Documents.ClassFileWriter.Save(JoePro.Documents.Design.DesignSession.NewForm(System.IO.Path.GetFileNameWithoutExtension(full)), full);
                break;
            case ProjectFileType.Report:
                JoePro.Documents.Reports.ReportDocument.NewReport().Save(full);
                break;
            case ProjectFileType.Label:
                JoePro.Documents.Reports.LabelPresets.Create(JoePro.Documents.Reports.LabelPresets.All[0]).Save(full);
                break;
            case ProjectFileType.Menu:
                JoePro.Documents.Menus.MenuDesignSession.QuickMenu().Save(full);
                break;
            case ProjectFileType.ClassLibrary:
                JoePro.Documents.ClassFileWriter.Save(JoePro.Documents.ClassLibrary.Empty(), full);
                break;
            default:
                File.WriteAllText(full, type == ProjectFileType.Program ? $"* {rel}\n" : "");
                break;
        }
        AddFile(full);
        Select(rel);
        Open(rel);
        return rel;
    }

    /// <summary>BUILD PROJECT / APP / EXE for this project (saved first).</summary>
    public BuildResult Build(string kind, string? output = null)
    {
        Save();
        var outName = output ?? System.IO.Path.Combine(ProjectDir, Project.Output ?? Project.Name);
        var result = kind.ToUpperInvariant() switch
        {
            "APP" => ProjectBuilder.BuildApp(Project, ProjectDir, System.IO.Path.ChangeExtension(outName, ".jpapp")),
            "EXE" => ProjectBuilder.BuildExe(Project, ProjectDir, outName),
            _ => ProjectBuilder.Check(Project, ProjectDir),
        };
        Status?.Invoke(result.Succeeded
            ? (kind.Equals("PROJECT", StringComparison.OrdinalIgnoreCase) ? $"No errors in {result.Files.Count} file(s)." : $"Built {result.Output}.")
              + (result.Warnings.Count > 0 ? $" {result.Warnings.Count} warning(s): " + string.Join("; ", result.Warnings.Take(3).Select(w => $"{w.File}: {w.Message}")) : "")
            : $"{result.Errors.Count} error(s): " + string.Join("; ", result.Errors.Take(3).Select(e => $"{e.File}{(e.Line > 0 ? $"({e.Line})" : "")}: {e.Message}")));
        return result;
    }

    // ---- Project-wide search -------------------------------------------------------------------------

    public sealed record SearchHit(string File, int Line, string Text);

    /// <summary>Finds text in the project's text files (programs, forms, class libraries, menus, reports, headers).</summary>
    public List<SearchHit> Search(string text)
    {
        var hits = new List<SearchHit>();
        if (text.Length == 0) return hits;
        var textual = new[] { ".prg", ".h", ".jpform", ".jpclass", ".jpmenu", ".jpreport", ".jplabel", ".jpquery", ".txt", ".md", ".mpr", ".qpr" };
        foreach (var f in Project.Files)
        {
            var full = Full(f.Path);
            if (!File.Exists(full) || !textual.Contains(System.IO.Path.GetExtension(full).ToLowerInvariant())) continue;
            var lines = File.ReadAllLines(full);
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Contains(text, StringComparison.OrdinalIgnoreCase)) hits.Add(new SearchHit(f.Path, i + 1, lines[i].Trim()));
        }
        return hits;
    }

    private void ShowSearch(string text)
    {
        _results.Items.Clear();
        foreach (var hit in Search(text))
            _results.Items.Add(new ListBoxItem { Content = $"{hit.File}({hit.Line}): {hit.Text}", Tag = hit, FontSize = 12, Padding = new Thickness(6, 2), MinHeight = 0 });
        Status?.Invoke($"{_results.Items.Count} match(es) for \"{text}\".");
    }

    private void OpenAt(SearchHit hit) => OpenRequested?.Invoke(Full(hit.File) + "#" + hit.Line);

    // ---- Dialogs -----------------------------------------------------------------------------------

    private async Task AddWithPicker()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Add files to the project", AllowMultiple = true });
        foreach (var f in files) if (f.TryGetLocalPath() is { } p) AddFile(p);
    }

    private void NewFileDialog()
    {
        var kinds = new[] { ProjectFileType.Program, ProjectFileType.Form, ProjectFileType.Report, ProjectFileType.Label, ProjectFileType.Menu, ProjectFileType.ClassLibrary, ProjectFileType.Text };
        var kind = new ComboBox { ItemsSource = kinds.Select(k => k.ToString()).ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { Watermark = "name (for example forms/customer)" };
        var message = new TextBlock { Foreground = Brushes.IndianRed };
        var ok = new Button { Content = "Create", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var dialog = new Window
        {
            Title = "New File", Width = 400, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border { Padding = new Thickness(12), Child = new StackPanel { Spacing = 6, Children = { kind, name, message, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } } } } },
        };
        ok.Click += (_, _) =>
        {
            try { NewFile(kinds[kind.SelectedIndex], (name.Text ?? "").Trim()); dialog.Close(); }
            catch (Exception ex) when (ex is ArgumentException or IOException) { message.Text = ex.Message; }
        };
        cancel.Click += (_, _) => dialog.Close();
        if (TopLevel.GetTopLevel(this) is Window owner) _ = dialog.ShowDialog(owner); else dialog.Show();
    }

    private void BuildDialog()
    {
        var kind = new ComboBox { ItemsSource = new[] { "Check the project (BUILD PROJECT)", "Application .jpapp (BUILD APP)", "Executable folder (BUILD EXE)" }, SelectedIndex = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        var output = new TextBox { Text = System.IO.Path.Combine(ProjectDir, Project.Output ?? Project.Name) };
        var version = new TextBox { Text = Project.Version };
        var ok = new Button { Content = "Build", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var dialog = new Window
        {
            Title = "Build", Width = 480, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border { Padding = new Thickness(12), Child = new StackPanel { Spacing = 6, Children = { kind, new TextBlock { Text = "Output" }, output, new TextBlock { Text = "Version" }, version, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } } } } },
        };
        ok.Click += (_, _) =>
        {
            Project.Version = (version.Text ?? "1.0.0").Trim();
            Build(kind.SelectedIndex switch { 0 => "PROJECT", 1 => "APP", _ => "EXE" }, output.Text);
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        if (TopLevel.GetTopLevel(this) is Window owner) _ = dialog.ShowDialog(owner); else dialog.Show();
    }
}
