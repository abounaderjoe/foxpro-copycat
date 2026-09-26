using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using JoePro.Documents;
using JoePro.Runtime;
using JoePro.Runtime.Builtins;

namespace JoePro.Ide;

/// <summary>A class library in the Class Browser.</summary>
public sealed class ClassBrowserTab : DocumentTab
{
    public ClassBrowserTab(ClassBrowser browser)
    {
        Browser = browser;
        Content = browser;
        Title = "Classes: " + Path.GetFileName(browser.LibraryPath);
        browser.Reloaded += () => Title = "Classes: " + Path.GetFileName(browser.LibraryPath);
    }

    public ClassBrowser Browser { get; }
}

/// <summary>
/// The Class Browser: the classes of a library as a hierarchy, with each class's members (its own and the ones
/// it inherits), its code, and the library operations — new, rename, remove, copy to another library, redefine
/// (change the parent class), create an instance, export the code. A legacy .vcx library is shown read-only
/// until it is saved as a .jpclass library.
/// </summary>
public sealed class ClassBrowser : UserControl
{
    private readonly Interpreter _rt;
    private readonly TreeView _tree = new();
    private readonly TextBox _filter = new() { Watermark = "Filter classes", Margin = new Thickness(4) };
    private readonly TextBlock _header = new() { FontWeight = FontWeight.SemiBold, FontSize = 15, Margin = new Thickness(8, 6, 8, 2), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _details = new() { Margin = new Thickness(8, 0, 8, 6), TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
    private readonly ListBox _members = new();
    private readonly TextEditor _code;
    private readonly TextBlock _legacyNote = new() { Margin = new Thickness(8, 4), Foreground = Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap };
    private readonly List<Button> _writeButtons = new();
    private Button? _saveAsButton;
    private readonly Dictionary<string, ClassFile?> _libraries = new(StringComparer.OrdinalIgnoreCase);

    public ClassBrowser(string path, Interpreter rt, bool dark)
    {
        _rt = rt;
        LibraryPath = Path.GetFullPath(path);
        _code = new TextEditor
        {
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"),
            FontSize = 13,
            IsReadOnly = true,
            ShowLineNumbers = true,
            SyntaxHighlighting = FoxProHighlighting.Get(dark),
        };
        _filter.TextChanged += (_, _) => FillTree();
        _tree.SelectionChanged += (_, _) => ShowSelected();
        _tree.DoubleTapped += (_, _) => { if (SelectedClass != null && !IsLegacy) ModifyRequested?.Invoke(LibraryPath, SelectedClass); };
        _members.DoubleTapped += (_, _) =>
        {
            if (SelectedClass != null && !IsLegacy && _members.SelectedItem is ListBoxItem { Tag: MemberInfo { Kind: "Method", InheritedFrom: null } m })
                ModifyRequested?.Invoke(LibraryPath, SelectedClass);
        };

        var left = new DockPanel { Width = 260 };
        DockPanel.SetDock(_filter, Dock.Top);
        left.Children.Add(_filter);
        left.Children.Add(_tree);

        var tabs = new TabControl { Padding = new Thickness(0) };
        tabs.Items.Add(new TabItem { Header = "Members", Content = _members, FontSize = 13 });
        tabs.Items.Add(new TabItem { Header = "Code", Content = _code, FontSize = 13 });
        var right = new DockPanel();
        var info = new StackPanel { Children = { _header, _details } };
        DockPanel.SetDock(info, Dock.Top);
        right.Children.Add(info);
        right.Children.Add(tabs);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        body.Children.Add(left);
        var split = new GridSplitter { Width = 5, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(split, 1);
        body.Children.Add(split);
        Grid.SetColumn(right, 2);
        body.Children.Add(right);

        var root = new DockPanel();
        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        DockPanel.SetDock(_legacyNote, Dock.Top);
        root.Children.Add(_legacyNote);
        root.Children.Add(body);
        Content = root;
        Reload();
    }

    public string LibraryPath { get; private set; }
    public ClassFile Library { get; private set; } = ClassLibrary.Empty();
    /// <summary>A legacy .vcx library: shown read-only until saved as .jpclass.</summary>
    public bool IsLegacy => Path.GetExtension(LibraryPath).Equals(".vcx", StringComparison.OrdinalIgnoreCase);
    public ConversionResult? Conversion { get; private set; }
    public string? SelectedClass { get; private set; }
    public string Filter
    {
        get => _filter.Text ?? "";
        set { _filter.Text = value; FillTree(); }
    }

    /// <summary>Open the Class Designer on a class: (library path, class name).</summary>
    public event Action<string, string>? ModifyRequested;
    /// <summary>Commands to run in the IDE session (Create instance).</summary>
    public event Action<string>? RunRequested;
    public event Action<string>? Status;
    /// <summary>The library was written (the runtime should drop its cached copy).</summary>
    public event Action<string>? LibraryChanged;
    public event Action? Reloaded;

    // ---- Loading and display ------------------------------------------------------------------------

    public void Reload()
    {
        _libraries.Clear();
        Library = ClassLibrary.Load(LibraryPath, out var conversion);
        Conversion = conversion;
        _legacyNote.IsVisible = IsLegacy;
        _legacyNote.Text = IsLegacy ? $"{Path.GetFileName(LibraryPath)} is a legacy class library (converted in memory, read-only). Use \"Save as .jpclass\" to edit it." : "";
        foreach (var b in _writeButtons) b.IsEnabled = !IsLegacy;
        if (_saveAsButton != null) _saveAsButton.IsVisible = IsLegacy;
        var keep = SelectedClass;
        FillTree();
        if (keep != null && Library.Find(keep) != null) SelectClass(keep);
        else if (VisibleClasses.Count > 0) SelectClass(VisibleClasses[0]);
        else ShowSelected();
        Reloaded?.Invoke();
    }

    /// <summary>Class names in the order the tree shows them (hierarchy order, filtered).</summary>
    public IReadOnlyList<string> VisibleClasses { get; private set; } = [];

    private void FillTree()
    {
        _tree.Items.Clear();
        var filter = Filter.Trim();
        var visible = new List<string>();
        var stack = new List<TreeViewItem>();
        foreach (var (cls, depth) in ClassLibrary.Hierarchy(Library))
        {
            var matches = filter.Length == 0 || cls.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
            if (filter.Length > 0)
            {
                // A flat list of matches when filtering.
                if (!matches) continue;
                _tree.Items.Add(Node(cls));
                visible.Add(cls.Name);
                continue;
            }
            var node = Node(cls);
            while (stack.Count > depth) stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0) _tree.Items.Add(node);
            else stack[^1].Items.Add(node);
            stack.Add(node);
            visible.Add(cls.Name);
        }
        VisibleClasses = visible;
    }

    private TreeViewItem Node(ClassDocument cls) => new()
    {
        Header = cls.ParentLibrary != null || Library.Find(cls.ParentClass) == null
            ? $"{cls.Name}  ({cls.ParentClass}{(cls.ParentLibrary != null ? " of " + Path.GetFileName(cls.ParentLibrary) : "")})"
            : cls.Name,
        Tag = cls.Name,
        IsExpanded = true,
    };

    public void SelectClass(string name)
    {
        TreeViewItem? Find(IEnumerable<object?> items)
        {
            foreach (var i in items.OfType<TreeViewItem>())
            {
                if (i.Tag is string t && t.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
                if (Find(i.Items) is { } found) return found;
            }
            return null;
        }
        var node = Find(_tree.Items);
        if (node != null) _tree.SelectedItem = node;
        SelectedClass = Library.Find(name)?.Name;
        ShowSelected();
    }

    /// <summary>A member of a class as the browser lists it.</summary>
    public sealed record MemberInfo(string Name, string Kind, string? Visibility, string? Value, string? Description, string? InheritedFrom);

    private void ShowSelected()
    {
        if (_tree.SelectedItem is TreeViewItem { Tag: string name }) SelectedClass = name;
        var cls = SelectedClass != null ? Library.Find(SelectedClass) : null;
        _members.Items.Clear();
        if (cls == null)
        {
            _header.Text = Library.Classes.Count == 0 ? "This library has no classes yet." : "";
            _details.Text = "";
            _code.Text = "";
            return;
        }
        var baseClass = Library_BaseClass(cls);
        _header.Text = cls.Name;
        _details.Text = $"Parent class: {cls.ParentClass}{(cls.ParentLibrary != null ? " of " + cls.ParentLibrary : "")}   ·   Base class: {baseClass}"
                        + (cls.OlePublic ? "   ·   OLE public" : "") + (cls.Description != null ? "\n" + cls.Description : "");
        foreach (var m in Members(cls.Name))
        {
            var mark = m.Visibility switch { "PROTECTED" => "#", "HIDDEN" => "-", _ => "+" };
            var kind = m.Kind switch { "Method" => "◆", "Object" => "▣", "Array" => "▦", _ => "◇" };
            var text = $"{mark} {kind} {m.Name}{(m.Value != null ? " = " + m.Value : "")}{(m.InheritedFrom != null ? "   (from " + m.InheritedFrom + ")" : "")}";
            var item = new ListBoxItem { Content = text, Tag = m, Opacity = m.InheritedFrom != null ? 0.6 : 1, Padding = new Thickness(8, 3), MinHeight = 0, FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"), FontSize = 12 };
            if (m.Description != null) ToolTip.SetTip(item, m.Description);
            _members.Items.Add(item);
        }
        _code.Text = ClassLibrary.ClassCode(cls);
    }

    public string ClassCode => SelectedClass != null && Library.Find(SelectedClass) is { } c ? ClassLibrary.ClassCode(c) : "";

    private string Library_BaseClass(ClassDocument cls) => JoePro.Runtime.Builtins.Library.BaseClassOf(_rt, Library, cls, LibraryPath);

    private ClassFile? LoadLibrary(string reference, string relativeTo)
    {
        var path = _rt.ResolveClassFile(reference, ".jpclass", ".vcx", relativeTo);
        if (path == null) return null;
        if (!_libraries.TryGetValue(path, out var lib))
        {
            try { lib = ClassLibrary.Load(path); }
            catch (Exception ex) when (ex is FormatException or IOException) { lib = null; }
            _libraries[path] = lib;
        }
        return lib;
    }

    /// <summary>The members of a class: its own custom properties, arrays, methods and objects, then inherited ones.</summary>
    public IReadOnlyList<MemberInfo> Members(string className)
    {
        var result = new List<MemberInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cls = Library.Find(className);
        if (cls == null) return result;
        var baseClass = Library_BaseClass(cls);
        ClassFile lib = Library;
        var libPath = LibraryPath;
        for (int depth = 0; cls != null && depth < 50; depth++)
        {
            var owner = depth == 0 ? null : cls.Name;
            string? Vis(string n) =>
                cls.FindMethod(n) is { } mm ? mm.Visibility
                : cls.Protected.Contains(n, StringComparer.OrdinalIgnoreCase) ? "PROTECTED"
                : cls.Hidden.Contains(n, StringComparer.OrdinalIgnoreCase) ? "HIDDEN" : null;
            foreach (var (n, v) in cls.Properties.Canonical())
                if (!n.Contains('.') && seen.Add(n) && (depth == 0 || !BaseClasses.IsNativeProperty(baseClass, n)))
                    result.Add(new(n, "Property", Vis(n), v, cls.MemberDescriptions.GetValueOrDefault(n), owner));
            foreach (var (n, dims) in cls.Arrays)
                if (seen.Add(n)) result.Add(new(n, "Array", Vis(n), $"[{dims}]", cls.MemberDescriptions.GetValueOrDefault(n), owner));
            foreach (var m in cls.Methods.Where(m => m.ObjectPath.Length == 0).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                if (seen.Add(m.Name)) result.Add(new(m.Name, "Method", m.Visibility, null, cls.MemberDescriptions.GetValueOrDefault(m.Name), owner));
            foreach (var m in cls.Members)
                if (seen.Add(m.Path)) result.Add(new(m.Path, "Object", m.Protected ? "PROTECTED" : null, m.Class + (m.ClassLibrary != null ? " of " + m.ClassLibrary : ""), null, owner));
            // Up to the parent class (same library, or another .jpclass/.vcx library).
            if (cls.ParentLibrary == null)
            {
                var parent = lib.Find(cls.ParentClass);
                cls = parent != null && !ReferenceEquals(parent, cls) ? parent : null;
            }
            else
            {
                var parentLib = LoadLibrary(cls.ParentLibrary, libPath);
                var parentPath = _rt.ResolveClassFile(cls.ParentLibrary, ".jpclass", ".vcx", libPath);
                cls = parentLib?.Find(cls.ParentClass);
                if (parentLib != null) { lib = parentLib; libPath = parentPath!; }
            }
        }
        return result;
    }

    // ---- Library operations -------------------------------------------------------------------------------

    private void EnsureWritable()
    {
        if (IsLegacy) throw new InvalidOperationException($"{Path.GetFileName(LibraryPath)} is a legacy library and is read-only; save it as .jpclass first.");
    }

    private void Write()
    {
        EnsureWritable();
        ClassFileWriter.Save(Library, LibraryPath);
        LibraryChanged?.Invoke(LibraryPath);
    }

    public void NewClass(string name, string parentClass, string? parentLibrary = null)
    {
        EnsureWritable();
        if (BaseClasses.Exists(parentClass) && parentLibrary == null) parentClass = BaseClasses.Canonical(parentClass);
        else if (parentLibrary == null && Library.Find(parentClass) == null) throw new ArgumentException($"Class {parentClass} is not a base class or a class of this library.");
        ClassLibrary.NewClass(Library, name, parentClass, parentLibrary);
        Write();
        Reload();
        SelectClass(name);
    }

    public void RenameClass(string oldName, string newName)
    {
        EnsureWritable();
        var updated = ClassLibrary.RenameClass(Library, oldName, newName);
        Write();
        Reload();
        SelectClass(newName);
        Status?.Invoke($"Renamed {oldName} to {newName}{(updated > 0 ? $"; {updated} reference(s) in this library updated" : "")}. Code in other files that uses {oldName} is not changed.");
    }

    public IReadOnlyList<string> RemoveClass(string name)
    {
        EnsureWritable();
        var dependents = ClassLibrary.RemoveClass(Library, name);
        Write();
        SelectedClass = null;
        Reload();
        if (dependents.Count > 0) Status?.Invoke($"Removed {name}. {string.Join(", ", dependents)} still refer to it.");
        return dependents;
    }

    /// <summary>Copies a class to another library (created if needed).</summary>
    public void CopyClassTo(string name, string targetPath, string? newName = null, bool overwrite = false)
    {
        targetPath = Path.GetFullPath(Path.HasExtension(targetPath) ? targetPath : targetPath + ".jpclass");
        var target = File.Exists(targetPath) ? ClassLibrary.Load(targetPath) : ClassLibrary.Empty();
        var reference = Path.GetRelativePath(Path.GetDirectoryName(targetPath)!, Path.ChangeExtension(LibraryPath, ".jpclass")).Replace('\\', '/');
        ClassLibrary.CopyClass(Library, name, target, reference, overwrite, newName);
        ClassFileWriter.Save(target, targetPath);
        LibraryChanged?.Invoke(targetPath);
        Status?.Invoke($"Copied {name} to {Path.GetFileName(targetPath)}.");
    }

    public void Redefine(string name, string parentClass, string? parentLibrary)
    {
        EnsureWritable();
        var cls = Library.Find(name) ?? throw new ArgumentException($"Class {name} is not in the library.");
        if (parentLibrary == null && !BaseClasses.Exists(parentClass) && Library.Find(parentClass) == null)
            throw new ArgumentException($"Class {parentClass} is not a base class or a class of this library.");
        if (parentLibrary == null && BaseClasses.Exists(parentClass)) parentClass = BaseClasses.Canonical(parentClass);
        ClassLibrary.Redefine(cls, parentClass, parentLibrary);
        Write();
        Reload();
        SelectClass(name);
    }

    /// <summary>Writes a class's code to a program file (Class Browser → Export).</summary>
    public void ExportCode(string name, string path)
    {
        var cls = Library.Find(name) ?? throw new ArgumentException($"Class {name} is not in the library.");
        File.WriteAllText(path, $"* {cls.Name} from {Path.GetFileName(LibraryPath)}\n\n" + ClassLibrary.ClassCode(cls));
        Status?.Invoke($"Exported {name} to {Path.GetFileName(path)}.");
    }

    /// <summary>Saves a legacy .vcx library as .jpclass next to it and continues with the new file.</summary>
    public string SaveAsJpclass()
    {
        var target = Path.ChangeExtension(LibraryPath, ".jpclass");
        ClassFileWriter.Save(Library, target);
        LibraryPath = target;
        LibraryChanged?.Invoke(target);
        Reload();
        return target;
    }

    public void CreateInstance(string name)
    {
        var cls = Library.Find(name) ?? throw new ArgumentException($"Class {name} is not in the library.");
        RunRequested?.Invoke(FormDesigner.PreviewCommand(cls.Name, LibraryPath, Library_BaseClass(cls)));
    }

    // ---- Toolbar and dialogs ----------------------------------------------------------------------------

    private Control BuildToolbar()
    {
        Button B(string text, string tip, Action act, bool writes = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 2), FontSize = 12 };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) =>
            {
                try { act(); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { Status?.Invoke(ex.Message); }
            };
            if (writes) _writeButtons.Add(b);
            return b;
        }
        return new WrapPanel
        {
            Margin = new Thickness(4),
            Children =
            {
                B("Modify", "Open the class in the Class Designer", () => { if (SelectedClass != null) ModifyRequested?.Invoke(LibraryPath, SelectedClass); }, writes: true),
                B("New…", "New class", () => Prompt("New Class", [("Class name", ""), ("Based on", SelectedClass ?? "Custom"), ("From library", "")],
                    v => NewClass(v[0], v[1], v[2].Length > 0 ? v[2] : null)), writes: true),
                B("Rename…", "Rename the class", () => { if (SelectedClass is { } c) Prompt("Rename Class", [("New name", c)], v => RenameClass(c, v[0])); }, writes: true),
                B("Remove", "Remove the class from the library", () => { if (SelectedClass is { } c) RemoveClass(c); }, writes: true),
                B("Copy to…", "Copy the class to another library", () =>
                {
                    if (SelectedClass is { } c)
                        Prompt("Copy Class", [("Target library", Path.Combine(Path.GetDirectoryName(LibraryPath)!, "other.jpclass")), ("New name", c)],
                            v => CopyClassTo(c, v[0], v[1].Length > 0 && v[1] != c ? v[1] : null));
                }),
                B("Redefine…", "Change the parent class", () =>
                {
                    if (SelectedClass is { } c && Library.Find(c) is { } cls)
                        Prompt("Redefine Class", [("Parent class", cls.ParentClass), ("Parent library", cls.ParentLibrary ?? "")],
                            v => Redefine(c, v[0], v[1].Length > 0 ? v[1] : null));
                }, writes: true),
                B("Create instance", "Create an object of the class", () => { if (SelectedClass is { } c) CreateInstance(c); }),
                B("Export…", "Write the class code to a .prg file", () =>
                {
                    if (SelectedClass is { } c)
                        Prompt("Export Class Code", [("File", Path.Combine(Path.GetDirectoryName(LibraryPath)!, c.ToLowerInvariant() + ".prg"))], v => ExportCode(c, v[0]));
                }),
                (_saveAsButton = B("Save as .jpclass", "Convert this legacy library to a .jpclass library", () => SaveAsJpclass())),
                B("Refresh", "Reload the library from disk", Reload),
            },
        };
    }

    /// <summary>A small dialog with text fields; <paramref name="ok"/> runs with the values and may throw ArgumentException to keep it open.</summary>
    private void Prompt(string title, (string Label, string Value)[] fields, Action<string[]> ok)
    {
        var boxes = fields.Select(f => new TextBox { Text = f.Value }).ToArray();
        var message = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        var okButton = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var panel = new StackPanel();
        for (int i = 0; i < fields.Length; i++)
            panel.Children.Add(new DockPanel { Margin = new Thickness(0, 3), Children = { new TextBlock { Text = fields[i].Label, Width = 110, VerticalAlignment = VerticalAlignment.Center }, boxes[i] } });
        panel.Children.Add(message);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { okButton, cancel } });
        var dialog = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new Border { Padding = new Thickness(12), Child = panel },
        };
        okButton.Click += (_, _) =>
        {
            try { ok(boxes.Select(b => (b.Text ?? "").Trim()).ToArray()); dialog.Close(); }
            catch (Exception ex) when (ex is ArgumentException or IOException) { message.Text = ex.Message; }
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => boxes.FirstOrDefault()?.Focus();
        if (TopLevel.GetTopLevel(this) is Window owner) _ = dialog.ShowDialog(owner);
        else dialog.Show();
    }

    public void SetDark(bool dark) => _code.SyntaxHighlighting = FoxProHighlighting.Get(dark);
}
