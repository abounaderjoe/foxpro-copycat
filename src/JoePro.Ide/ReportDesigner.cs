using System.Reflection;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using JoePro.Core;
using JoePro.Documents.Reports;
using JoePro.Language;
using JoePro.Reports;
using JoePro.Runtime;
using JoePro.Ui.Runtime;

namespace JoePro.Ide;

/// <summary>A report (.jpreport) or label (.jplabel) in the Report/Label Designer.</summary>
public sealed class ReportDesignerTab : DocumentTab
{
    public ReportDesignerTab(string? path, ReportDocument doc, IdeSession session, bool dark)
    {
        Designer = new ReportDesigner(path, doc, session, dark);
        Designer.Session.Changed += UpdateTitle;
        Designer.Saved += UpdateTitle;
        Content = Designer;
        UpdateTitle();
    }

    public ReportDesigner Designer { get; }

    private void UpdateTitle() =>
        Title = (Designer.FilePath != null ? System.IO.Path.GetFileName(Designer.FilePath) : Designer.Session.Document.Kind == ReportKind.Label ? "label1.jplabel" : "report1.jpreport")
                + (Designer.Session.IsDirty ? " •" : "");
}

/// <summary>
/// The Report Designer: bands stacked as on paper (drag a band's bar to change its height), a toolbox, the fields of
/// the data environment, a property grid, and a live preview that re-runs the report with real data after each
/// change. All edits go through <see cref="ReportDesignSession"/> (undo/redo, canonical files).
/// </summary>
public sealed class ReportDesigner : UserControl
{
    public const double Dpi = 96;
    public const double BarHeight = 18;
    private readonly IdeSession _ide;
    private readonly Canvas _surface = new() { Background = Brushes.White, Focusable = true, ClipToBounds = true };
    private readonly Canvas _adorners = new() { IsHitTestVisible = false };
    private readonly ListBox _toolbox = new();
    private readonly ListBox _fields = new() { MinHeight = 60 };
    private readonly StackPanel _props = new();
    private readonly TextBlock _propsTitle = new() { FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 4), TextWrapping = TextWrapping.Wrap };
    private readonly ContentControl _previewHost = new();
    private readonly TextBlock _previewMessage = new() { Margin = new Thickness(8), Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private readonly Grid _center;
    private readonly List<ObjectRef> _selection = new();
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private static string? s_clipboard;
    private int _lastBand = -1;

    public static readonly string[] Tools = ["Label", "Field", "Line", "Rectangle", "Rounded rectangle", "Picture"];

    public ReportDesigner(string? path, ReportDocument doc, IdeSession ide, bool dark)
    {
        _ide = ide;
        FilePath = path;
        Session = new ReportDesignSession(doc);
        Session.Changed += () => { Render(); SchedulePreview(); };

        ListBoxItem Item(string text, string tag) => new() { Content = text, Tag = tag, Padding = new Thickness(10, 3), MinHeight = 0, FontSize = 13 };
        _toolbox.Items.Add(Item("➤ Pointer", ""));
        foreach (var t in Tools) _toolbox.Items.Add(Item(t, t));
        _toolbox.SelectedIndex = 0;
        _toolbox.SelectionChanged += (_, _) => ArmedTool = _toolbox.SelectedItem is ListBoxItem { Tag: string t } && t.Length > 0 ? t : null;
        _fields.DoubleTapped += (_, _) => { if (_fields.SelectedItem is ListBoxItem { Tag: string f }) AddField(f); };

        var left = new DockPanel { Width = 170 };
        var tbHeader = Header("Toolbox");
        DockPanel.SetDock(tbHeader, Dock.Top);
        left.Children.Add(tbHeader);
        DockPanel.SetDock(_toolbox, Dock.Top);
        left.Children.Add(_toolbox);
        var fHeader = Header("Fields (double-click)");
        DockPanel.SetDock(fHeader, Dock.Top);
        left.Children.Add(fHeader);
        left.Children.Add(_fields);

        var surfaceHost = new Panel { Children = { _surface, _adorners }, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16) };
        var scroller = new ScrollViewer
        {
            Content = surfaceHost,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)),
        };
        _surface.PointerPressed += (_, e) =>
        {
            _surface.Focus();
            var p = e.GetPosition(_surface);
            if (e.ClickCount == 2) { EditSelectedText(); return; }
            PointerDown(p, e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Control));
            e.Pointer.Capture(_surface);
            e.Handled = true;
        };
        _surface.PointerMoved += (_, e) => { if (_drag != Drag.None) PointerMove(e.GetPosition(_surface)); };
        _surface.PointerReleased += (_, e) => { e.Pointer.Capture(null); PointerUp(e.GetPosition(_surface)); };
        _surface.KeyDown += (_, e) => OnKey(e);

        var previewPane = new DockPanel();
        var previewHeader = Header("Live preview");
        DockPanel.SetDock(previewHeader, Dock.Top);
        previewPane.Children.Add(previewHeader);
        DockPanel.SetDock(_previewMessage, Dock.Top);
        previewPane.Children.Add(_previewMessage);
        previewPane.Children.Add(_previewHost);
        _center = new Grid { RowDefinitions = new RowDefinitions("3*,Auto,2*") };
        _center.Children.Add(scroller);
        var split = new GridSplitter { Height = 5, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(split, 1);
        _center.Children.Add(split);
        Grid.SetRow(previewPane, 2);
        _center.Children.Add(previewPane);

        var right = new DockPanel { Width = 300 };
        DockPanel.SetDock(_propsTitle, Dock.Top);
        right.Children.Add(_propsTitle);
        right.Children.Add(new ScrollViewer { Content = _props });

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        body.Children.Add(left);
        Grid.SetColumn(_center, 1);
        body.Children.Add(_center);
        var rightBorder = new Border { Child = right, BorderThickness = new Thickness(1, 0, 0, 0), BorderBrush = Brushes.Gray };
        Grid.SetColumn(rightBorder, 2);
        body.Children.Add(rightBorder);
        var toolbar = BuildToolbar();
        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(body);
        Content = root;

        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RefreshPreview(); };
        Render();
        RefreshFields();
    }

    private static TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 4) };

    public ReportDesignSession Session { get; }
    public string? FilePath { get; private set; }
    public IReadOnlyList<ObjectRef> Selection => _selection;
    public string? ArmedTool { get; set; }
    /// <summary>Snap positions to 1/16 inch.</summary>
    public bool SnapToGrid { get; set; } = true;
    public const double GridStep = 0.0625;
    /// <summary>The last live preview (null before the first run or when it failed).</summary>
    public RenderedReport? PreviewReport { get; private set; }
    public string? PreviewError { get; private set; }
    public bool LivePreview { get; set; } = true;

    public event Action<string>? Status;
    public event Action<string>? RunRequested;
    public event Action? Saved;
    public event Action? SaveAsRequested;

    private ReportDocument Doc => Session.Document;

    // ================================================================================
    // Geometry: inches in the document, pixels on the surface
    // ================================================================================

    public double BandTopPx(int band)
    {
        double y = 0;
        for (int i = 0; i < band; i++) y += Doc.Bands[i].Height * Dpi + BarHeight;
        return y;
    }

    private double SurfaceWidth => Math.Max(Doc.PrintableWidth, Doc.Columns > 1 ? Doc.EffectiveColumnWidth : 0) * Dpi;

    /// <summary>The object's rectangle on the surface (pixels).</summary>
    public Rect RectOf(ObjectRef r)
    {
        var o = Session.Get(r)!;
        return new Rect(o.Left * Dpi, BandTopPx(r.Band) + o.Top * Dpi, Math.Max(3, o.Width * Dpi), Math.Max(3, o.Height * Dpi));
    }

    /// <summary>The band whose area (not its bar) contains a surface point, or -1.</summary>
    public int BandAtPx(double y, out bool onBar)
    {
        onBar = false;
        double top = 0;
        for (int i = 0; i < Doc.Bands.Count; i++)
        {
            var h = Doc.Bands[i].Height * Dpi;
            if (y < top + h) return i;
            if (y < top + h + BarHeight) { onBar = true; return i; }
            top += h + BarHeight;
        }
        return -1;
    }

    private double Snap(double inches) => SnapToGrid ? Math.Round(inches / GridStep) * GridStep : Math.Round(inches, 4);

    // ================================================================================
    // Rendering
    // ================================================================================

    private void Render()
    {
        _selection.RemoveAll(r => Session.Get(r) == null);
        _surface.Children.Clear();
        _surface.Width = SurfaceWidth;
        _surface.Height = BandTopPx(Doc.Bands.Count);
        for (int b = 0; b < Doc.Bands.Count; b++)
        {
            var band = Doc.Bands[b];
            var top = BandTopPx(b);
            // Dotted grid every half inch.
            for (double x = 0.5; x * Dpi < SurfaceWidth; x += 0.5)
                AddAt(new Line { StartPoint = new Point(0, 0), EndPoint = new Point(0, band.Height * Dpi), Stroke = new SolidColorBrush(Color.FromArgb(40, 0, 0, 128)), StrokeDashArray = new AvaloniaList<double> { 1, 3 } }, x * Dpi, top);
            for (int i = 0; i < band.Objects.Count; i++) AddAt(Visual(band.Objects[i]), band.Objects[i].Left * Dpi, top + band.Objects[i].Top * Dpi, band.Objects[i]);
            var bar = new Border
            {
                Width = SurfaceWidth, Height = BarHeight, Background = new SolidColorBrush(Color.FromRgb(0xC9, 0xD6, 0xE8)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x9F, 0xBF)), BorderThickness = new Thickness(0, 1),
                Child = new TextBlock { Text = "▲ " + BandLabel(band), FontSize = 11, Margin = new Thickness(6, 1), Foreground = Brushes.Black },
                Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
            };
            AddAt(bar, 0, top + band.Height * Dpi);
        }
        UpdateAdorners();
        ShowProperties();
    }

    private string BandLabel(ReportBand b) => b.Kind switch
    {
        BandKind.GroupHeader or BandKind.GroupFooter when b.Index <= Doc.Groups.Count => $"{b.DisplayName}: {Doc.Groups[b.Index - 1].Expression}",
        _ => b.DisplayName,
    } + $"  ({b.Height:0.###} in)";

    private void AddAt(Control c, double x, double y, ReportObject? tag = null)
    {
        Canvas.SetLeft(c, x);
        Canvas.SetTop(c, y);
        c.Tag = tag;
        c.IsHitTestVisible = false;
        _surface.Children.Add(c);
    }

    private static IBrush Brush(int? vfp, IBrush fallback) =>
        vfp is { } c ? new SolidColorBrush(Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF))) : fallback;

    private Control Visual(ReportObject o)
    {
        var w = Math.Max(1, o.Width * Dpi);
        var h = Math.Max(1, o.Height * Dpi);
        switch (o)
        {
            case ReportTextObject t:
            {
                var text = t is ReportLabel l ? l.Text : $"[{((ReportField)t).Expression}]";
                var tb = new TextBlock
                {
                    Text = text, Width = w, Height = h, TextTrimming = TextTrimming.CharacterEllipsis,
                    FontFamily = new FontFamily(t.FontName ?? Doc.FontName), FontSize = (t.FontSize ?? Doc.FontSize) * Dpi / 72,
                    FontWeight = t.Bold ? FontWeight.Bold : FontWeight.Normal, FontStyle = t.Italic ? FontStyle.Italic : FontStyle.Normal,
                    Foreground = Brush(t.ForeColor, t is ReportField ? new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x79)) : Brushes.Black),
                    Background = Brush(t.BackColor, Brushes.Transparent),
                    TextAlignment = t.Align switch { TextAlign.Right => TextAlignment.Right, TextAlign.Center => TextAlignment.Center, _ => TextAlignment.Left },
                    TextDecorations = t.Underline ? TextDecorations.Underline : null,
                };
                return t is ReportField
                    ? new Border { Child = tb, BorderBrush = new SolidColorBrush(Color.FromArgb(90, 31, 78, 121)), BorderThickness = new Thickness(1), Width = w, Height = h }
                    : tb;
            }
            case ReportLine line:
                return new Line
                {
                    StartPoint = new Point(0, 0), EndPoint = line.Vertical ? new Point(0, h) : new Point(w, 0),
                    Stroke = Brush(line.Color, Brushes.Black), StrokeThickness = Math.Max(1, line.PenWidth * Dpi / 72),
                    StrokeDashArray = line.PenStyle is PenStyle.Dot or PenStyle.Dash ? new AvaloniaList<double> { 3, 2 } : null,
                };
            case ReportShape s:
                return new Border
                {
                    Width = w, Height = h, CornerRadius = new CornerRadius(s.Curvature >= 99 ? Math.Min(w, h) / 2 : s.Curvature / 99.0 * Math.Min(w, h) / 2),
                    BorderBrush = s.PenStyle == PenStyle.None ? Brushes.Transparent : Brush(s.LineColor, Brushes.Black), BorderThickness = new Thickness(Math.Max(1, s.PenWidth * Dpi / 72)),
                    Background = Brush(s.FillColor, Brushes.Transparent),
                };
            case ReportPicture p:
                return new Border
                {
                    Width = w, Height = h, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.FromArgb(25, 0, 0, 0)),
                    Child = new TextBlock { Text = "🖼 " + p.Value, FontSize = 11, Margin = new Thickness(3), TextTrimming = TextTrimming.CharacterEllipsis },
                };
        }
        return new Border();
    }

    private void UpdateAdorners()
    {
        _adorners.Children.Clear();
        _adorners.Width = _surface.Width;
        _adorners.Height = _surface.Height;
        for (int i = 0; i < _selection.Count; i++)
        {
            if (Session.Get(_selection[i]) == null) continue;
            var r = RectOf(_selection[i]);
            var outline = new Rectangle { Width = r.Width + 2, Height = r.Height + 2, Stroke = Brushes.DodgerBlue, StrokeThickness = 1, StrokeDashArray = i == 0 ? null : new AvaloniaList<double> { 3, 2 } };
            Canvas.SetLeft(outline, r.X - 1);
            Canvas.SetTop(outline, r.Y - 1);
            _adorners.Children.Add(outline);
            if (i == 0)
            {
                var h = new Rectangle { Width = 7, Height = 7, Fill = Brushes.White, Stroke = Brushes.DodgerBlue };
                Canvas.SetLeft(h, r.Right - 3.5);
                Canvas.SetTop(h, r.Bottom - 3.5);
                _adorners.Children.Add(h);
            }
        }
    }

    // ================================================================================
    // Selection and mouse
    // ================================================================================

    public void Select(IEnumerable<ObjectRef> refs)
    {
        _selection.Clear();
        foreach (var r in refs) if (Session.Get(r) != null && !_selection.Contains(r)) _selection.Add(r);
        UpdateAdorners();
        ShowProperties();
    }

    /// <summary>The topmost object under a surface point.</summary>
    public ObjectRef? HitTest(Point p)
    {
        foreach (var r in Session.AllObjects().Reverse())
        {
            var rect = RectOf(r).Inflate(Session.Get(r) is ReportLine ? 3 : 0);
            if (rect.Contains(p)) return r;
        }
        return null;
    }

    private enum Drag { None, Move, Resize, Band, Create, Marquee }
    private Drag _drag;
    private Point _start;
    private int _bandDragged;
    private bool _moved;
    private Rectangle? _marquee;

    public void PointerDown(Point p, bool extend = false)
    {
        _start = p;
        _moved = false;
        var band = BandAtPx(p.Y, out var onBar);
        if (band >= 0) _lastBand = band;
        if (onBar) { _drag = Drag.Band; _bandDragged = band; return; }
        if (ArmedTool != null) { _drag = Drag.Create; return; }
        if (_selection.Count > 0 && Session.Get(_selection[0]) != null)
        {
            var r = RectOf(_selection[0]);
            if (Math.Abs(p.X - r.Right) <= 5 && Math.Abs(p.Y - r.Bottom) <= 5) { _drag = Drag.Resize; return; }
        }
        var hit = HitTest(p);
        if (hit == null)
        {
            if (!extend) Select([]);
            _drag = Drag.Marquee;
            return;
        }
        if (extend)
        {
            var list = _selection.ToList();
            if (!list.Remove(hit.Value)) list.Add(hit.Value);
            Select(list);
        }
        else if (!_selection.Contains(hit.Value)) Select([hit.Value]);
        _drag = Drag.Move;
    }

    public void PointerMove(Point p)
    {
        if (Math.Abs(p.X - _start.X) + Math.Abs(p.Y - _start.Y) >= 3) _moved = true;
        if (!_moved || _drag is not (Drag.Marquee or Drag.Create)) return;
        if (_marquee == null) { _marquee = new Rectangle { Stroke = Brushes.DodgerBlue, StrokeDashArray = new AvaloniaList<double> { 4, 2 } }; _adorners.Children.Add(_marquee); }
        var rect = new Rect(_start, p).Normalize();
        Canvas.SetLeft(_marquee, rect.X);
        Canvas.SetTop(_marquee, rect.Y);
        _marquee.Width = rect.Width;
        _marquee.Height = rect.Height;
    }

    public void PointerUp(Point p)
    {
        var drag = _drag;
        _drag = Drag.None;
        if (_marquee != null) { _adorners.Children.Remove(_marquee); _marquee = null; }
        var dx = (p.X - _start.X) / Dpi;
        var dy = (p.Y - _start.Y) / Dpi;
        switch (drag)
        {
            case Drag.Band when _moved:
                Session.SetBandHeight(_bandDragged, Snap(Doc.Bands[_bandDragged].Height + dy));
                break;
            case Drag.Create:
            {
                var tool = ArmedTool!;
                var rect = new Rect(_start, p).Normalize();
                AddObjectAt(tool, rect.TopLeft, _moved && rect.Width > 4 ? new Size(rect.Width, rect.Height) : null);
                ArmedTool = null;
                _toolbox.SelectedIndex = 0;
                break;
            }
            case Drag.Move when _moved:
            {
                var first = Session.Get(_selection[0])!;
                var sdx = Snap(first.Left + dx) - first.Left;
                var absTop = Session.BandTop(_selection[0].Band) + first.Top;
                var sdy = Snap(absTop + dy) - absTop;
                Select(Session.MoveObjects(_selection.ToList(), sdx, sdy));
                break;
            }
            case Drag.Resize when _moved:
            {
                var o = Session.Get(_selection[0])!;
                var w = o is ReportLine { Vertical: true } ? o.Width : Math.Max(GridStep, Snap(o.Width + dx));
                var h = o is ReportLine { Vertical: false } ? o.Height : Math.Max(GridStep, Snap(o.Height + dy));
                Session.SetBounds(_selection[0], o.Left, o.Top, w, h);
                break;
            }
            case Drag.Marquee when _moved:
            {
                var rect = new Rect(_start, p).Normalize();
                Select(Session.AllObjects().Where(r => rect.Intersects(RectOf(r))));
                break;
            }
        }
    }

    // ================================================================================
    // Commands
    // ================================================================================

    /// <summary>Adds an object from the toolbox at a surface point, in the band under it. Returns its reference.</summary>
    public ObjectRef? AddObjectAt(string tool, Point p, Size? size = null)
    {
        var band = BandAtPx(p.Y, out _);
        if (band < 0) band = Doc.Bands.Count - 1;
        if (band < 0) return null;
        var left = Snap(p.X / Dpi);
        var top = Snap(Math.Max(0, p.Y - BandTopPx(band)) / Dpi);
        double W(double d) => size is { Width: > 4 } s ? Snap(s.Width / Dpi) : d;
        double H(double d) => size is { Height: > 4 } s ? Snap(s.Height / Dpi) : d;
        ReportObject o = tool switch
        {
            "Label" => new ReportLabel { Text = "Label", Width = W(0.75), Height = H(0.1875) },
            "Field" => new ReportField { Expression = "\"\"", Width = W(1), Height = H(0.1875) },
            "Line" => new ReportLine { Width = W(1), Height = 0, Vertical = size is { } sz && sz.Height > sz.Width },
            "Rectangle" => new ReportShape { Width = W(1), Height = H(0.5) },
            "Rounded rectangle" => new ReportShape { Width = W(1), Height = H(0.5), Curvature = 20 },
            "Picture" => new ReportPicture { Value = "", Width = W(1), Height = H(1) },
            _ => throw new ArgumentException($"Unknown tool {tool}."),
        };
        if (o is ReportLine { Vertical: true } vl) { vl.Height = H(1); vl.Width = 0; }
        o.Left = left;
        o.Top = Math.Min(top, Math.Max(0, Doc.Bands[band].Height - o.Height));
        var r = Session.AddObject(band, o);
        Select([r]);
        return r;
    }

    /// <summary>Adds a field (with a label in the page header when it is a detail band) — from the Fields list.</summary>
    public ObjectRef AddField(string expression, int? band = null)
    {
        var target = band ?? (_lastBand >= 0 ? _lastBand : Doc.Bands.FindIndex(b => b.Kind == BandKind.Detail));
        if (target < 0) target = 0;
        var right = Doc.Bands[target].Objects.Select(o => o.Left + o.Width).DefaultIfEmpty(-0.125).Max() + 0.125;
        ObjectRef r = default;
        Session.Transaction($"Add field {expression}", () =>
        {
            r = Session.AddObject(target, new ReportField { Expression = expression, Left = Snap(right), Top = 0, Width = 1, Height = 0.1875 });
            if (Doc.Bands[target].Kind == BandKind.Detail && Doc.Band(BandKind.PageHeader) is { } ph)
            {
                var name = expression.Contains('.') ? expression[(expression.LastIndexOf('.') + 1)..] : expression;
                ph.Objects.Add(new ReportLabel { Text = char.ToUpperInvariant(name[0]) + name[1..], Left = Snap(right), Top = Math.Max(0, ph.Height - 0.25), Width = 1, Height = 0.1875, Bold = true });
            }
        });
        Select([r]);
        return r;
    }

    public void DeleteSelection()
    {
        if (_selection.Count == 0) return;
        Session.RemoveObjects(_selection.ToList());
        Select([]);
    }

    public void MoveSelection(double dxInches, double dyInches)
    {
        if (_selection.Count > 0) Select(Session.MoveObjects(_selection.ToList(), dxInches, dyInches));
    }

    public void CopySelection() { if (_selection.Count > 0) s_clipboard = Session.Copy(_selection); }
    public void CutSelection() { CopySelection(); DeleteSelection(); }
    public void Paste()
    {
        if (s_clipboard == null) return;
        var band = _selection.Count > 0 ? _selection[0].Band : _lastBand >= 0 ? _lastBand : Doc.Bands.FindIndex(b => b.Kind == BandKind.Detail);
        if (band >= 0) Select(Session.Paste(s_clipboard, band));
    }

    public void Undo() => Session.Undo();
    public void Redo() => Session.Redo();

    /// <summary>Align/size selected objects to the first one: Left, Right, Top, Bottom, SameWidth, SameHeight.</summary>
    public void Align(string how)
    {
        if (_selection.Count < 2) return;
        var refObj = Session.Get(_selection[0])!;
        var refs = _selection.ToList();
        Session.Update("Align " + how, _ =>
        {
            foreach (var r in refs.Skip(1))
            {
                var o = Session.Get(r)!;
                switch (how)
                {
                    case "Left": o.Left = refObj.Left; break;
                    case "Right": o.Left = Math.Round(refObj.Left + refObj.Width - o.Width, 4); break;
                    case "Top": o.Top = refObj.Top; break;
                    case "Bottom": o.Top = Math.Round(refObj.Top + refObj.Height - o.Height, 4); break;
                    case "SameWidth": o.Width = refObj.Width; break;
                    case "SameHeight": o.Height = refObj.Height; break;
                }
            }
        });
    }

    public void ZOrder(bool toFront) { if (_selection.Count > 0) { Session.ZOrder(_selection, toFront); Select([]); } }

    private void OnKey(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var step = ctrl ? GridStep : 0.01;
        Action? act = (e.Key, ctrl) switch
        {
            (Key.Delete, _) => DeleteSelection,
            (Key.Left, _) => () => MoveSelection(-step, 0),
            (Key.Right, _) => () => MoveSelection(step, 0),
            (Key.Up, _) => () => MoveSelection(0, -step),
            (Key.Down, _) => () => MoveSelection(0, step),
            (Key.Z, true) => Undo,
            (Key.Y, true) => Redo,
            (Key.C, true) => CopySelection,
            (Key.X, true) => CutSelection,
            (Key.V, true) => Paste,
            (Key.A, true) => () => Select(Session.AllObjects()),
            (Key.Escape, _) => () => { ArmedTool = null; _toolbox.SelectedIndex = 0; Select([]); },
            _ => null,
        };
        if (act == null) return;
        act();
        e.Handled = true;
    }

    private void EditSelectedText()
    {
        if (_selection.Count != 1) return;
        var name = Session.Get(_selection[0]) switch { ReportLabel => "Text", ReportField => "Expression", ReportPicture => "Value", _ => null };
        if (name != null && _editors.TryGetValue(name, out var box)) box.Focus();
    }

    // ================================================================================
    // Property grid
    // ================================================================================

    private readonly Dictionary<string, Control> _editors = new();

    private static readonly string[] Order =
    [
        "Text", "Expression", "Value", "Source", "Format", "Calculate", "Reset", "ResetGroup", "Name", "Left", "Top", "Width", "Height",
        "FontName", "FontSize", "Bold", "Italic", "Underline", "Strikeout", "ForeColor", "BackColor", "Align", "Vertical", "Curvature", "PenWidth", "PenStyle",
        "Color", "LineColor", "FillColor", "Scale", "Float", "Stretch", "PrintWhen", "PrintRepeated", "RemoveLineIfBlank", "Comment",
    ];

    private static readonly string[] BandProps = ["Height", "ConstantHeight", "OnEntry", "OnExit", "TargetAlias"];
    private static readonly string[] ReportProps =
    [
        "Description", "Paper", "PageWidth", "PageHeight", "Landscape", "LeftMargin", "RightMargin", "TopMargin", "BottomMargin", "Columns", "ColumnWidth",
        "ColumnSpacing", "ColumnOrder", "FontName", "FontSize", "TitleOnNewPage", "SummaryOnNewPage", "SummaryWithPageHeader", "PrivateDataSession",
    ];

    /// <summary>What the property grid edits: the selected object, else the band last clicked, else the report.</summary>
    public object PropertyTarget => _selection.Count > 0 && Session.Get(_selection[0]) is { } o ? o
        : _lastBand >= 0 && _lastBand < Doc.Bands.Count ? Doc.Bands[_lastBand] : Doc;

    private void ShowProperties()
    {
        _props.Children.Clear();
        _editors.Clear();
        var target = PropertyTarget;
        IEnumerable<PropertyInfo> props = target.GetType().GetProperties().Where(p => p.CanWrite && Editable(p.PropertyType));
        props = target switch
        {
            ReportObject => props.OrderBy(p => Array.IndexOf(Order, p.Name) is var i and >= 0 ? i : 999),
            ReportBand => props.Where(p => BandProps.Contains(p.Name)).OrderBy(p => Array.IndexOf(BandProps, p.Name)),
            _ => props.Where(p => ReportProps.Contains(p.Name)).OrderBy(p => Array.IndexOf(ReportProps, p.Name)),
        };
        _propsTitle.Text = target switch
        {
            ReportObject o when _selection.Count > 1 => $"{_selection.Count} objects (editing the first)",
            ReportObject o => ReportDesignSession.Describe(o),
            ReportBand b => b.DisplayName + " band",
            _ => Doc.Kind == ReportKind.Label ? "Label layout" : "Report",
        };
        foreach (var p in props) _props.Children.Add(Row(target, p));
    }

    private static bool Editable(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t == typeof(string) || t == typeof(double) || t == typeof(int) || t == typeof(bool) || t.IsEnum;
    }

    private Control Row(object target, PropertyInfo p)
    {
        var type = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        var value = p.GetValue(target);
        Control editor;
        var isColor = p.Name.EndsWith("Color", StringComparison.Ordinal) && type == typeof(int);
        if (type == typeof(bool))
        {
            var cb = new CheckBox { IsChecked = (bool)value! };
            cb.IsCheckedChanged += (_, _) => SetProperty(p.Name, cb.IsChecked == true ? "true" : "false");
            editor = cb;
        }
        else if (type.IsEnum)
        {
            var combo = new ComboBox { ItemsSource = Enum.GetNames(type), SelectedItem = value?.ToString(), HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string s && s != p.GetValue(PropertyTarget)?.ToString()) SetProperty(p.Name, s); };
            editor = combo;
        }
        else
        {
            var text = value switch
            {
                null => "",
                int c when isColor => $"{c & 0xFF},{(c >> 8) & 0xFF},{(c >> 16) & 0xFF}",
                double d => d.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                _ => value.ToString() ?? "",
            };
            var box = new TextBox { Text = text, MinHeight = 24, Padding = new Thickness(4, 1) };
            var original = text;
            void Commit() { if (box.Text != original) { original = box.Text ?? ""; SetProperty(p.Name, box.Text ?? ""); } }
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            box.LostFocus += (_, _) => Commit();
            editor = box;
        }
        _editors[p.Name] = editor;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), Margin = new Thickness(4, 1) };
        grid.Children.Add(new TextBlock { Text = p.Name, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    /// <summary>Sets a property of the property grid's target from text (colors as r,g,b; empty clears optional values).</summary>
    public void SetProperty(string name, string text)
    {
        var target = PropertyTarget;
        var p = target.GetType().GetProperty(name) ?? throw new ArgumentException($"No property {name}.");
        var type = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        var optional = Nullable.GetUnderlyingType(p.PropertyType) != null || (type == typeof(string) && name is not ("Text" or "Expression" or "Value"));
        object? value;
        try
        {
            if (text.Trim().Length == 0 && optional) value = null;
            else if (type == typeof(string)) value = text;
            else if (type == typeof(bool)) value = text.Trim().ToLowerInvariant() is "true" or ".t." or "yes" or "1";
            else if (type.IsEnum) value = Enum.Parse(type, text.Trim(), ignoreCase: true);
            else if (type == typeof(int) && name.EndsWith("Color", StringComparison.Ordinal))
            {
                var parts = text.Split(',', StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
                value = parts.Length == 3 ? parts[0] + parts[1] * 256 + parts[2] * 65536 : int.Parse(text.Trim());
            }
            else if (type == typeof(int)) value = int.Parse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture);
            else value = Math.Round(double.Parse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture), 4);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            Status?.Invoke($"'{text}' is not a valid value for {name}.");
            ShowProperties();
            return;
        }
        // Apply to every selected object that has the property (multi-selection edits all of them).
        var targets = target is ReportObject && _selection.Count > 1
            ? _selection.Select(Session.Get).Where(o => o?.GetType().GetProperty(name) != null).Cast<object>().ToList()
            : [target];
        var bandIndex = target is ReportBand band ? Doc.Bands.IndexOf(band) : -1;
        var selection = _selection.ToList();
        Session.Update($"Set {name}", _ => { foreach (var t in targets) t.GetType().GetProperty(name)!.SetValue(t, value); });
        if (bandIndex >= 0) _lastBand = bandIndex;
        Select(selection);
    }

    // ================================================================================
    // Data environment, fields, dialogs
    // ================================================================================

    /// <summary>Fields of the tables the report uses: the data environment's cursors, else the current table of the session.</summary>
    public IReadOnlyList<string> AvailableFields()
    {
        var list = new List<string>();
        var rt = _ide.Runtime;
        var aliases = new List<string>();
        if (Doc.DataEnvironment is { } de)
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(de, @"Alias\s*=\s*""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                aliases.Add(m.Groups[1].Value);
        if (aliases.Count == 0 && rt.Session.Current.InUse) aliases.Add(rt.Session.Current.Alias);
        foreach (var alias in aliases)
        {
            var wa = rt.Session.FindAlias(alias);
            if (wa is { InUse: true }) list.AddRange(wa.Table.Fields.Select(f => $"{alias.ToLowerInvariant()}.{f.Name.ToLowerInvariant()}"));
        }
        return list;
    }

    private void RefreshFields()
    {
        _fields.Items.Clear();
        foreach (var f in AvailableFields()) _fields.Items.Add(new ListBoxItem { Content = f, Tag = f, Padding = new Thickness(8, 2), MinHeight = 0, FontSize = 12 });
    }

    /// <summary>Adds a table to the report's data environment (creating it if needed).</summary>
    public void AddTable(string tablePath, string? alias = null)
    {
        alias ??= System.IO.Path.GetFileNameWithoutExtension(tablePath);
        var dir = FilePath != null ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(FilePath))! : _ide.Runtime.Options.Default_;
        var source = System.IO.Path.GetRelativePath(dir, System.IO.Path.GetFullPath(tablePath)).Replace('\\', '/');
        var code = Doc.DataEnvironment ?? "DEFINE CLASS ReportDataEnvironment AS DataEnvironment\nENDDEFINE\n";
        var n = System.Text.RegularExpressions.Regex.Matches(code, @"AS Cursor", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count + 1;
        var member = $"    ADD OBJECT Cursor{n} AS Cursor WITH ;\n        Alias = \"{alias}\", ;\n        CursorSource = \"{source}\"\n";
        code = code.Replace("ENDDEFINE", member + "ENDDEFINE");
        Session.Update("Add table", d => d.DataEnvironment = code);
        // Open it in the session so its fields can be listed and previewed.
        try
        {
            if (_ide.Runtime.Session.FindAlias(alias) == null)
                _ide.Runtime.ExecuteCommand($"USE \"{System.IO.Path.Combine(dir, source)}\" ALIAS {alias} AGAIN IN 0");
        }
        catch (VfpException ex) { Status?.Invoke(ex.Message); }
        RefreshFields();
    }

    /// <summary>Report → Quick Report over a table's fields (replaces the layout, as one undo step).</summary>
    public void QuickReport(string alias, bool columnar = true)
    {
        var wa = _ide.Runtime.Session.FindAlias(alias) ?? throw new ArgumentException($"Alias {alias} is not open.");
        var quick = ReportDesignSession.QuickReport(alias.ToLowerInvariant(), wa.Table.Fields.Select(f => (f.Name, f.Type, f.Width)).ToList(), columnar,
            title: char.ToUpperInvariant(alias[0]) + alias[1..].ToLowerInvariant());
        Session.Update("Quick report", d =>
        {
            d.Bands.Clear();
            d.Bands.AddRange(quick.Bands);
            d.Groups.Clear();
        });
        Select([]);
    }

    // ================================================================================
    // Preview, files
    // ================================================================================

    private void SchedulePreview()
    {
        if (!LivePreview) return;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    /// <summary>Runs the report with the current layout and shows it in the live preview pane.</summary>
    public void RefreshPreview()
    {
        PreviewError = null;
        _previewMessage.Text = "";
        try
        {
            var doc = ReportSerializer.Parse(Session.Text, Doc.Kind); // a copy: the layout must not see preview side effects
            var area = _ide.Runtime.Session.Current;
            var saved = area.InUse && !area.Eof ? area.RecNo : 0;
            PreviewReport = _ide.Reports.Layout(doc, FilePath, new ReportRequest { Path = FilePath ?? "", Scope = new JoePro.Language.Scope("ALL") });
            if (area.InUse && saved > 0) area.Go(saved);
            var preview = new ReportPreview(PreviewReport, _ide.Reports, compact: true);
            preview.Status += m => Status?.Invoke(m);
            _previewHost.Content = preview;
        }
        catch (Exception ex) when (ex is VfpException or CompileException or FormatException or IOException)
        {
            PreviewError = ex.Message;
            _previewMessage.Text = "Preview: " + ex.Message;
        }
    }

    public void Save(string? path = null)
    {
        FilePath = path ?? FilePath ?? throw new InvalidOperationException("No file name.");
        Doc.Save(FilePath);
        Session.MarkSaved();
        Saved?.Invoke();
        Status?.Invoke($"Saved {System.IO.Path.GetFileName(FilePath)}.");
    }

    public void Run()
    {
        if (FilePath == null) { SaveAsRequested?.Invoke(); return; }
        Save();
        RunRequested?.Invoke($"{(Doc.Kind == ReportKind.Label ? "LABEL" : "REPORT")} FORM \"{FilePath}\" PREVIEW");
    }

    private Control BuildToolbar()
    {
        Button B(string text, string tip, Action act)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 2), FontSize = 12 };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) =>
            {
                try { act(); }
                catch (Exception ex) when (ex is ArgumentException or VfpException or InvalidOperationException) { Status?.Invoke(ex.Message); }
            };
            return b;
        }
        var live = new CheckBox { Content = "Live preview", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        live.IsCheckedChanged += (_, _) => { LivePreview = live.IsChecked == true; _center.RowDefinitions[2].Height = LivePreview ? new GridLength(2, GridUnitType.Star) : new GridLength(0); if (LivePreview) RefreshPreview(); };
        return new WrapPanel
        {
            Margin = new Thickness(4),
            Children =
            {
                B("Save", "Save (Ctrl+S)", () => { if (FilePath == null) SaveAsRequested?.Invoke(); else Save(); }),
                B("▶ Preview", "Save and preview the report (Ctrl+E)", Run),
                B("↶", "Undo (Ctrl+Z)", Undo), B("↷", "Redo (Ctrl+Y)", Redo),
                B("⇤", "Align left edges", () => Align("Left")), B("⇥", "Align right edges", () => Align("Right")),
                B("⤒", "Align top edges", () => Align("Top")), B("⤓", "Align bottom edges", () => Align("Bottom")),
                B("↔", "Same width", () => Align("SameWidth")),
                B("Front", "Bring to front", () => ZOrder(true)), B("Back", "Send to back", () => ZOrder(false)),
                B("Groups…", "Data grouping", () => ReportDialogs.Groups(this)),
                B("Variables…", "Report variables", () => ReportDialogs.Variables(this)),
                B("Title/Summary…", "Title and summary bands", () => ReportDialogs.TitleSummary(this)),
                B("Page setup…", "Paper, margins and columns", () => ReportDialogs.PageSetup(this)),
                B("Data environment…", "Edit the report's data environment code", () => ReportDialogs.DataEnvironment(this)),
                B("Quick report…", "Lay out the fields of a table", () => ReportDialogs.QuickReport(this, _ide.Runtime)),
                live,
            },
        };
    }

    public void SetDark(bool dark) { }
}
