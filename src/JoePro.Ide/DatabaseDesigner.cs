using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using JoePro.Core;
using JoePro.Data;

namespace JoePro.Ide;

/// <summary>A database (.jpdb) in the Database Designer.</summary>
public sealed class DatabaseDesignerTab : DocumentTab
{
    public DatabaseDesignerTab(DatabaseDesigner designer)
    {
        Designer = designer;
        Content = designer;
        Title = System.IO.Path.GetFileName(designer.DatabasePath);
    }

    public DatabaseDesigner Designer { get; }
}

/// <summary>
/// The Database Designer: the tables and views of a database as boxes (fields and indexes, keys marked) with its
/// persistent relations drawn between index tags. Drag a box to move it, drag from a parent's primary or candidate
/// index to a child's index to relate them, click a relation to set its referential integrity (the RI Builder).
/// It opens the Table Designer, stored procedures and schema-diff scripts.
/// </summary>
public sealed class DatabaseDesigner : UserControl
{
    public const double BoxWidth = 200, HeaderHeight = 26, RowHeight = 18, GapHeight = 6;
    private const string LayoutKey = "designer_layout";

    private readonly IdeSession _ide;
    private readonly Canvas _canvas = new() { Width = 2400, Height = 1600, Background = Brushes.Transparent };
    private readonly StackPanel _side = new() { Margin = new Thickness(10), Spacing = 6, Width = 250 };
    private readonly Dictionary<string, Point> _layout = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _boxes = new(StringComparer.OrdinalIgnoreCase);
    private string? _dragTable;
    private Point _dragOffset;
    private (string Table, string Tag)? _linkFrom;
    private Line? _linkLine;

    public DatabaseDesigner(string path, IdeSession ide)
    {
        _ide = ide;
        DatabasePath = System.IO.Path.GetFullPath(path);
        Database = ide.Runtime.Session.OpenDatabases.FirstOrDefault(d => string.Equals(d.Path, DatabasePath, StringComparison.OrdinalIgnoreCase))
                   ?? ide.Runtime.Session.OpenDatabase(DatabasePath);
        ide.Runtime.Session.SetDatabase(Database.Name);
        LoadLayout();
        Button B(string text, string tip, Action act)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 2), FontSize = 12 };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => Try(act);
            return b;
        }
        var toolbar = new WrapPanel
        {
            Margin = new Thickness(4),
            Children =
            {
                B("New table", "Create a table in the Table Designer", () => NewTableRequested?.Invoke()),
                B("Add table…", "Move a free table into the database (ADD TABLE)", () => AddTableRequested?.Invoke()),
                B("Modify", "Open the selected table in the Table Designer", () => { if (SelectedTable != null) ModifyTableRequested?.Invoke(SelectedTable); }),
                B("Browse", "Browse the selected table", () => { if (SelectedTable != null) BrowseRequested?.Invoke(SelectedTable); }),
                B("Remove", "Remove the selected table from the database (it stays as a free table)", () => { if (SelectedTable != null) RemoveTable(SelectedTable, delete: false); }),
                B("New relation…", "Relate a parent's primary or candidate index to a child's index", NewRelationDialog),
                B("Stored procedures", "Edit the database's stored procedures", () => ProceduresRequested?.Invoke()),
                B("Schema script", "The script that creates this database's definitions", () => ScriptRequested?.Invoke(Database.Name.ToLowerInvariant() + "-schema.prg", DatabaseSchema.Read(Database).CreateScript())),
                B("Compare…", "The script that upgrades another version of the database to this one", () => CompareRequested?.Invoke()),
                B("Arrange", "Lay the tables out in a grid", Arrange),
            },
        };
        var scroller = new ScrollViewer { Content = _canvas, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var sideBorder = new Border { BorderThickness = new Thickness(1, 0, 0, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), Child = new ScrollViewer { Content = _side } };
        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        DockPanel.SetDock(sideBorder, Dock.Right);
        root.Children.Add(sideBorder);
        root.Children.Add(scroller);
        Content = root;
        _canvas.PointerMoved += OnCanvasPointerMoved;
        _canvas.PointerReleased += OnCanvasPointerReleased;
        _canvas.PointerPressed += (_, e) => { if (e.Source == _canvas) Select(null, null); };
        Refresh();
    }

    public string DatabasePath { get; }
    public Store Database { get; }
    public IReadOnlyList<string> Tables => Database.TableNames();
    public IReadOnlyList<string> Views => Database.ObjectNames(DbObjectStore.ViewKind);
    public IReadOnlyList<RelationDef> Relations => Database.Relations();
    public string? SelectedTable { get; private set; }
    public RelationDef? SelectedRelation { get; private set; }

    public event Action? NewTableRequested;
    public event Action? AddTableRequested;
    public event Action<string>? ModifyTableRequested;
    public event Action<string>? ModifyViewRequested;
    public event Action<string>? BrowseRequested;
    public event Action? ProceduresRequested;
    public event Action? CompareRequested;
    /// <summary>A script to show: its suggested file name and text.</summary>
    public event Action<string, string>? ScriptRequested;
    public event Action<string>? Status;

    private void Try(Action act)
    {
        try { act(); }
        catch (Exception ex) when (ex is VfpException or InvalidOperationException or IOException or ArgumentException) { Status?.Invoke(ex.Message); }
    }

    private void Run(string command)
    {
        if (!_ide.Execute(command)) throw new InvalidOperationException($"{command.Split('\n')[0]} failed.");
    }

    // ---- Layout ------------------------------------------------------------------------------------

    private void LoadLayout()
    {
        _layout.Clear();
        if (Database.GetMeta(LayoutKey) is not { Length: > 0 } json) return;
        try
        {
            foreach (var (name, xy) in JsonSerializer.Deserialize<Dictionary<string, double[]>>(json) ?? [])
                if (xy.Length == 2) _layout[name] = new Point(xy[0], xy[1]);
        }
        catch (JsonException) { }
    }

    private void SaveLayout() =>
        Database.SetMeta(LayoutKey, JsonSerializer.Serialize(_layout.ToDictionary(kv => kv.Key.ToUpperInvariant(), kv => new[] { Math.Round(kv.Value.X), Math.Round(kv.Value.Y) })));

    public Point PositionOf(string name) => _layout.TryGetValue(name, out var p) ? p : default;

    public void MoveTable(string name, Point to)
    {
        _layout[name] = new Point(Math.Max(0, to.X), Math.Max(0, to.Y));
        SaveLayout();
        Refresh();
    }

    /// <summary>Places tables and views in a grid, parents before their children.</summary>
    public void Arrange()
    {
        _layout.Clear();
        PlaceMissing();
        SaveLayout();
        Refresh();
    }

    private double BoxHeight(string name)
    {
        if (!Database.HasTable(name)) return HeaderHeight + RowHeight * 2 + GapHeight;
        var s = Database.OpenTable(name, _ide.Runtime).Schema;
        return HeaderHeight + RowHeight * s.Fields.Count + GapHeight + (s.Tags.Count > 0 ? RowHeight * s.Tags.Count + GapHeight : 0);
    }

    private void PlaceMissing()
    {
        var rels = Relations;
        // Parents first: a table's depth is the length of its longest chain of parents.
        int Depth(string t, int guard = 0) => guard > 20 ? 0 : rels.Where(r => r.ChildTable.Equals(t, StringComparison.OrdinalIgnoreCase) && !r.ParentTable.Equals(t, StringComparison.OrdinalIgnoreCase))
            .Select(r => Depth(r.ParentTable, guard + 1) + 1).DefaultIfEmpty(0).Max();
        var names = Tables.OrderBy(t => Depth(t)).ThenBy(t => t, StringComparer.OrdinalIgnoreCase).Concat(Views).ToList();
        double x = 20, y = 20, rowHeight = 0;
        var placed = _layout.Count > 0;
        if (placed)
        {
            y = _layout.Values.Max(p => p.Y) + 20;
            y += names.Where(_layout.ContainsKey).Select(BoxHeight).DefaultIfEmpty(0).Max();
        }
        foreach (var name in names)
        {
            if (_layout.ContainsKey(name)) continue;
            if (x + BoxWidth > 20 + 4 * (BoxWidth + 50)) { x = 20; y += rowHeight + 40; rowHeight = 0; }
            _layout[name] = new Point(x, y);
            rowHeight = Math.Max(rowHeight, BoxHeight(name));
            x += BoxWidth + 50;
        }
    }

    // ---- Drawing -----------------------------------------------------------------------------------

    public void Refresh()
    {
        var before = _layout.Count;
        PlaceMissing();
        if (_layout.Count != before) SaveLayout();
        _canvas.Children.Clear();
        _boxes.Clear();
        foreach (var t in Tables) AddBox(t, Database.OpenTable(t, _ide.Runtime).Schema);
        foreach (var v in Views) AddViewBox(v, Database.GetView(v));
        foreach (var r in Relations) AddRelationLine(r);
        if (SelectedTable != null && !_boxes.ContainsKey(SelectedTable)) SelectedTable = null;
        if (SelectedRelation != null && !Relations.Any(r => r.SameLink(SelectedRelation))) SelectedRelation = null;
        else if (SelectedRelation != null) SelectedRelation = Relations.First(r => r.SameLink(SelectedRelation));
        ShowSide();
    }

    private static IBrush Card => new SolidColorBrush(Color.FromArgb(24, 128, 128, 128));
    private static readonly IBrush Accent = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xB5));
    private static readonly IBrush ViewAccent = new SolidColorBrush(Color.FromRgb(0x6A, 0x4C, 0x9C));
    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace");

    private Border Box(string name, string title, IBrush accent, IEnumerable<Control> rows)
    {
        var header = new Border
        {
            Background = accent,
            Height = HeaderHeight,
            Padding = new Thickness(8, 0),
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new TextBlock { Text = title, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
        };
        header.PointerPressed += (_, e) =>
        {
            Select(name, null);
            _dragTable = name;
            _dragOffset = e.GetPosition(_boxes[name]);
            e.Pointer.Capture(_canvas);
            e.Handled = true;
        };
        header.DoubleTapped += (_, _) =>
        {
            if (Database.HasTable(name)) ModifyTableRequested?.Invoke(name);
            else ModifyViewRequested?.Invoke(name);
        };
        var body = new StackPanel { Children = { header } };
        foreach (var r in rows) body.Children.Add(r);
        var selected = string.Equals(SelectedTable, name, StringComparison.OrdinalIgnoreCase);
        var box = new Border
        {
            Width = BoxWidth,
            Background = Card,
            BorderBrush = selected ? Brushes.Orange : new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)),
            BorderThickness = new Thickness(selected ? 2 : 1),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            Child = body,
            Tag = name,
        };
        var p = PositionOf(name);
        Canvas.SetLeft(box, p.X);
        Canvas.SetTop(box, p.Y);
        _canvas.Children.Add(box);
        _boxes[name] = box;
        return box;
    }

    private void AddBox(string name, TableSchema s)
    {
        var rows = new List<Control>();
        foreach (var f in s.Fields)
            rows.Add(new TextBlock { Text = $"{f.Name.ToLowerInvariant(),-14}{TableDesign.TypeText(f)}{(f.Nullable ? " null" : "")}", FontFamily = Mono, FontSize = 11, Height = RowHeight, Padding = new Thickness(8, 1), TextTrimming = TextTrimming.CharacterEllipsis });
        rows.Add(new Border { Height = GapHeight });
        foreach (var t in s.Tags)
        {
            var mark = t.Kind switch { TagKind.Primary => "🔑", TagKind.Candidate => "◆", TagKind.Unique => "◇", _ => "·" };
            var row = new TextBlock
            {
                Text = $"{mark} {t.Name.ToLowerInvariant()}  {t.Expression}", FontSize = 11, Height = RowHeight, Padding = new Thickness(8, 1),
                TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.85, Tag = (name, t.Name), Cursor = new Cursor(StandardCursorType.Cross),
            };
            ToolTip.SetTip(row, "Drag to a child table's index to relate them");
            row.PointerPressed += (_, e) =>
            {
                _linkFrom = (name, t.Name);
                var start = TagAnchor(name, t.Name, right: true);
                _linkLine = new Line { StartPoint = start, EndPoint = start, Stroke = Brushes.Orange, StrokeThickness = 2, StrokeDashArray = [4, 2], IsHitTestVisible = false };
                _canvas.Children.Add(_linkLine);
                e.Pointer.Capture(_canvas);
                e.Handled = true;
            };
            rows.Add(row);
        }
        if (s.Tags.Count > 0) rows.Add(new Border { Height = GapHeight });
        Box(name, name.ToLowerInvariant(), Accent, rows);
    }

    private void AddViewBox(string name, ViewDefinition? v)
    {
        var sql = (v?.Sql ?? "").Replace('\n', ' ').Trim();
        Box(name, name.ToLowerInvariant() + (v?.Remote == true ? "  (remote view)" : "  (view)"), ViewAccent,
        [
            new TextBlock { Text = sql, FontSize = 11, Height = RowHeight * 2, Padding = new Thickness(8, 1), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.8 },
            new Border { Height = GapHeight },
        ]);
    }

    /// <summary>Where a relation line meets a table's index row (the box's left or right edge).</summary>
    public Point TagAnchor(string table, string tag, bool right)
    {
        var p = PositionOf(table);
        if (!Database.HasTable(table)) return new Point(p.X + (right ? BoxWidth : 0), p.Y + HeaderHeight / 2);
        var s = Database.OpenTable(table, _ide.Runtime).Schema;
        var i = s.Tags.FindIndex(t => t.Name.Equals(tag, StringComparison.OrdinalIgnoreCase));
        var y = p.Y + HeaderHeight + RowHeight * s.Fields.Count + GapHeight + RowHeight * Math.Max(0, i) + RowHeight / 2;
        return new Point(p.X + (right ? BoxWidth : 0), y);
    }

    private void AddRelationLine(RelationDef r)
    {
        var parentRight = PositionOf(r.ParentTable).X <= PositionOf(r.ChildTable).X;
        var from = TagAnchor(r.ParentTable, r.ParentTag, right: parentRight);
        var to = TagAnchor(r.ChildTable, r.ChildTag, right: !parentRight);
        var stub = parentRight ? 14 : -14;
        var points = new List<Point> { from, new(from.X + stub, from.Y), new(to.X - stub, to.Y), to };
        var selected = SelectedRelation != null && SelectedRelation.SameLink(r);
        var line = new Polyline { Points = points, Stroke = selected ? Brushes.Orange : Accent, StrokeThickness = selected ? 3 : 1.5, IsHitTestVisible = false };
        // A wide transparent line under it makes the relation easy to click.
        var hit = new Polyline { Points = points, Stroke = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), StrokeThickness = 10, Cursor = new Cursor(StandardCursorType.Hand), Tag = r };
        hit.PointerPressed += (_, e) => { Select(null, r); e.Handled = true; };
        ToolTip.SetTip(hit, $"{r.ParentTable.ToLowerInvariant()}.{r.ParentTag.ToLowerInvariant()} → {r.ChildTable.ToLowerInvariant()}.{r.ChildTag.ToLowerInvariant()}  (update {r.RiUpdate.ToLowerInvariant()}, delete {r.RiDelete.ToLowerInvariant()}, insert {r.RiInsert.ToLowerInvariant()})");
        _canvas.Children.Insert(0, hit);
        _canvas.Children.Insert(1, line);
        // The "many" end.
        _canvas.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = selected ? Brushes.Orange : Accent, IsHitTestVisible = false, [Canvas.LeftProperty] = to.X - 3.5, [Canvas.TopProperty] = to.Y - 3.5 });
        if (r.RiCode != "III")
            _canvas.Children.Add(new TextBlock { Text = r.RiCode, FontSize = 10, Opacity = 0.8, IsHitTestVisible = false, [Canvas.LeftProperty] = (from.X + to.X) / 2 - 10, [Canvas.TopProperty] = (from.Y + to.Y) / 2 - 14 });
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        if (_dragTable != null && _boxes.TryGetValue(_dragTable, out var box))
        {
            Canvas.SetLeft(box, Math.Max(0, p.X - _dragOffset.X));
            Canvas.SetTop(box, Math.Max(0, p.Y - _dragOffset.Y));
        }
        else if (_linkLine != null) _linkLine.EndPoint = p;
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        e.Pointer.Capture(null);
        if (_dragTable != null && _boxes.TryGetValue(_dragTable, out var box))
        {
            var name = _dragTable;
            _dragTable = null;
            MoveTable(name, new Point(Canvas.GetLeft(box), Canvas.GetTop(box)));
            return;
        }
        if (_linkFrom is { } from)
        {
            _linkFrom = null;
            if (_linkLine != null) _canvas.Children.Remove(_linkLine);
            _linkLine = null;
            if (TagAt(p) is { } to && !(to.Table.Equals(from.Table, StringComparison.OrdinalIgnoreCase) && to.Tag.Equals(from.Tag, StringComparison.OrdinalIgnoreCase)))
                Try(() => AddRelation(from.Table, from.Tag, to.Table, to.Tag));
        }
    }

    /// <summary>The index row under a canvas point.</summary>
    public (string Table, string Tag)? TagAt(Point p)
    {
        foreach (var name in Tables)
        {
            var pos = PositionOf(name);
            if (p.X < pos.X || p.X > pos.X + BoxWidth) continue;
            foreach (var t in Database.OpenTable(name, _ide.Runtime).Schema.Tags)
            {
                var a = TagAnchor(name, t.Name, right: false);
                if (Math.Abs(p.Y - a.Y) <= RowHeight / 2) return (name, t.Name);
            }
        }
        return null;
    }

    // ---- Selection and the side panel -------------------------------------------------------------------

    public void Select(string? table, RelationDef? relation)
    {
        SelectedTable = table != null ? Tables.Concat(Views).FirstOrDefault(t => t.Equals(table, StringComparison.OrdinalIgnoreCase)) : null;
        SelectedRelation = relation;
        Refresh();
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 14 };
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 12 };

    private void ShowSide()
    {
        _side.Children.Clear();
        if (SelectedRelation is { } r)
        {
            _side.Children.Add(Heading("Relation"));
            _side.Children.Add(Note($"Parent: {r.ParentTable.ToLowerInvariant()}, index {r.ParentTag.ToLowerInvariant()}\nChild: {r.ChildTable.ToLowerInvariant()}, index {r.ChildTag.ToLowerInvariant()}"));
            _side.Children.Add(Heading("Referential integrity"));
            ComboBox Rule(string label, string value, string help, Func<string, RelationDef> with)
            {
                var combo = new ComboBox { ItemsSource = new[] { "Ignore", "Cascade", "Restrict" }, SelectedIndex = Array.IndexOf(RelationDef.Rules, value), HorizontalAlignment = HorizontalAlignment.Stretch };
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedIndex < 0 || RelationDef.Rules[combo.SelectedIndex] == value) return;
                    Try(() => SetIntegrity(with(RelationDef.Rules[combo.SelectedIndex])));
                };
                _side.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 4, 0, 0) });
                _side.Children.Add(combo);
                _side.Children.Add(Note(help));
                return combo;
            }
            Rule("Update", r.RiUpdate, "Cascade changes the child keys when a parent key changes; Restrict refuses the change while children exist.", v => r with { RiUpdate = v });
            Rule("Delete", r.RiDelete, "Cascade deletes the children with their parent; Restrict refuses to delete a parent that has children.", v => r with { RiDelete = v });
            Rule("Insert", r.RiInsert, "Restrict refuses a child whose key matches no parent.", v => r with { RiInsert = v });
            var remove = new Button { Content = "Remove relation", Margin = new Thickness(0, 8, 0, 0) };
            remove.Click += (_, _) => Try(() => RemoveRelation(r));
            _side.Children.Add(remove);
            return;
        }
        if (SelectedTable is { } name)
        {
            if (!Database.HasTable(name))
            {
                _side.Children.Add(Heading(name.ToLowerInvariant() + " (view)"));
                _side.Children.Add(new SelectableTextBlock { Text = Database.GetView(name)?.Sql ?? "", TextWrapping = TextWrapping.Wrap, FontFamily = Mono, FontSize = 11 });
                return;
            }
            var s = Database.OpenTable(name, _ide.Runtime).Schema;
            _side.Children.Add(Heading(name.ToLowerInvariant()));
            if (s.Comment is { Length: > 0 } c) _side.Children.Add(Note(c));
            _side.Children.Add(Note($"{s.Fields.Count} field(s), {s.Tags.Count} index(es), {Database.OpenTable(name, _ide.Runtime).RecordCount} record(s)."));
            if (s.RuleExpr is { Length: > 0 } rule) _side.Children.Add(Note("Rule: " + rule));
            foreach (var (kind, trig) in new[] { ("Insert", s.InsertTrigger), ("Update", s.UpdateTrigger), ("Delete", s.DeleteTrigger) })
                if (trig is { Length: > 0 }) _side.Children.Add(Note($"{kind} trigger: {trig}"));
            var parents = Relations.Where(x => x.ChildTable.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(x => x.ParentTable.ToLowerInvariant()).ToList();
            var children = Relations.Where(x => x.ParentTable.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(x => x.ChildTable.ToLowerInvariant()).ToList();
            if (parents.Count > 0) _side.Children.Add(Note("Parents: " + string.Join(", ", parents)));
            if (children.Count > 0) _side.Children.Add(Note("Children: " + string.Join(", ", children)));
            return;
        }
        _side.Children.Add(Heading(Database.Name.ToLowerInvariant()));
        var procLines = Database.StoredProcedures.Split('\n').Count(l => l.Trim().Length > 0);
        _side.Children.Add(Note($"{Tables.Count} table(s), {Views.Count} view(s), {Relations.Count} relation(s), {procLines} line(s) of stored procedures."));
        _side.Children.Add(Note("Double-click a table to change its structure. Drag from a parent's primary or candidate index (🔑 ◆) to a child's index to relate them, then click the line to set referential integrity."));
    }

    // ---- Changes -----------------------------------------------------------------------------------

    /// <summary>Relates a parent's primary or candidate index to a child's index (ALTER TABLE child ADD FOREIGN KEY).</summary>
    public void AddRelation(string parentTable, string parentTag, string childTable, string childTag)
    {
        var parent = Database.OpenTable(parentTable, _ide.Runtime).Schema.FindTag(parentTag) ?? throw new InvalidOperationException($"{parentTable} has no index {parentTag}.");
        if (parent.Kind is not (TagKind.Primary or TagKind.Candidate))
        {
            // Dragged from the child's side: relate the other way when that makes a valid relation.
            if (Database.OpenTable(childTable, _ide.Runtime).Schema.FindTag(childTag) is { Kind: TagKind.Primary or TagKind.Candidate })
            {
                AddRelation(childTable, childTag, parentTable, parentTag);
                return;
            }
            throw new InvalidOperationException($"A relation starts at the parent's primary or candidate index; {parentTable.ToLowerInvariant()}.{parentTag.ToLowerInvariant()} is neither.");
        }
        if (Relations.Any(r => r.SameLink(new RelationDef(parentTable, parentTag, childTable, childTag))))
            throw new InvalidOperationException("These indexes are already related.");
        Run($"ALTER TABLE {childTable} ADD FOREIGN KEY TAG {childTag} REFERENCES {parentTable} TAG {parentTag}");
        SelectedRelation = Relations.FirstOrDefault(r => r.SameLink(new RelationDef(parentTable, parentTag, childTable, childTag)));
        SelectedTable = null;
        Refresh();
        Status?.Invoke($"Related {parentTable.ToLowerInvariant()} to {childTable.ToLowerInvariant()}.");
    }

    public void SetIntegrity(RelationDef r)
    {
        Database.UpdateRelation(r);
        SelectedRelation = r.Normalize();
        Refresh();
        Status?.Invoke($"Referential integrity of {r.ParentTable.ToLowerInvariant()} → {r.ChildTable.ToLowerInvariant()}: update {r.RiUpdate.ToLowerInvariant()}, delete {r.RiDelete.ToLowerInvariant()}, insert {r.RiInsert.ToLowerInvariant()}.");
    }

    public void RemoveRelation(RelationDef r)
    {
        Database.RemoveRelation(r);
        SelectedRelation = null;
        Refresh();
    }

    /// <summary>REMOVE TABLE (DELETE also erases it).</summary>
    public void RemoveTable(string name, bool delete)
    {
        foreach (var w in _ide.Runtime.Session.OpenWorkAreas().Where(w => w.Table.Store == Database && w.Table.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList()) w.Close();
        Run($"REMOVE TABLE {name}{(delete ? " DELETE" : "")}");
        _layout.Remove(name);
        SaveLayout();
        SelectedTable = null;
        Refresh();
    }

    public void AddTable(string path)
    {
        Run($"ADD TABLE \"{path}\"");
        Refresh();
    }

    /// <summary>The script that upgrades the database at <paramref name="otherPath"/> (say, the deployed one) to this one.</summary>
    public string UpgradeScriptFor(string otherPath)
    {
        using var other = Store.Open(otherPath);
        return DatabaseSchema.DiffScript(DatabaseSchema.Read(other), DatabaseSchema.Read(Database));
    }

    private void NewRelationDialog()
    {
        var tables = Tables.ToList();
        if (tables.Count == 0) throw new InvalidOperationException("The database has no tables.");
        var parent = new ComboBox { ItemsSource = tables, SelectedIndex = 0, MinWidth = 160 };
        var parentTag = new ComboBox { MinWidth = 160 };
        var child = new ComboBox { ItemsSource = tables, SelectedIndex = Math.Min(1, tables.Count - 1), MinWidth = 160 };
        var childTag = new ComboBox { MinWidth = 160 };
        void Tags(ComboBox table, ComboBox tag, bool keys)
        {
            var name = table.SelectedItem as string;
            tag.ItemsSource = name == null ? [] : Database.OpenTable(name, _ide.Runtime).Schema.Tags.Where(t => !keys || t.Kind is TagKind.Primary or TagKind.Candidate).Select(t => t.Name).ToList();
            tag.SelectedIndex = 0;
        }
        parent.SelectionChanged += (_, _) => Tags(parent, parentTag, keys: true);
        child.SelectionChanged += (_, _) => Tags(child, childTag, keys: false);
        Tags(parent, parentTag, true);
        Tags(child, childTag, false);
        var ok = new Button { Content = "Relate", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = new Thickness(12) };
        void Cell(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); c.Margin = new Thickness(4); grid.Children.Add(c); }
        Cell(new TextBlock { Text = "Parent", VerticalAlignment = VerticalAlignment.Center }, 0, 0); Cell(parent, 0, 1); Cell(parentTag, 0, 2);
        Cell(new TextBlock { Text = "Child", VerticalAlignment = VerticalAlignment.Center }, 1, 0); Cell(child, 1, 1); Cell(childTag, 1, 2);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } };
        Cell(buttons, 2, 2);
        var window = new Window { Title = "New relation", Content = grid, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        ok.Click += (_, _) =>
        {
            if (parent.SelectedItem is string p && parentTag.SelectedItem is string pt && child.SelectedItem is string c && childTag.SelectedItem is string ct)
                Try(() => AddRelation(p, pt, c, ct));
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        if (TopLevel.GetTopLevel(this) is Window owner) window.ShowDialog(owner);
        else window.Show();
    }
}

/// <summary>A database's stored procedures in the code editor (MODIFY PROCEDURE); saving writes them to the database.</summary>
public sealed class StoredProceduresTab : DocumentTab
{
    private bool _dirty;

    public StoredProceduresTab(Store database, bool dark)
    {
        Database = database;
        Editor = new TextEditor
        {
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"),
            FontSize = 13,
            ShowLineNumbers = true,
            SyntaxHighlighting = FoxProHighlighting.Get(dark),
            Padding = new Thickness(4),
            Text = database.StoredProcedures,
        };
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 3;
        Editor.TextChanged += (_, _) => { _dirty = true; UpdateTitle(); };
        Content = Editor;
        UpdateTitle();
    }

    public Store Database { get; }
    public TextEditor Editor { get; }
    public bool IsDirty => _dirty;

    private void UpdateTitle() => Title = $"{Database.Name.ToLowerInvariant()} · stored procedures{(_dirty ? " •" : "")}";

    /// <summary>Checks that the code compiles, then saves it to the database.</summary>
    public void Save()
    {
        JoePro.Language.Parser.ParseProgram(Editor.Text, Database.Name, Database.Path);
        Database.StoredProcedures = Editor.Text;
        _dirty = false;
        UpdateTitle();
    }
}
