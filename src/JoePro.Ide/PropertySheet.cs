using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using JoePro.Core;
using JoePro.Documents.Design;
using JoePro.Runtime;

namespace JoePro.Ide;

/// <summary>
/// The designer's property sheet: the selected object's properties (values set on this form in bold, inherited
/// or default values in normal weight), with search and categories, plus a Methods list. Typing follows VFP:
/// text needs no quotes, "=expression" enters an expression. Right-click resets a property to its default.
/// </summary>
public sealed class PropertySheet : UserControl
{
    private readonly ComboBox _objects = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(4) };
    private readonly TextBox _search = new() { Watermark = "Search properties", Margin = new Thickness(4, 0, 4, 4) };
    private readonly ComboBox _category = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(4, 0, 4, 4) };
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly ListBox _methods = new();
    private readonly TabControl _tabs = new() { Padding = new Thickness(0) };
    private bool _loading;

    public static readonly string[] Categories = ["All", "Layout", "Data", "Appearance", "Behavior", "Custom", "Other"];
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4), FontSize = 12, MinHeight = 34 };

    private static readonly HashSet<string> Layout = new(StringComparer.OrdinalIgnoreCase)
    { "Left", "Top", "Width", "Height", "Anchor", "AutoSize", "Visible", "TabIndex", "TabStop", "ZOrderSet", "Alignment", "AutoCenter", "WindowState", "ScrollBars", "Stretch" };
    private static readonly HashSet<string> Data = new(StringComparer.OrdinalIgnoreCase)
    { "ControlSource", "RowSource", "RowSourceType", "RecordSource", "RecordSourceType", "Value", "InputMask", "Format", "BoundColumn", "ColumnCount", "ColumnWidths",
      "DataSession", "BufferMode", "ReadOnly", "MaxLength", "Increment", "SpinnerHighValue", "SpinnerLowValue", "KeyboardHighValue", "KeyboardLowValue", "LinkMaster", "ChildOrder", "RelationalExpr" };
    private static readonly HashSet<string> Appearance = new(StringComparer.OrdinalIgnoreCase)
    { "Caption", "FontName", "FontSize", "FontBold", "FontItalic", "ForeColor", "BackColor", "BackStyle", "BorderStyle", "BorderColor", "BorderWidth", "Picture",
      "SpecialEffect", "Curvature", "FillColor", "FillStyle", "WordWrap", "Themes", "Icon", "TitleBar", "PasswordChar", "ToolTipText", "Style" };
    private static readonly HashSet<string> Behavior = new(StringComparer.OrdinalIgnoreCase)
    { "Enabled", "Default", "Cancel", "Closable", "Movable", "MaxButton", "MinButton", "ControlBox", "WindowType", "ShowWindow", "KeyPreview", "AlwaysOnTop",
      "Interval", "MultiSelect", "Sorted", "IncrementalSearch", "AllowAddNew", "DeleteMark", "RecordMark", "AllowTabs", "PageCount", "ButtonCount" };
    private static readonly HashSet<string> ReadOnlyNames = new(StringComparer.OrdinalIgnoreCase)
    { "Class", "BaseClass", "ParentClass", "ClassLibrary", "ControlCount", "Controls", "Objects", "PageCount_", "ListCount", "DataSessionId", "ActiveControl", "FormCount" };

    public PropertySheet()
    {
        foreach (var c in Categories) _category.Items.Add(c);
        _category.SelectedIndex = 0;
        _objects.SelectionChanged += (_, _) => { if (!_loading && _objects.SelectedItem is string p) ObjectChosen?.Invoke(p == RootLabel ? "" : p); };
        _search.TextChanged += (_, _) => Rebuild();
        _category.SelectionChanged += (_, _) => Rebuild();
        _methods.DoubleTapped += (_, _) => { if (_methods.SelectedItem is ListBoxItem { Tag: string m }) MethodChosen?.Invoke(_path, m); };
        var props = new DockPanel();
        var filters = new StackPanel { Children = { _search, _category } };
        DockPanel.SetDock(filters, Dock.Top);
        props.Children.Add(filters);
        var descriptionBorder = new Border { Child = _description, BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Brushes.Gray };
        DockPanel.SetDock(descriptionBorder, Dock.Bottom);
        props.Children.Add(descriptionBorder);
        props.Children.Add(new ScrollViewer { Content = _rows });
        _tabs.Items.Add(new TabItem { Header = "Properties", Content = props, FontSize = 13 });
        _tabs.Items.Add(new TabItem { Header = "Methods", Content = _methods, FontSize = 13 });
        var root = new DockPanel();
        DockPanel.SetDock(_objects, Dock.Top);
        root.Children.Add(_objects);
        root.Children.Add(_tabs);
        Content = root;
    }

    private DesignSession? _session;
    private VfpObject? _object;
    private string _path = "";
    private string RootLabel = "(form)";

    /// <summary>The user picked another object in the object list.</summary>
    public event Action<string>? ObjectChosen;
    /// <summary>The user double-clicked a method: (object path, method name).</summary>
    public event Action<string, string>? MethodChosen;
    /// <summary>A property edit failed (for example an invalid name).</summary>
    public event Action<string>? Error;

    /// <summary>Descriptions of custom properties and methods (supplied by the designer from the class documents).</summary>
    public Func<string, string?>? DescriptionProvider { get; set; }

    /// <summary>The text in the description pane under the property list.</summary>
    public string DescriptionText => _description.Text ?? "";

    public void ShowDescription(string name)
    {
        var text = DescriptionProvider?.Invoke(name) ?? PropertyHelp.For(name);
        _description.Text = text == null ? name : $"{name}: {text}";
    }

    public IReadOnlyList<string> VisibleProperties => _rows.Children.OfType<PropertyRow>().Select(r => r.Name!).ToList();

    /// <summary>Shows an object: <paramref name="allPaths"/> fills the object list.</summary>
    public void Show(DesignSession session, VfpObject obj, string path, IEnumerable<string> allPaths, string rootName)
    {
        _session = session;
        _object = obj;
        _path = path;
        RootLabel = rootName;
        _loading = true;
        try
        {
            _objects.Items.Clear();
            _objects.Items.Add(rootName);
            foreach (var p in allPaths) _objects.Items.Add(p);
            _objects.SelectedItem = path.Length == 0 ? rootName : path;
        }
        finally { _loading = false; }
        Rebuild();
        RebuildMethods();
    }

    public void Clear()
    {
        _object = null;
        _rows.Children.Clear();
        _methods.Items.Clear();
    }

    private string CategoryOf(string name) =>
        Layout.Contains(name) ? "Layout" : Data.Contains(name) ? "Data" : Appearance.Contains(name) ? "Appearance" : Behavior.Contains(name) ? "Behavior"
        : _object != null && !BaseClasses.IsNativeProperty(_object.Class.BaseClass, name) ? "Custom" : "Other";

    private void Rebuild()
    {
        _rows.Children.Clear();
        if (_object == null || _session == null) return;
        var filter = _search.Text?.Trim() ?? "";
        var category = _category.SelectedItem as string ?? "All";
        var names = _object.Properties.Values
            .Where(v => !v.Name.StartsWith("__", StringComparison.Ordinal) && v.Value.Kind != ValueKind.Object)
            .Select(v => v.Name)
            .Where(n => filter.Length == 0 || n.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(n => category == "All" || CategoryOf(n) == category)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        foreach (var n in names) _rows.Children.Add(new PropertyRow(this, n));
    }

    private void RebuildMethods()
    {
        _methods.Items.Clear();
        if (_object == null || _session == null) return;
        var withCode = _session.MethodsWithCode(_path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in DesignerEvents.For(_object.Class.BaseClass)) names.Add(e);
        for (var c = _object.Class; c != null; c = c.Parent)
            if (c.Definition != null) foreach (var m in c.Definition.Methods.Keys.Where(k => !k.Contains('.'))) names.Add(m);
        foreach (var m in withCode) names.Add(m);
        foreach (var n in names)
            _methods.Items.Add(new ListBoxItem { Content = withCode.Contains(n) ? n + "  ●" : n, Tag = n, FontWeight = withCode.Contains(n) ? FontWeight.Bold : FontWeight.Normal });
    }

    /// <summary>Applies typed text to a property, following the property sheet's rules.</summary>
    public void Commit(string name, string input)
    {
        if (_session == null || _object == null) return;
        try
        {
            if (name.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                if (_path.Length == 0 || input.Trim().Equals(_object.Name, StringComparison.Ordinal)) return;
                var renamed = (_path.Contains('.') ? _path[.._path.LastIndexOf('.')] + "." : "") + input.Trim();
                _session.RenameObject(_path, input.Trim());
                ObjectChosen?.Invoke(renamed);
                return;
            }
            var current = _object.FindProperty(name)?.Value ?? Value.EmptyString;
            _session.SetProperty(_path, name, PropertyInput.ToExpression(input, current, name));
        }
        catch (Exception ex) when (ex is ArgumentException or VfpException) { Error?.Invoke(ex.Message); }
    }

    public void Reset(string name) => _session?.SetProperty(_path, name, null);

    private sealed class PropertyRow : Grid
    {
        public PropertyRow(PropertySheet sheet, string name)
        {
            Name = name;
            ColumnDefinitions = new ColumnDefinitions("140,*");
            var stored = sheet._session!.GetProperty(sheet._path, name);
            var live = sheet._object!.FindProperty(name)?.Value ?? Value.Null;
            var display = stored != null ? PropertyInput.ToDisplay(stored, name)
                : live.Kind == ValueKind.Character ? live.AsString
                : live.Kind == ValueKind.Number && PropertyInput.IsColor(name) ? PropertyInput.ColorText((int)live.AsNumber)
                : Formatter.ToDisplay(live, new SetOptions()).Trim();
            var label = new TextBlock
            {
                Text = name,
                FontWeight = stored != null ? FontWeight.Bold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 1),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var readOnly = ReadOnlyNames.Contains(name) || (name.Equals("Name", StringComparison.OrdinalIgnoreCase) && !sheet._session.IsDeclared(sheet._path));
            var box = new TextBox
            {
                Text = display,
                IsReadOnly = readOnly,
                FontWeight = stored != null ? FontWeight.Bold : FontWeight.Normal,
                MinHeight = 24,
                Padding = new Thickness(4, 1),
                Margin = new Thickness(0, 1, 2, 1),
                BorderThickness = new Thickness(1),
            };
            var original = display;
            void CommitIfChanged()
            {
                if (!readOnly && box.Text != original) { original = box.Text ?? ""; sheet.Commit(name, box.Text ?? ""); }
            }
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { CommitIfChanged(); e.Handled = true; }
                else if (e.Key == Key.Escape) { box.Text = original; e.Handled = true; }
            };
            box.LostFocus += (_, _) => CommitIfChanged();
            box.GotFocus += (_, _) => sheet.ShowDescription(name);
            var reset = new MenuItem { Header = "Reset to default", IsEnabled = stored != null && !readOnly };
            reset.Click += (_, _) => sheet.Reset(name);
            ContextMenu = new ContextMenu { Items = { reset } };
            Grid.SetColumn(box, 1);
            Children.Add(label);
            Children.Add(box);
            Box = box;
        }

        public TextBox Box { get; }
    }

    /// <summary>The text box of a property row (for tests and keyboard navigation).</summary>
    public TextBox? EditorFor(string property) => _rows.Children.OfType<PropertyRow>().FirstOrDefault(r => r.Name == property)?.Box;

    public void ShowMethodsTab() => _tabs.SelectedIndex = 1;
}

/// <summary>The native events and methods listed in the Methods tab for each base class.</summary>
public static class DesignerEvents
{
    private static readonly string[] Common = ["Init", "Destroy", "Error"];
    private static readonly string[] Visual = ["Click", "DblClick", "RightClick", "MouseDown", "MouseUp", "MouseMove", "MouseEnter", "MouseLeave", "DragDrop", "DragOver", "Refresh"];
    private static readonly string[] Input = ["GotFocus", "LostFocus", "When", "Valid", "KeyPress", "InteractiveChange", "ProgrammaticChange", "SetFocus"];

    public static IEnumerable<string> For(string baseClass) => baseClass switch
    {
        "Form" => [.. Common, .. Visual, "Load", "Unload", "Activate", "Deactivate", "QueryUnload", "Resize", "Moved", "Paint", "Show", "Hide", "Release"],
        "FormSet" => [.. Common, "Load", "Unload", "Activate", "Deactivate", "Release"],
        "TextBox" or "EditBox" or "Spinner" or "ComboBox" or "ListBox" or "CheckBox" or "OptionButton" => [.. Common, .. Visual, .. Input],
        "CommandButton" => [.. Common, .. Visual, "GotFocus", "LostFocus", "When", "Valid", "KeyPress", "SetFocus"],
        "OptionGroup" or "CommandGroup" => [.. Common, .. Visual, "InteractiveChange", "ProgrammaticChange", "Valid", "When"],
        "Grid" => [.. Common, .. Visual, "AfterRowColChange", "BeforeRowColChange", "Scrolled", "Deleted", "SetFocus"],
        "PageFrame" => [.. Common, .. Visual],
        "Page" => [.. Common, .. Visual, "Activate", "Deactivate"],
        "Timer" => [.. Common, "Timer", "Reset"],
        "DataEnvironment" => [.. Common, "BeforeOpenTables", "AfterCloseTables", "OpenTables", "CloseTables"],
        "Cursor" or "Relation" => Common,
        "Label" or "Image" or "Shape" or "Line" or "Container" or "Control" => [.. Common, .. Visual],
        "Toolbar" => [.. Common, .. Visual, "AfterDock", "BeforeDock", "UnDock", "Resize", "Activate", "Deactivate"],
        "Hyperlink" => [.. Common, "NavigateTo", "GoBack", "GoForward"],
        _ => Common,
    };

    private static readonly HashSet<string> NativeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "AddObject", "AddProperty", "NewObject", "RemoveObject", "Release", "Refresh", "SetFocus", "Move", "ZOrder", "Show", "Hide", "Draw",
        "Cls", "Print", "ReadExpression", "WriteExpression", "ReadMethod", "WriteMethod", "ResetToDefault", "SaveAs", "SaveAsClass", "SetAll",
        "OLEDrag", "Box", "Circle", "Line", "PSet", "Point", "TextHeight", "TextWidth", "CloneObject", "ShowWhatsThis", "WhatsThisMode",
        "AddItem", "AddListItem", "RemoveItem", "RemoveListItem", "Clear", "Requery", "DoVerb", "AddColumn", "DeleteColumn", "ActivateCell",
        "DoScroll", "GridHitTest", "AutoFit", "NavigateTo", "GoBack", "GoForward", "Dock", "Reset", "Timer", "CursorFill", "CursorRefresh",
        "CursorAttach", "CursorDetach", "OpenTables", "CloseTables", "Item", "Add", "Remove", "GetKey",
    };

    /// <summary>True for an event or method every object of the base class has (as opposed to a custom method).</summary>
    public static bool IsNative(string baseClass, string method) =>
        NativeMethods.Contains(method) || For(baseClass).Contains(method, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Short descriptions of common native properties for the property sheet's description pane.</summary>
public static class PropertyHelp
{
    private static readonly Dictionary<string, string> Text = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Alignment"] = "Alignment of text in the control (0 left, 1 right, 2 center, 3 automatic).",
        ["Anchor"] = "How the control moves and resizes when its container is resized.",
        ["AutoCenter"] = "Centers the form in its parent window when it is shown.",
        ["AutoSize"] = "Sizes the control to fit its contents.",
        ["BackColor"] = "Background color, as red,green,blue.",
        ["BackStyle"] = "0 transparent, 1 opaque.",
        ["BorderStyle"] = "Border of the object (for forms: 0 none, 1 fixed single, 2 fixed dialog, 3 sizable).",
        ["BoundColumn"] = "The column of a multicolumn list whose value is stored in Value.",
        ["BufferMode"] = "Buffering of the form's tables: 0 none, 1 pessimistic, 2 optimistic.",
        ["Caption"] = "The text shown in the object's title or label.",
        ["Closable"] = "Whether the form can be closed from its title bar.",
        ["ColumnCount"] = "Number of columns (-1 in a grid: one per field of the record source).",
        ["ControlSource"] = "The field or variable the control's value is bound to.",
        ["DataSession"] = "1 default data session, 2 private data session for the form.",
        ["Enabled"] = "Whether the object responds to user input.",
        ["FontBold"] = "Bold text.",
        ["FontName"] = "Font used for the object's text.",
        ["FontSize"] = "Font size in points.",
        ["ForeColor"] = "Text color, as red,green,blue.",
        ["Format"] = "Display and input format codes (for example ! for uppercase, K to select on entry).",
        ["Height"] = "Height in pixels.",
        ["InputMask"] = "How data is entered and shown: 9 digit, X any character, A letter, ! uppercase, # digit or sign.",
        ["Interval"] = "Milliseconds between Timer events (0 disables the timer).",
        ["Left"] = "Distance from the left edge of the container, in pixels.",
        ["MaxLength"] = "Maximum number of characters that can be typed.",
        ["Name"] = "The name used to refer to the object in code.",
        ["PageCount"] = "Number of pages in the page frame.",
        ["PasswordChar"] = "Character shown instead of what is typed.",
        ["Picture"] = "Image file shown by the object.",
        ["ReadOnly"] = "Whether the value can be changed by the user.",
        ["RecordSource"] = "The table, alias or query shown in the grid.",
        ["RowSource"] = "Where a list or combo box gets its items.",
        ["RowSourceType"] = "Kind of row source: 0 none, 1 value, 2 alias, 3 SQL statement, 5 array, 6 fields…",
        ["ShowWindow"] = "0 in screen, 1 in top-level form, 2 as top-level form.",
        ["TabIndex"] = "Order of the control when tabbing through the form.",
        ["TabStop"] = "Whether Tab moves to the control.",
        ["Tag"] = "Extra text stored with the object for your own use.",
        ["ToolTipText"] = "Text shown when the mouse rests on the control.",
        ["Top"] = "Distance from the top edge of the container, in pixels.",
        ["Value"] = "The current value of the control.",
        ["Visible"] = "Whether the object is shown.",
        ["Width"] = "Width in pixels.",
        ["WindowState"] = "0 normal, 1 minimized, 2 maximized.",
        ["WindowType"] = "0 modeless, 1 modal.",
        ["WordWrap"] = "Wraps text onto several lines.",
    };

    public static string? For(string property) => Text.GetValueOrDefault(property);
}
