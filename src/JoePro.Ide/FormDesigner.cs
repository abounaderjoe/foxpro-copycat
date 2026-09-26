using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using JoePro.Core;
using JoePro.Documents;
using JoePro.Documents.Design;
using JoePro.Language;
using JoePro.Runtime;
using JoePro.Ui.Runtime;

namespace JoePro.Ide;

/// <summary>A form (.jpform) or visual class open in the Form Designer.</summary>
public sealed class FormDesignerTab : DocumentTab
{
    public FormDesignerTab(string? path, ClassFile file, string workingDirectory, bool dark)
    {
        Designer = new FormDesigner(path, file, workingDirectory, dark);
        Designer.Session.Changed += UpdateTitle;
        Designer.Saved += UpdateTitle;
        Content = Designer;
        UpdateTitle();
    }

    public FormDesigner Designer { get; }

    private void UpdateTitle() =>
        Title = (Designer.FilePath != null ? System.IO.Path.GetFileName(Designer.FilePath) : Designer.Session.ClassName + ".jpform") + (Designer.Session.IsDirty ? " •" : "");
}

/// <summary>
/// The Form Designer. The design surface shows the form as it will run: it is built by a separate runtime in
/// design mode (property values apply, no event code runs) and rendered with the same controls. Every edit goes
/// through a <see cref="DesignSession"/>, so everything can be undone and the saved file is canonical.
/// </summary>
public sealed class FormDesigner : UserControl
{
    private readonly Interpreter _rt;
    private readonly AvaloniaUiHost _host;
    private readonly Panel _renderHost = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Canvas _overlay = new() { Background = Brushes.Transparent, Focusable = true };
    private readonly Canvas _adorners = new() { IsHitTestVisible = false };
    private readonly Border _formFrame = new();
    private readonly TextBlock _formTitle = new() { Margin = new Thickness(8, 4), FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _message = new() { Margin = new Thickness(12), Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _toolbox = new();
    private readonly TreeView _dataEnvironment = new() { MinHeight = 80 };
    private readonly TextEditor _code;
    private readonly TextBlock _codeTitle = new() { Margin = new Thickness(6, 3), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _centerGrid;
    private readonly List<string> _selection = new();
    private string? _codePath, _codeMethod, _codeOriginal;
    private static string? s_clipboard;

    public const string PointerTool = "Pointer";

    public static readonly string[] ToolClasses =
    [
        "Label", "TextBox", "EditBox", "CommandButton", "CheckBox", "OptionGroup", "CommandGroup", "ComboBox", "ListBox",
        "Spinner", "Grid", "Image", "Shape", "Line", "Container", "PageFrame", "Timer",
    ];

    public FormDesigner(string? path, ClassFile file, string workingDirectory, bool dark)
    {
        FilePath = path;
        Session = new DesignSession(file);
        _rt = new Interpreter(new TextWriterOutput(TextWriter.Null), workingDirectory) { DesignMode = true };
        _rt.ExecuteCommand("SET TALK OFF");
        _host = new AvaloniaUiHost(_rt);
        Properties = new PropertySheet();
        Properties.ObjectChosen += p => Select([p]);
        Properties.MethodChosen += OpenMethod;
        Properties.Error += m => Status?.Invoke(m);

        _code = new TextEditor
        {
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"),
            FontSize = 13,
            ShowLineNumbers = true,
            SyntaxHighlighting = FoxProHighlighting.Get(dark),
        };
        _code.Options.ConvertTabsToSpaces = true;
        _code.Options.IndentationSize = 3;
        _code.LostFocus += (_, _) => CommitCode();

        // Toolbox and data environment (left).
        ListBoxItem Tool(string text, string tag) => new() { Content = text, Tag = tag, Padding = new Thickness(10, 3), MinHeight = 0, FontSize = 13 };
        _toolbox.Items.Add(Tool("➤ " + PointerTool, PointerTool));
        foreach (var t in ToolClasses) _toolbox.Items.Add(Tool(t, t));
        _toolbox.SelectedIndex = 0;
        _toolbox.SelectionChanged += (_, _) => ArmedTool = _toolbox.SelectedItem is ListBoxItem { Tag: string t } && t != PointerTool ? t : null;
        _dataEnvironment.DoubleTapped += (_, _) => AddFromDataEnvironment();
        var left = new DockPanel { Width = 170 };
        var toolboxHeader = Header("Toolbox");
        DockPanel.SetDock(toolboxHeader, Dock.Top);
        left.Children.Add(toolboxHeader);
        var deHeader = Header("Data environment");
        var dePanel = new DockPanel { Height = 160 };
        DockPanel.SetDock(deHeader, Dock.Top);
        dePanel.Children.Add(deHeader);
        dePanel.Children.Add(_dataEnvironment);
        DockPanel.SetDock(dePanel, Dock.Bottom);
        left.Children.Add(dePanel);
        left.Children.Add(_toolbox);

        // Design surface (center): the rendered form, adorners and an input overlay on top.
        var surface = new Panel { Children = { _renderHost } };
        _renderHost.Children.Add(_adorners);
        _renderHost.Children.Add(_overlay);
        _formFrame.Child = new DockPanel { Children = { _formTitle, surface } };
        DockPanel.SetDock(_formTitle, Dock.Top);
        _formFrame.BorderThickness = new Thickness(1);
        _formFrame.BorderBrush = Brushes.SteelBlue;
        _formFrame.HorizontalAlignment = HorizontalAlignment.Left;
        _formFrame.VerticalAlignment = VerticalAlignment.Top;
        _formFrame.Margin = new Thickness(16);
        var scroller = new ScrollViewer
        {
            Content = new StackPanel { Children = { _message, _formFrame } },
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        _overlay.PointerPressed += OnPointerPressed;
        _overlay.PointerMoved += OnPointerMoved;
        _overlay.PointerReleased += OnPointerReleased;
        _overlay.KeyDown += (_, e) => OnKey(e);

        var codeHeader = new DockPanel { Children = { _codeTitle } };
        var codePane = new DockPanel();
        DockPanel.SetDock(codeHeader, Dock.Top);
        codePane.Children.Add(codeHeader);
        codePane.Children.Add(_code);
        _centerGrid = new Grid { RowDefinitions = new RowDefinitions("3*,Auto,0") };
        _centerGrid.Children.Add(scroller);
        var split = new GridSplitter { Height = 5, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(split, 1);
        _centerGrid.Children.Add(split);
        Grid.SetRow(codePane, 2);
        _centerGrid.Children.Add(codePane);

        var toolbar = BuildToolbar();
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,300") };
        body.Children.Add(left);
        Grid.SetColumn(_centerGrid, 1);
        body.Children.Add(_centerGrid);
        var right = new Border { Child = Properties, BorderThickness = new Thickness(1, 0, 0, 0), BorderBrush = Brushes.Gray };
        Grid.SetColumn(right, 2);
        body.Children.Add(right);
        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(body);
        Content = root;

        Session.Changed += () => Render();
        Render();
        Select([""]);
    }

    private static TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 4) };

    public DesignSession Session { get; }
    public PropertySheet Properties { get; }
    public string? FilePath { get; private set; }
    public VfpObject? Root { get; private set; }
    public Canvas? Rendered { get; private set; }
    public IReadOnlyList<string> Selection => _selection;
    public string? ArmedTool { get; set; }
    public int GridSize { get; set; } = 12;
    public bool SnapToGrid { get; set; } = true;
    public string? RenderError { get; private set; }

    public event Action<string>? Status;
    public event Action<string>? RunRequested;
    public event Action? Saved;

    // ================================================================================
    // Rendering
    // ================================================================================

    /// <summary>Builds the form from the current document in design mode and shows it.</summary>
    public void Render()
    {
        RenderError = null;
        _message.Text = "";
        try
        {
            var name = Session.ClassName;
            var unit = Parser.ParseProgram(Session.Text, name.ToUpperInvariant(), FilePath, inc => _rt.ResolveInclude(inc, FilePath));
            var cls = _rt.ResolveClass(name, unit);
            var obj = _rt.BuildForDesign(cls);
            Root = obj;
            if (Rendered != null) _renderHost.Children.Remove(Rendered);
            Rendered = _host.BuildDesignSurface(obj);
            Rendered.IsHitTestVisible = false;
            _renderHost.Children.Insert(0, Rendered);
            _renderHost.Width = Rendered.Width;
            _renderHost.Height = Rendered.Height;
            _overlay.Width = _adorners.Width = Rendered.Width;
            _overlay.Height = _adorners.Height = Rendered.Height;
            _formTitle.Text = Prop(obj, "Caption") is { Kind: ValueKind.Character } c ? c.AsString : name;
        }
        catch (Exception ex) when (ex is VfpException or CompileException)
        {
            RenderError = ex.Message;
            _message.Text = "The form cannot be displayed: " + ex.Message;
        }
        _selection.RemoveAll(p => Find(p) == null);
        if (_selection.Count == 0) _selection.Add("");
        RefreshDataEnvironment();
        ShowSelection();
    }

    private static Value Prop(VfpObject o, string name) => o.FindProperty(name)?.Value ?? Value.Null;

    public VfpObject? Find(string path) => Root == null ? null : path.Length == 0 ? Root : AvaloniaUiHost.Find(Root, path);

    public string PathOf(VfpObject o)
    {
        var parts = new List<string>();
        for (var p = o; p != null && p != Root; p = p.Parent) parts.Insert(0, p.Name);
        return string.Join(".", parts);
    }

    /// <summary>All objects below the root in z-order (depth first), with their paths.</summary>
    public IEnumerable<(VfpObject Object, string Path)> Objects()
    {
        if (Root == null) yield break;
        var stack = new Stack<(VfpObject, string)>();
        foreach (var m in Enumerable.Reverse(Root.Members)) stack.Push((m, m.Name));
        while (stack.Count > 0)
        {
            var (o, p) = stack.Pop();
            yield return (o, p);
            foreach (var m in Enumerable.Reverse(o.Members)) stack.Push((m, p + "." + m.Name));
        }
    }

    /// <summary>The object's rectangle in design-surface coordinates (from layout, or from its properties before layout).</summary>
    public Rect? RectOf(VfpObject o)
    {
        if (Rendered == null) return null;
        if (o == Root) return new Rect(0, 0, Rendered.Width, Rendered.Height);
        if (o.Class.BaseClass is "DataEnvironment" or "Cursor" or "Relation") return null;
        if (o.Native is Control c && c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.TranslatePoint(new Point(0, 0), Rendered) is { } p)
            return new Rect(p, c.Bounds.Size);
        // Before layout: add up Left/Top through the containers.
        double x = 0, y = 0;
        for (var q = o; q != null && q != Root; q = q.Parent)
        {
            if (q.Class.BaseClass == "Page") { y += 30; continue; }
            if (q.Parent?.Class.BaseClass == "PageFrame" && q.Class.BaseClass != "Page") continue;
            x += Prop(q, "Left") is { Kind: ValueKind.Number } l ? l.AsNumber : 0;
            y += Prop(q, "Top") is { Kind: ValueKind.Number } t ? t.AsNumber : 0;
        }
        var w = Prop(o, "Width") is { Kind: ValueKind.Number } wv ? wv.AsNumber : 0;
        var h = Prop(o, "Height") is { Kind: ValueKind.Number } hv ? hv.AsNumber : 0;
        if (o.Native is Control { IsEffectivelyVisible: false }) return null;
        return new Rect(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    private static bool IsHidden(VfpObject o)
    {
        // Objects on pages other than the active one are not on the surface.
        for (var q = o; q?.Parent != null; q = q.Parent)
        {
            if (q.Class.BaseClass != "Page" || q.Parent.Class.BaseClass != "PageFrame") continue;
            var active = Prop(q.Parent, "ActivePage") is { Kind: ValueKind.Number } a && a.AsNumber >= 1 ? (int)a.AsNumber : 1;
            var index = q.Parent.Members.Where(m => m.Class.BaseClass == "Page").ToList().IndexOf(q) + 1;
            if (index != active) return true;
        }
        return false;
    }

    /// <summary>The object under a point: the deepest one, and the topmost among siblings. Pages select their page frame.</summary>
    public string HitTest(Point p)
    {
        string best = "";
        int bestDepth = -1;
        foreach (var (o, path) in Objects())
        {
            if (IsHidden(o) || o.Class.BaseClass is "Page" or "Column" or "Header") continue;
            if (o.Parent != null && o.Parent != Root && o.Parent.Class.BaseClass is "Grid" or "Column" or "OptionGroup" or "CommandGroup") continue;
            if (RectOf(o) is not { } r || !r.Contains(p)) continue;
            var depth = path.Count(ch => ch == '.');
            if (depth >= bestDepth) { best = path; bestDepth = depth; }
        }
        return best;
    }

    /// <summary>The container a new object dropped at a point goes into (a page frame's active page, a container, or the form).</summary>
    public string ContainerAt(Point p)
    {
        string best = "";
        int bestDepth = -1;
        foreach (var (o, path) in Objects())
        {
            if (IsHidden(o)) continue;
            var target = o.Class.BaseClass switch
            {
                "Container" or "Control" => path,
                "Page" when o.Parent?.Class.BaseClass == "PageFrame" => path,
                _ => null,
            };
            if (target == null || RectOf(o.Class.BaseClass == "Page" ? o.Parent! : o) is not { } r || !r.Contains(p)) continue;
            var depth = path.Count(ch => ch == '.');
            if (depth > bestDepth) { best = target; bestDepth = depth; }
        }
        return best;
    }

    // ================================================================================
    // Selection and adorners
    // ================================================================================

    public void Select(IEnumerable<string> paths)
    {
        CommitCode();
        _selection.Clear();
        foreach (var p in paths) if (Find(p) != null && !_selection.Contains(p, StringComparer.OrdinalIgnoreCase)) _selection.Add(p);
        if (_selection.Count == 0) _selection.Add("");
        // Selecting a page switches the page frame to it, as in VFP.
        if (Find(_selection[0]) is { Class.BaseClass: "Page", Parent: { } frame } page && frame.Native is TabControl tabs)
            tabs.SelectedIndex = frame.Members.Where(m => m.Class.BaseClass == "Page").ToList().IndexOf(page);
        ShowSelection();
    }

    private void ShowSelection()
    {
        if (Root != null && Find(_selection[0]) is { } primary)
            Properties.Show(Session, primary, _selection[0], Objects().Select(x => x.Path), Session.ClassName);
        else Properties.Clear();
        UpdateAdorners();
        Dispatcher.UIThread.Post(UpdateAdorners, DispatcherPriority.Loaded);
    }

    private const double HandleSize = 7;

    private void UpdateAdorners()
    {
        _adorners.Children.Clear();
        for (int i = 0; i < _selection.Count; i++)
        {
            if (_selection[i].Length == 0 || Find(_selection[i]) is not { } o || RectOf(o) is not { } r) continue;
            var outline = new Rectangle
            {
                Width = r.Width + 2, Height = r.Height + 2,
                Stroke = Brushes.DodgerBlue, StrokeThickness = 1,
                StrokeDashArray = i == 0 ? null : new AvaloniaList<double> { 3, 2 },
            };
            Canvas.SetLeft(outline, r.X - 1);
            Canvas.SetTop(outline, r.Y - 1);
            _adorners.Children.Add(outline);
            if (i != 0) continue;
            foreach (var (hx, hy) in HandlePoints(r))
            {
                var h = new Rectangle { Width = HandleSize, Height = HandleSize, Fill = Brushes.White, Stroke = Brushes.DodgerBlue, StrokeThickness = 1 };
                Canvas.SetLeft(h, hx - HandleSize / 2);
                Canvas.SetTop(h, hy - HandleSize / 2);
                _adorners.Children.Add(h);
            }
        }
    }

    // Handles: 0 top-left, 1 top, 2 top-right, 3 right, 4 bottom-right, 5 bottom, 6 bottom-left, 7 left.
    private static (double X, double Y)[] HandlePoints(Rect r) =>
    [
        (r.Left, r.Top), (r.Center.X, r.Top), (r.Right, r.Top), (r.Right, r.Center.Y),
        (r.Right, r.Bottom), (r.Center.X, r.Bottom), (r.Left, r.Bottom), (r.Left, r.Center.Y),
    ];

    private int HandleAt(Point p)
    {
        if (_selection.Count == 0 || _selection[0].Length == 0 || Find(_selection[0]) is not { } o || RectOf(o) is not { } r) return -1;
        var pts = HandlePoints(r);
        for (int i = 0; i < pts.Length; i++)
            if (Math.Abs(p.X - pts[i].X) <= HandleSize / 2 + 1 && Math.Abs(p.Y - pts[i].Y) <= HandleSize / 2 + 1) return i;
        return -1;
    }

    // ================================================================================
    // Mouse
    // ================================================================================

    private enum DragMode { None, Move, Resize, Band, Create }
    private DragMode _drag;
    private Point _dragStart;
    private int _handle;
    private bool _dragged;
    private readonly List<(string Path, double Left, double Top, double Width, double Height)> _originals = new();
    private Rectangle? _band;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _overlay.Focus();
        var p = e.GetPosition(_overlay);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.ClickCount == 2)
        {
            var hit2 = HitTest(p);
            OpenMethod(hit2, DefaultEvent(Find(hit2)));
            return;
        }
        PointerDown(p, shift);
        e.Pointer.Capture(_overlay);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_drag == DragMode.None) return;
        PointerMove(e.GetPosition(_overlay));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        PointerUp(e.GetPosition(_overlay));
    }

    /// <summary>Mouse button down on the surface (public so tests and touch input can drive the designer).</summary>
    public void PointerDown(Point p, bool extend = false)
    {
        _dragStart = p;
        _dragged = false;
        if (ArmedTool != null) { _drag = DragMode.Create; return; }
        _handle = HandleAt(p);
        if (_handle >= 0) { _drag = DragMode.Resize; RememberOriginals(); return; }
        var hit = HitTest(p);
        if (hit.Length == 0)
        {
            if (!extend) Select([""]);
            _drag = DragMode.Band;
            return;
        }
        if (extend)
        {
            var list = _selection.Where(s => s.Length > 0).ToList();
            if (list.Contains(hit, StringComparer.OrdinalIgnoreCase)) list.RemoveAll(s => s.Equals(hit, StringComparison.OrdinalIgnoreCase));
            else list.Add(hit);
            Select(list);
        }
        else if (!_selection.Contains(hit, StringComparer.OrdinalIgnoreCase)) Select([hit]);
        _drag = DragMode.Move;
        RememberOriginals();
    }

    private void RememberOriginals()
    {
        _originals.Clear();
        foreach (var path in _selection.Where(s => s.Length > 0))
        {
            var o = Find(path)!;
            double N(string n) => Prop(o, n) is { Kind: ValueKind.Number } v ? v.AsNumber : 0;
            _originals.Add((path, N("Left"), N("Top"), N("Width"), N("Height")));
        }
    }

    private double Snap(double v) => SnapToGrid && GridSize > 1 ? Math.Round(v / GridSize) * GridSize : Math.Round(v);

    public void PointerMove(Point p)
    {
        var dx = p.X - _dragStart.X;
        var dy = p.Y - _dragStart.Y;
        if (Math.Abs(dx) + Math.Abs(dy) >= 3) _dragged = true;
        if (!_dragged) return;
        switch (_drag)
        {
            case DragMode.Move:
                foreach (var (path, left, top, w, h) in _originals)
                    if (Find(path)?.Native is Control c) { Canvas.SetLeft(c, Snap(left + dx)); Canvas.SetTop(c, Snap(top + dy)); }
                UpdateAdorners();
                break;
            case DragMode.Resize:
                if (_originals.Count > 0 && Find(_originals[0].Path)?.Native is Control rc)
                {
                    var r = Resized(_originals[0], dx, dy);
                    Canvas.SetLeft(rc, r.X); Canvas.SetTop(rc, r.Y); rc.Width = r.Width; rc.Height = r.Height;
                    UpdateAdorners();
                }
                break;
            case DragMode.Band or DragMode.Create:
                if (_band == null) { _band = new Rectangle { Stroke = Brushes.DodgerBlue, StrokeDashArray = new AvaloniaList<double> { 4, 2 }, StrokeThickness = 1 }; _adorners.Children.Add(_band); }
                var rect = new Rect(_dragStart, p).Normalize();
                Canvas.SetLeft(_band, rect.X); Canvas.SetTop(_band, rect.Y); _band.Width = rect.Width; _band.Height = rect.Height;
                break;
        }
    }

    private Rect Resized((string Path, double Left, double Top, double Width, double Height) o, double dx, double dy)
    {
        double l = o.Left, t = o.Top, w = o.Width, h = o.Height;
        if (_handle is 0 or 6 or 7) { l = Snap(o.Left + dx); w = o.Width + (o.Left - l); }
        if (_handle is 2 or 3 or 4) w = Snap(o.Left + o.Width + dx) - o.Left;
        if (_handle is 0 or 1 or 2) { t = Snap(o.Top + dy); h = o.Height + (o.Top - t); }
        if (_handle is 4 or 5 or 6) h = Snap(o.Top + o.Height + dy) - o.Top;
        return new Rect(l, t, Math.Max(4, w), Math.Max(4, h));
    }

    public void PointerUp(Point p)
    {
        var mode = _drag;
        _drag = DragMode.None;
        if (_band != null) { _adorners.Children.Remove(_band); _band = null; }
        var dx = p.X - _dragStart.X;
        var dy = p.Y - _dragStart.Y;
        switch (mode)
        {
            case DragMode.Create:
            {
                var tool = ArmedTool!;
                var rect = _dragged ? new Rect(_dragStart, p).Normalize() : new Rect(_dragStart, new Size(0, 0));
                AddObjectAt(tool, rect.TopLeft, _dragged && rect.Width > 4 && rect.Height > 4 ? rect.Size : null);
                ArmedTool = null;
                _toolbox.SelectedIndex = 0;
                break;
            }
            case DragMode.Move when _dragged:
            {
                // Snap the dragged object and keep the others at the same distance from it.
                var first = _originals[0];
                var sdx = Snap(first.Left + dx) - first.Left;
                var sdy = Snap(first.Top + dy) - first.Top;
                Session.SetProperties("Move", _originals.SelectMany(o => new[]
                {
                    (o.Path, "Left", (string?)Literal.FormatNumber(o.Left + sdx)),
                    (o.Path, "Top", (string?)Literal.FormatNumber(o.Top + sdy)),
                }));
                break;
            }
            case DragMode.Resize when _dragged && _originals.Count > 0:
            {
                var o = _originals[0];
                var r = Resized(o, dx, dy);
                var changes = new List<(string, string, string?)>();
                if (r.X != o.Left) changes.Add((o.Path, "Left", Literal.FormatNumber(r.X)));
                if (r.Y != o.Top) changes.Add((o.Path, "Top", Literal.FormatNumber(r.Y)));
                if (r.Width != o.Width) changes.Add((o.Path, "Width", Literal.FormatNumber(r.Width)));
                if (r.Height != o.Height) changes.Add((o.Path, "Height", Literal.FormatNumber(r.Height)));
                Session.SetProperties("Resize", changes);
                break;
            }
            case DragMode.Band when _dragged:
            {
                var band = new Rect(_dragStart, p).Normalize();
                var hits = Objects().Where(x => x.Object.Parent == Root && RectOf(x.Object) is { } r && band.Intersects(r)).Select(x => x.Path).ToList();
                Select(hits);
                break;
            }
            default:
                UpdateAdorners();
                break;
        }
    }

    private static string DefaultEvent(VfpObject? o) => o?.Class.BaseClass switch
    {
        null or "Form" => "Init",
        "TextBox" or "EditBox" or "Spinner" => "Valid",
        "ComboBox" or "ListBox" or "CheckBox" or "OptionGroup" => "InteractiveChange",
        "Timer" => "Timer",
        "Grid" => "AfterRowColChange",
        "DataEnvironment" => "BeforeOpenTables",
        _ => "Click",
    };

    // ================================================================================
    // Commands
    // ================================================================================

    private static readonly Dictionary<string, (double W, double H)> DefaultSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Label"] = (60, 17), ["TextBox"] = (100, 23), ["EditBox"] = (100, 53), ["CommandButton"] = (84, 27), ["CheckBox"] = (72, 17),
        ["OptionGroup"] = (71, 46), ["CommandGroup"] = (94, 64), ["ComboBox"] = (100, 24), ["ListBox"] = (100, 170), ["Spinner"] = (120, 24),
        ["Grid"] = (320, 200), ["Image"] = (100, 100), ["Shape"] = (100, 50), ["Line"] = (100, 0), ["Container"] = (200, 100),
        ["PageFrame"] = (240, 170),
    };

    /// <summary>Adds an object from the toolbox at a surface point (into the container there). Returns its path.</summary>
    public string AddObjectAt(string className, Point p, Size? size = null)
    {
        var parent = ContainerAt(p);
        var origin = parent.Length == 0 || Find(parent) is not { } po || RectOf(po.Class.BaseClass == "Page" ? po.Parent! : po) is not { } pr
            ? new Point(0, 0)
            : po.Class.BaseClass == "Page" ? new Point(pr.X, pr.Y + TabStripHeight(po.Parent!)) : pr.TopLeft;
        var (dw, dh) = DefaultSizes.TryGetValue(className, out var d) ? d : (100, 25);
        var props = new List<KeyValuePair<string, string>>
        {
            new("Left", Literal.FormatNumber(Snap(p.X - origin.X))),
            new("Top", Literal.FormatNumber(Snap(p.Y - origin.Y))),
        };
        if (className is not "Timer")
        {
            props.Add(new("Width", Literal.FormatNumber(size?.Width is > 0 ? Snap(size.Value.Width) : dw)));
            props.Add(new("Height", Literal.FormatNumber(size?.Height is > 0 ? Snap(size.Value.Height) : dh)));
        }
        var name = Session.NewName(parent, DesignSession.DefaultNamePrefix(className));
        if (className is "Label" or "CommandButton" or "CheckBox") props.Add(new("Caption", Literal.Quote(name)));
        if (className == "PageFrame") props.Add(new("PageCount", "2"));
        var path = Session.AddObject(parent, className, null, props, name);
        Select([path]);
        return path;
    }

    private static double TabStripHeight(VfpObject pageFrame) =>
        pageFrame.Native is TabControl tabs && tabs.SelectedContent is Control content && content.TranslatePoint(new Point(0, 0), tabs) is { } p ? p.Y : 30;

    public void DeleteSelection()
    {
        var paths = _selection.Where(s => s.Length > 0 && Session.IsDeclared(s)).ToList();
        if (paths.Count == 0) return;
        Session.Transaction("Delete", () => { foreach (var p in paths) Session.RemoveObject(p); });
        Select([""]);
    }

    public void MoveSelection(double dx, double dy) =>
        Session.SetProperties("Move", _selection.Where(s => s.Length > 0).SelectMany(path =>
        {
            var o = Find(path)!;
            double N(string n) => Prop(o, n) is { Kind: ValueKind.Number } v ? v.AsNumber : 0;
            return new[] { (path, "Left", (string?)Literal.FormatNumber(N("Left") + dx)), (path, "Top", (string?)Literal.FormatNumber(N("Top") + dy)) };
        }));

    public void ResizeSelection(double dw, double dh) =>
        Session.SetProperties("Resize", _selection.Where(s => s.Length > 0).SelectMany(path =>
        {
            var o = Find(path)!;
            double N(string n) => Prop(o, n) is { Kind: ValueKind.Number } v ? v.AsNumber : 0;
            return new[] { (path, "Width", (string?)Literal.FormatNumber(Math.Max(4, N("Width") + dw))), (path, "Height", (string?)Literal.FormatNumber(Math.Max(4, N("Height") + dh))) };
        }));

    /// <summary>Align and size commands for several selected objects: the first selected object is the reference.</summary>
    public void Align(string how)
    {
        var items = _selection.Where(s => s.Length > 0).Select(p => (Path: p, O: Find(p)!)).ToList();
        if (items.Count == 0) return;
        double N(VfpObject o, string n) => Prop(o, n) is { Kind: ValueKind.Number } v ? v.AsNumber : 0;
        var reference = items[0].O;
        var changes = new List<(string, string, string?)>();
        foreach (var (path, o) in items)
        {
            void Set(string prop, double value) => changes.Add((path, prop, Literal.FormatNumber(value)));
            switch (how)
            {
                case "Left": Set("Left", N(reference, "Left")); break;
                case "Right": Set("Left", N(reference, "Left") + N(reference, "Width") - N(o, "Width")); break;
                case "Top": Set("Top", N(reference, "Top")); break;
                case "Bottom": Set("Top", N(reference, "Top") + N(reference, "Height") - N(o, "Height")); break;
                case "SameWidth": Set("Width", N(reference, "Width")); break;
                case "SameHeight": Set("Height", N(reference, "Height")); break;
                case "CenterHorizontally":
                {
                    var container = o.Parent == Root || o.Parent == null ? N(Root!, "Width") : N(o.Parent, "Width");
                    Set("Left", Math.Round((container - N(o, "Width")) / 2));
                    break;
                }
                case "CenterVertically":
                {
                    var container = o.Parent == Root || o.Parent == null ? N(Root!, "Height") : N(o.Parent, "Height");
                    Set("Top", Math.Round((container - N(o, "Height")) / 2));
                    break;
                }
            }
        }
        Session.SetProperties("Align " + how, changes);
    }

    public void ZOrder(bool toFront)
    {
        foreach (var p in _selection.Where(s => s.Length > 0 && Session.IsDeclared(s))) Session.ChangeZOrder(p, toFront);
    }

    public void CopySelection()
    {
        var paths = _selection.Where(s => s.Length > 0 && Session.IsDeclared(s)).ToList();
        if (paths.Count > 0) s_clipboard = Session.Copy(paths);
    }

    public void CutSelection()
    {
        CopySelection();
        DeleteSelection();
    }

    public void Paste()
    {
        if (s_clipboard == null) return;
        var target = _selection.Count == 1 && Find(_selection[0]) is { } o && o.Class.BaseClass is "Container" or "Page" ? _selection[0] : "";
        var added = Session.Paste(s_clipboard, target, GridSize);
        if (added.Count > 0) Select(added);
    }

    public void Undo() { CommitCode(); Session.Undo(); }
    public void Redo() { CommitCode(); Session.Redo(); }

    private void OnKey(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var step = ctrl ? GridSize : 1;
        Action? act = (e.Key, ctrl, shift) switch
        {
            (Key.Delete, _, _) => DeleteSelection,
            (Key.Left, _, false) => () => MoveSelection(-step, 0),
            (Key.Right, _, false) => () => MoveSelection(step, 0),
            (Key.Up, _, false) => () => MoveSelection(0, -step),
            (Key.Down, _, false) => () => MoveSelection(0, step),
            (Key.Left, _, true) => () => ResizeSelection(-step, 0),
            (Key.Right, _, true) => () => ResizeSelection(step, 0),
            (Key.Up, _, true) => () => ResizeSelection(0, -step),
            (Key.Down, _, true) => () => ResizeSelection(0, step),
            (Key.Z, true, false) => Undo,
            (Key.Y, true, _) or (Key.Z, true, true) => Redo,
            (Key.C, true, _) => CopySelection,
            (Key.X, true, _) => CutSelection,
            (Key.V, true, _) => Paste,
            (Key.A, true, _) => () => Select(Objects().Where(x => x.Object.Parent == Root).Select(x => x.Path)),
            (Key.Escape, _, _) => () => { ArmedTool = null; _toolbox.SelectedIndex = 0; Select([ParentPath(_selection[0])]); },
            (Key.Tab, _, _) => () => SelectNext(shift ? -1 : 1),
            _ => null,
        };
        if (act == null) return;
        act();
        e.Handled = true;
    }

    private static string ParentPath(string path) => path.Contains('.') ? path[..path.LastIndexOf('.')] : "";

    private void SelectNext(int direction)
    {
        var all = Objects().Where(x => !IsHidden(x.Object) && x.Object.Class.BaseClass is not ("Page" or "Column" or "Header")).Select(x => x.Path).ToList();
        if (all.Count == 0) return;
        var i = all.FindIndex(p => p.Equals(_selection[0], StringComparison.OrdinalIgnoreCase));
        Select([all[((i + direction) % all.Count + all.Count) % all.Count]]);
    }

    // ================================================================================
    // Method code
    // ================================================================================

    /// <summary>Shows a method's code in the code pane below the surface.</summary>
    public void OpenMethod(string path, string method)
    {
        CommitCode();
        _codePath = path;
        _codeMethod = method;
        _codeOriginal = Session.GetMethod(path, method) ?? "";
        _code.Text = _codeOriginal;
        _codeTitle.Text = (path.Length == 0 ? Session.ClassName : path) + "." + method;
        _centerGrid.RowDefinitions[2].Height = new GridLength(2, GridUnitType.Star);
        _code.Focus();
    }

    public string? OpenMethodName => _codeMethod == null ? null : (_codePath!.Length == 0 ? "" : _codePath + ".") + _codeMethod;

    public TextEditor CodeEditor => _code;

    /// <summary>Stores the code pane's text in the document when it changed.</summary>
    public void CommitCode()
    {
        if (_codeMethod == null || _code.Text == _codeOriginal) return;
        _codeOriginal = _code.Text;
        Session.SetMethod(_codePath!, _codeMethod, _code.Text);
    }

    // ================================================================================
    // Data environment
    // ================================================================================

    private const string DataEnvironmentName = "Dataenvironment";

    /// <summary>Adds a table to the data environment (DE cursor) and returns its alias.</summary>
    public string AddTable(string tablePath, string? alias = null)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(tablePath);
        alias ??= name;
        var formDir = FilePath != null ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(FilePath))! : _rt.Options.Default_;
        var source = tablePath.Contains('!') ? tablePath[(tablePath.IndexOf('!') + 1)..] : System.IO.Path.GetRelativePath(formDir, System.IO.Path.GetFullPath(tablePath)).Replace('\\', '/');
        string? database = tablePath.Contains('!') ? tablePath[..tablePath.IndexOf('!')] : null;
        Session.Transaction("Add table", () =>
        {
            if (Session.Class.FindMember(DataEnvironmentName) == null) Session.AddObject("", "DataEnvironment", name: DataEnvironmentName);
            var props = new List<KeyValuePair<string, string>> { new("Alias", Literal.Quote(alias)), new("CursorSource", Literal.Quote(source)) };
            if (database != null) props.Add(new("Database", Literal.Quote(database)));
            Session.AddObject(DataEnvironmentName, "Cursor", null, props, Session.NewName(DataEnvironmentName, "Cursor"));
        });
        return alias;
    }

    /// <summary>The fields of a data environment cursor (opened read-only in the designer's own data session).</summary>
    public IReadOnlyList<FieldDef> FieldsOf(string alias)
    {
        var wa = _rt.Session.FindAlias(alias);
        if (wa == null && Root != null && DataEnvironmentCursor(alias) is { } cursor)
        {
            try
            {
                var source = Prop(cursor, "CursorSource").AsString.Trim();
                var db = Prop(cursor, "Database") is { Kind: ValueKind.Character } d && d.AsString.Trim().Length > 0 ? d.AsString.Trim() : null;
                var formDir = FilePath != null ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(FilePath))! : _rt.Options.Default_;
                var target = db != null ? db + "!" + source : System.IO.Path.GetFullPath(System.IO.Path.Combine(formDir, source));
                if (db != null && !_rt.Session.OpenDatabases.Any(x => x.Name.Equals(System.IO.Path.GetFileNameWithoutExtension(db), StringComparison.OrdinalIgnoreCase)))
                    _rt.Session.OpenDatabase(System.IO.Path.Combine(formDir, db));
                _rt.ExecuteCommand($"USE \"{target}\" ALIAS {alias} AGAIN IN 0 NOUPDATE");
                wa = _rt.Session.FindAlias(alias);
            }
            catch (VfpException ex) { Status?.Invoke($"Cannot open {alias}: {ex.Message}"); }
        }
        return wa?.Table.Fields ?? (IReadOnlyList<FieldDef>)[];
    }

    private VfpObject? DataEnvironmentCursor(string alias) =>
        Root?.FindProperty(DataEnvironmentName)?.Value is { Kind: ValueKind.Object } de
            ? ((VfpObject)de.AsObject).Members.FirstOrDefault(m => m.Class.BaseClass == "Cursor" && Prop(m, "Alias").AsString.Trim().Equals(alias, StringComparison.OrdinalIgnoreCase))
            : null;

    private IEnumerable<string> DataEnvironmentAliases() =>
        Root?.FindProperty(DataEnvironmentName)?.Value is { Kind: ValueKind.Object } de
            ? ((VfpObject)de.AsObject).Members.Where(m => m.Class.BaseClass == "Cursor").Select(m => Prop(m, "Alias").AsString.Trim()).Where(a => a.Length > 0)
            : [];

    private void RefreshDataEnvironment()
    {
        _dataEnvironment.Items.Clear();
        foreach (var alias in DataEnvironmentAliases())
        {
            var node = new TreeViewItem { Header = alias, Tag = (alias, (string?)null), IsExpanded = true };
            foreach (var f in FieldsOf(alias)) node.Items.Add(new TreeViewItem { Header = $"{f.Name.ToLowerInvariant()}  {f.Type}", Tag = (alias, (string?)f.Name) });
            _dataEnvironment.Items.Add(node);
        }
    }

    private void AddFromDataEnvironment()
    {
        if (_dataEnvironment.SelectedItem is not TreeViewItem { Tag: ValueTuple<string, string?> tag }) return;
        var (alias, field) = tag;
        if (field == null) AddGridFor(alias);
        else AddFieldControls(alias, field);
    }

    /// <summary>The next free spot below the objects on the form (for controls added from the data environment).</summary>
    private Point NextFreePoint()
    {
        var bottom = Objects().Where(x => x.Object.Parent == Root && !IsHidden(x.Object)).Select(x => RectOf(x.Object)).OfType<Rect>().Select(r => r.Bottom).DefaultIfEmpty(0).Max();
        return new Point(GridSize, Snap(bottom + GridSize));
    }

    /// <summary>A label and a control bound to a field (a check box for logical fields, an edit box for memos).</summary>
    public (string Label, string Control) AddFieldControls(string alias, string field, Point? at = null)
    {
        var f = FieldsOf(alias).FirstOrDefault(x => x.Name.Equals(field, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"{alias} has no field {field}.");
        var p = at ?? NextFreePoint();
        string label = "", control = "";
        Session.Transaction($"Add {field}", () =>
        {
            var caption = f.Caption ?? char.ToUpperInvariant(f.Name[0]) + f.Name[1..].ToLowerInvariant();
            var controlClass = f.Type switch { 'L' => "CheckBox", 'M' => "EditBox", _ => "TextBox" };
            if (controlClass != "CheckBox")
            {
                label = Session.AddObject("", "Label", null,
                [
                    new("Left", Literal.FormatNumber(p.X)), new("Top", Literal.FormatNumber(p.Y + 3)), new("Width", "90"), new("Height", "17"),
                    new("Caption", Literal.Quote(caption)),
                ], Unique("lbl" + char.ToUpperInvariant(f.Name[0]) + f.Name[1..].ToLowerInvariant()));
            }
            var width = f.Type switch { 'D' => 80, 'T' => 140, 'L' => 120, 'M' => 240, 'I' or 'N' or 'Y' or 'F' or 'B' => 90, _ => Math.Clamp(f.Width * 7 + 10, 50, 300) };
            var props = new List<KeyValuePair<string, string>>
            {
                new("Left", Literal.FormatNumber(p.X + (controlClass == "CheckBox" ? 0 : 100))), new("Top", Literal.FormatNumber(p.Y)),
                new("Width", width.ToString()), new("Height", controlClass == "EditBox" ? "53" : controlClass == "CheckBox" ? "17" : "23"),
                new("ControlSource", Literal.Quote(alias.ToLowerInvariant() + "." + f.Name.ToLowerInvariant())),
            };
            if (controlClass == "CheckBox") props.Add(new("Caption", Literal.Quote(caption)));
            var prefix = controlClass switch { "CheckBox" => "chk", "EditBox" => "edt", _ => "txt" };
            control = Session.AddObject("", controlClass, null, props, Unique(prefix + char.ToUpperInvariant(f.Name[0]) + f.Name[1..].ToLowerInvariant()));
        });
        Select([control]);
        return (label, control);
    }

    /// <summary>A name for a new object on the form: the preferred one, numbered if it is taken.</summary>
    private string Unique(string name) => Session.Class.FindMember(name) == null ? name : Session.NewName("", name);

    /// <summary>A grid showing a data environment table.</summary>
    public string AddGridFor(string alias, Point? at = null)
    {
        var p = at ?? NextFreePoint();
        var path = Session.AddObject("", "Grid", null,
        [
            new("Left", Literal.FormatNumber(p.X)), new("Top", Literal.FormatNumber(p.Y)), new("Width", "320"), new("Height", "200"),
            new("RecordSource", Literal.Quote(alias.ToLowerInvariant())),
        ], Unique("grd" + char.ToUpperInvariant(alias[0]) + alias[1..].ToLowerInvariant()));
        Select([path]);
        return path;
    }

    // ================================================================================
    // Files
    // ================================================================================

    public void Save(string? path = null)
    {
        CommitCode();
        FilePath = path ?? FilePath ?? throw new InvalidOperationException("No file name.");
        ClassFileWriter.Save(Session.File, FilePath);
        Session.MarkSaved();
        Saved?.Invoke();
        Status?.Invoke($"Saved {System.IO.Path.GetFileName(FilePath)}.");
    }

    public void Run()
    {
        if (FilePath == null) { Status?.Invoke("Save the form before running it."); return; }
        Save();
        RunRequested?.Invoke(FilePath);
    }

    private Control BuildToolbar()
    {
        Button B(string text, string tip, Action act)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 2), FontSize = 12 };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => act();
            return b;
        }
        var snap = new CheckBox { Content = "Snap to grid", IsChecked = SnapToGrid, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        snap.IsCheckedChanged += (_, _) => SnapToGrid = snap.IsChecked == true;
        return new WrapPanel
        {
            Margin = new Thickness(4),
            Children =
            {
                B("Save", "Save the form (Ctrl+S)", () => Save()),
                B("▶ Run", "Save and run the form (Ctrl+E)", Run),
                B("↶", "Undo (Ctrl+Z)", Undo),
                B("↷", "Redo (Ctrl+Y)", Redo),
                B("⇤", "Align left edges", () => Align("Left")),
                B("⇥", "Align right edges", () => Align("Right")),
                B("⤒", "Align top edges", () => Align("Top")),
                B("⤓", "Align bottom edges", () => Align("Bottom")),
                B("↔", "Same width", () => Align("SameWidth")),
                B("↕", "Same height", () => Align("SameHeight")),
                B("⊟", "Center horizontally", () => Align("CenterHorizontally")),
                B("Front", "Bring to front", () => ZOrder(true)),
                B("Back", "Send to back", () => ZOrder(false)),
                snap,
            },
        };
    }

    public void SetDark(bool dark) => _code.SyntaxHighlighting = FoxProHighlighting.Get(dark);
}
