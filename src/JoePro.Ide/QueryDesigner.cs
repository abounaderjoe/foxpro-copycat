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
using JoePro.Documents.Queries;

namespace JoePro.Ide;

/// <summary>A query (.jpquery) or a view of a database in the Query/View Designer.</summary>
public sealed class QueryDesignerTab : DocumentTab
{
    public QueryDesignerTab(QueryDesigner designer)
    {
        Designer = designer;
        Content = designer;
        designer.Changed += UpdateTitle;
        designer.Saved += UpdateTitle;
        UpdateTitle();
    }

    public QueryDesigner Designer { get; }

    private void UpdateTitle() => Title = (Designer.IsView
        ? (Designer.ViewName ?? "view1").ToLowerInvariant() + " · view"
        : Designer.FilePath != null ? System.IO.Path.GetFileName(Designer.FilePath) : "query1.jpquery") + (Designer.IsDirty ? " •" : "");
}

/// <summary>
/// The Query and View Designer: tables and joins as a diagram (drag a field onto another table's field to join
/// them, double-click a field to output it), tabs for fields, joins, filter, order, grouping and options, the SQL
/// pane — editable, with two-way sync (a SELECT the designer cannot show is edited as SQL only) — and a results
/// preview. As the View Designer it also sets the update criteria and saves the view in its database.
/// </summary>
public sealed class QueryDesigner : UserControl
{
    private const double BoxWidth = 170, HeaderHeight = 24, RowHeight = 17;
    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace");
    private static readonly IBrush Accent = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xB5));

    private readonly IdeSession _ide;
    private readonly Store? _viewDb;
    private readonly Canvas _canvas = new() { Width = 1600, Height = 400, Background = Brushes.Transparent };
    private readonly TextEditor _sql;
    private readonly TextBlock _banner = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkOrange, Margin = new Thickness(6, 2) };
    private readonly TabControl _tabs = new();
    private readonly StackPanel _fieldsTab = new() { Spacing = 4, Margin = new Thickness(8) };
    private readonly StackPanel _joinTab = new() { Spacing = 4, Margin = new Thickness(8) };
    private readonly StackPanel _filterTab = new() { Spacing = 4, Margin = new Thickness(8) };
    private readonly StackPanel _orderTab = new() { Spacing = 4, Margin = new Thickness(8) };
    private readonly StackPanel _groupTab = new() { Spacing = 4, Margin = new Thickness(8) };
    private readonly StackPanel _miscTab = new() { Spacing = 4, Margin = new Thickness(8) };
    private readonly StackPanel _updateTab = new() { Spacing = 4, Margin = new Thickness(8) };
    private readonly ListBox _results = new() { FontFamily = Mono, FontSize = 12 };
    private readonly TextBox _viewName = new() { Width = 200 };
    private bool _syncing, _dirty;
    private string? _dragFrom;
    private (string Table, string Field)? _linkFrom;
    private Line? _linkLine;

    public QueryDesigner(QueryDocument doc, string? path, IdeSession ide, bool dark, Store? viewDatabase = null, string? viewName = null)
    {
        _ide = ide;
        Document = doc;
        FilePath = path;
        _viewDb = viewDatabase;
        ViewName = viewName;
        if (IsView) LoadViewState();
        _sql = new TextEditor
        {
            FontFamily = Mono, FontSize = 13, ShowLineNumbers = true, SyntaxHighlighting = FoxProHighlighting.Get(dark), Padding = new Thickness(4),
        };
        _sql.TextChanged += (_, _) => { if (!_syncing) _dirty = true; };
        Button B(string text, string tip, Action act)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 2), FontSize = 12 };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => Try(act);
            return b;
        }
        var toolbar = new WrapPanel { Margin = new Thickness(4) };
        if (IsView)
        {
            _viewName.Text = viewName?.ToLowerInvariant() ?? "";
            _viewName.Watermark = "view name";
            _viewName.TextChanged += (_, _) => { if (!_syncing) { ViewName = (_viewName.Text ?? "").Trim(); Touch(); } };
            toolbar.Children.Add(new TextBlock { Text = "View", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) });
            toolbar.Children.Add(_viewName);
        }
        foreach (var b in new[]
        {
            B("Add table…", "Add a table or view to the query", AddTableDialog),
            B("Remove table", "Remove the last selected table", () => { if (SelectedTable != null) RemoveTable(SelectedTable); }),
            B("Apply SQL", "Read the SQL pane back into the designer", () => ApplySql(_sql.Text)),
            B("▶ Run", "Run the query and show the results", () => RunPreview()),
            B("Save", "Save the query", () => Save()),
        }) toolbar.Children.Add(b);
        _tabs.Items.Add(new TabItem { Header = "Fields", Content = new ScrollViewer { Content = _fieldsTab } });
        _tabs.Items.Add(new TabItem { Header = "Join", Content = new ScrollViewer { Content = _joinTab } });
        _tabs.Items.Add(new TabItem { Header = "Filter", Content = new ScrollViewer { Content = _filterTab } });
        _tabs.Items.Add(new TabItem { Header = "Order By", Content = new ScrollViewer { Content = _orderTab } });
        _tabs.Items.Add(new TabItem { Header = "Group By", Content = new ScrollViewer { Content = _groupTab } });
        _tabs.Items.Add(new TabItem { Header = "Misc", Content = new ScrollViewer { Content = _miscTab } });
        if (IsView) _tabs.Items.Add(new TabItem { Header = "Update Criteria", Content = new ScrollViewer { Content = _updateTab } });
        _tabs.Items.Add(new TabItem { Header = "SQL", Content = _sql });
        _tabs.Items.Add(new TabItem { Header = "Results", Content = _results });
        var diagram = new Border
        {
            Height = 230, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
            Child = new ScrollViewer { Content = _canvas, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
        };
        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        DockPanel.SetDock(_banner, Dock.Top);
        root.Children.Add(_banner);
        DockPanel.SetDock(diagram, Dock.Top);
        root.Children.Add(diagram);
        root.Children.Add(_tabs);
        Content = root;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        Rebuild();
        _dirty = false;
    }

    public QueryDocument Document { get; private set; }
    public string? FilePath { get; private set; }
    public bool IsView => _viewDb != null;
    public string? ViewName
    {
        get => _name;
        set
        {
            _name = value;
            if (!_syncing && (_viewName.Text ?? "") != (value ?? "").ToLowerInvariant())
            {
                _syncing = true;
                _viewName.Text = value?.ToLowerInvariant() ?? "";
                _syncing = false;
            }
        }
    }

    private string? _name;
    public bool IsDirty => _dirty;
    public string SqlText => _sql.Text;
    public string? SelectedTable { get; private set; }
    public IReadOnlyList<string> ResultLines => _results.ItemsSource as IReadOnlyList<string> ?? [];
    public string Banner => _banner.Text ?? "";

    // View update criteria (DBSETPROP view and field properties).
    public bool SendUpdates { get; set; }
    public int WhereType { get; set; } = 3;
    public int UpdateType { get; set; } = 1;
    public HashSet<string> KeyFields { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UpdatableFields { get; } = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;
    public event Action? Saved;
    public event Action? SaveAsRequested;
    public event Action<string>? Status;

    private void Try(Action act)
    {
        try { act(); }
        catch (Exception ex) when (ex is VfpException or InvalidOperationException or IOException or ArgumentException or FormatException) { Status?.Invoke(ex.Message); }
    }

    private void Touch()
    {
        _dirty = true;
        Rebuild();
        Changed?.Invoke();
    }

    // ---- Tables and their fields ----------------------------------------------------------------------

    /// <summary>The fields of a query table, from the database or free table it names (none for views and unknown tables).</summary>
    public IReadOnlyList<string> FieldsOf(QueryTable t)
    {
        try
        {
            var session = _ide.Runtime.Session;
            var name = t.Name.Trim('"', '\'');
            var bang = name.IndexOf('!');
            Store? db = bang > 0 ? session.OpenDatabases.FirstOrDefault(d => d.Name.Equals(name[..bang], StringComparison.OrdinalIgnoreCase)) : _viewDb ?? session.CurrentDatabase;
            var bare = bang > 0 ? name[(bang + 1)..] : name;
            if (db != null && db.HasTable(bare)) return db.OpenTable(bare, _ide.Runtime).Fields.Select(f => f.Name.ToLowerInvariant()).ToList();
            var path = session.ResolvePath(name, Store.FreeTableExtension);
            if (File.Exists(path))
            {
                var store = session.StoreOf(path);
                return store.OpenTable(store.TableNames()[0], _ide.Runtime).Fields.Select(f => f.Name.ToLowerInvariant()).ToList();
            }
        }
        catch (Exception ex) when (ex is VfpException or IOException or InvalidOperationException) { }
        return [];
    }

    public QueryTable AddTable(string name, string? alias = null)
    {
        if (Document.IsSqlOnly) throw new InvalidOperationException("The query is SQL only; edit the SQL pane.");
        var t = new QueryTable { Name = name, Alias = alias, X = 20 + Document.Tables.Count * (BoxWidth + 50), Y = 20 };
        if (Document.Tables.Any(x => x.Ref.Equals(t.Ref, StringComparison.OrdinalIgnoreCase)))
        {
            for (int i = 2; ; i++)
                if (!Document.Tables.Any(x => x.Ref.Equals(QueryTable.Bare(name) + i, StringComparison.OrdinalIgnoreCase))) { t.Alias = QueryTable.Bare(name) + i; break; }
        }
        // A new table joins the one before it when a field of each has the same name (id / custid = id is left to the user).
        if (Document.Tables.Count > 0)
        {
            var mine = FieldsOf(t);
            foreach (var other in Document.Tables)
            {
                var common = FieldsOf(other).FirstOrDefault(f => mine.Contains(f) && f.EndsWith("id", StringComparison.OrdinalIgnoreCase));
                if (common == null) continue;
                t.Join = JoinKind.Inner;
                t.On = $"{t.Ref}.{common} = {other.Ref}.{common}";
                break;
            }
            if (t.Join == null) { t.Join = JoinKind.Inner; t.On = ".T."; }
        }
        Document.Tables.Add(t);
        SelectedTable = t.Ref;
        Touch();
        return t;
    }

    public void RemoveTable(string reference)
    {
        var t = Document.FindTable(reference) ?? throw new InvalidOperationException($"{reference} is not in the query.");
        var i = Document.Tables.IndexOf(t);
        Document.Tables.RemoveAt(i);
        if (i == 0 && Document.Tables.Count > 0) { Document.Tables[0].Join = null; Document.Tables[0].On = null; }
        Document.Fields.RemoveAll(f => f.Expression.StartsWith(t.Ref + ".", StringComparison.OrdinalIgnoreCase));
        SelectedTable = null;
        Touch();
    }

    /// <summary>Joins two tables on a field of each (dragging a field onto another table's field).</summary>
    public void Join(string leftTable, string leftField, string rightTable, string rightField, JoinKind kind = JoinKind.Inner)
    {
        var a = Document.FindTable(leftTable) ?? throw new InvalidOperationException($"{leftTable} is not in the query.");
        var b = Document.FindTable(rightTable) ?? throw new InvalidOperationException($"{rightTable} is not in the query.");
        // The join goes on the later of the two tables.
        var (first, second, f1, f2) = Document.Tables.IndexOf(a) < Document.Tables.IndexOf(b) ? (a, b, leftField, rightField) : (b, a, rightField, leftField);
        second.Join = kind;
        second.On = $"{second.Ref}.{f2} = {first.Ref}.{f1}";
        Touch();
    }

    public void AddField(string expression, string? alias = null)
    {
        if (Document.Fields.Any(f => f.Expression.Equals(expression, StringComparison.OrdinalIgnoreCase) && f.Alias == alias)) return;
        Document.Fields.Add(new QueryField(expression, string.IsNullOrWhiteSpace(alias) ? null : alias.Trim()));
        Touch();
    }

    public void AddFilter(QueryCondition c) { Document.Filters.Add(c); Touch(); }
    public void AddOrder(string expression, bool descending = false) { Document.OrderBy.Add(new QueryOrder(expression, descending)); Touch(); }

    /// <summary>Reads SQL back into the designer. Returns false when the SELECT can only be kept as SQL.</summary>
    public bool ApplySql(string sql)
    {
        var positions = Document.Tables.ToDictionary(t => t.Ref.ToUpperInvariant(), t => (t.X, t.Y));
        var doc = QueryDocument.FromSql(sql);
        foreach (var (t, i) in doc.Tables.Select((t, i) => (t, i)))
        {
            if (positions.TryGetValue(t.Ref.ToUpperInvariant(), out var p)) (t.X, t.Y) = p;
            else (t.X, t.Y) = (20 + i * (BoxWidth + 50), 20);
        }
        if (IsView && !doc.IsSqlOnly && doc.Destination != QueryDestination.Browse)
        {
            Status?.Invoke("A view has no INTO clause; it was dropped.");
            doc.Destination = QueryDestination.Browse;
            doc.IntoName = null;
        }
        Document = doc;
        Touch();
        return !doc.IsSqlOnly;
    }

    /// <summary>Leaves SQL-only mode, starting a visual query from the tables the SQL names (when it can).</summary>
    public void BackToVisual()
    {
        Document = new QueryDocument();
        Touch();
    }

    // ---- Drawing ------------------------------------------------------------------------------------

    private void Rebuild()
    {
        _syncing = true;
        var sql = IsView ? Document.ToSql(withDestination: false) : Document.ToSql();
        if (_sql.Text != sql) _sql.Text = sql;
        _syncing = false;
        _banner.Text = Document.IsSqlOnly
            ? $"This query is kept as SQL ({Document.SqlOnlyReason}): edit it in the SQL pane. The other tabs show a query the designer can represent."
            : "";
        _banner.IsVisible = Document.IsSqlOnly;
        DrawDiagram();
        FillFieldsTab();
        FillJoinTab();
        FillConditions(_filterTab, Document.Filters, "Records must meet these conditions (WHERE).");
        FillOrderTab();
        FillGroupTab();
        FillMiscTab();
        if (IsView) FillUpdateTab();
        foreach (var item in _tabs.Items.OfType<TabItem>())
            if (item.Header as string is not ("SQL" or "Results")) item.IsEnabled = !Document.IsSqlOnly;
    }

    private void DrawDiagram()
    {
        _canvas.Children.Clear();
        foreach (var t in Document.Tables)
        {
            var body = new StackPanel();
            var header = new Border
            {
                Background = Accent, Height = HeaderHeight, Padding = new Thickness(6, 0), Cursor = new Cursor(StandardCursorType.SizeAll),
                Child = new TextBlock { Text = t.Alias != null ? $"{QueryTable.Bare(t.Name)} ({t.Alias})" : QueryTable.Bare(t.Name), Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            };
            var reference = t.Ref;
            header.PointerPressed += (_, e) => { SelectedTable = reference; _dragFrom = reference; e.Pointer.Capture(_canvas); e.Handled = true; };
            body.Children.Add(header);
            foreach (var f in FieldsOf(t))
            {
                var field = f;
                var row = new TextBlock { Text = f, FontFamily = Mono, FontSize = 11, Height = RowHeight, Padding = new Thickness(6, 1), Cursor = new Cursor(StandardCursorType.Hand) };
                ToolTip.SetTip(row, "Double-click to output the field; drag onto another table's field to join");
                row.DoubleTapped += (_, _) => AddField($"{reference}.{field}");
                row.PointerPressed += (_, e) =>
                {
                    _linkFrom = (reference, field);
                    var p = e.GetPosition(_canvas);
                    _linkLine = new Line { StartPoint = p, EndPoint = p, Stroke = Brushes.Orange, StrokeThickness = 2, IsHitTestVisible = false };
                    _canvas.Children.Add(_linkLine);
                    e.Pointer.Capture(_canvas);
                };
                body.Children.Add(row);
            }
            var box = new Border
            {
                Width = BoxWidth, Child = body, BorderThickness = new Thickness(reference == SelectedTable ? 2 : 1), CornerRadius = new CornerRadius(3),
                BorderBrush = reference == SelectedTable ? Brushes.Orange : new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)),
                Background = new SolidColorBrush(Color.FromArgb(24, 128, 128, 128)), Tag = reference,
            };
            Canvas.SetLeft(box, t.X);
            Canvas.SetTop(box, t.Y);
            _canvas.Children.Add(box);
        }
        // Join lines from table to table, labelled with the join type.
        for (int i = 1; i < Document.Tables.Count; i++)
        {
            var t = Document.Tables[i];
            if (t.Join == null || string.IsNullOrWhiteSpace(t.On)) continue;
            var other = Document.Tables.Take(i).FirstOrDefault(o => t.On!.Contains(o.Ref + ".", StringComparison.OrdinalIgnoreCase)) ?? Document.Tables[i - 1];
            var from = new Point(other.X + BoxWidth, other.Y + HeaderHeight / 2);
            var to = new Point(t.X, t.Y + HeaderHeight / 2);
            _canvas.Children.Insert(0, new Line { StartPoint = from, EndPoint = to, Stroke = Accent, StrokeThickness = 1.5 });
            _canvas.Children.Add(new TextBlock { Text = t.Join.Value.ToString().ToLowerInvariant(), FontSize = 10, Opacity = 0.8, [Canvas.LeftProperty] = (from.X + to.X) / 2 - 12, [Canvas.TopProperty] = (from.Y + to.Y) / 2 - 14 });
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        if (_dragFrom != null && Document.FindTable(_dragFrom) is { } t)
        {
            t.X = Math.Max(0, p.X - BoxWidth / 2);
            t.Y = Math.Max(0, p.Y - HeaderHeight / 2);
            var box = _canvas.Children.OfType<Border>().FirstOrDefault(b => Equals(b.Tag, _dragFrom));
            if (box != null) { Canvas.SetLeft(box, t.X); Canvas.SetTop(box, t.Y); }
        }
        else if (_linkLine != null) _linkLine.EndPoint = p;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        var p = e.GetPosition(_canvas);
        if (_dragFrom != null)
        {
            _dragFrom = null;
            _dirty = true;
            DrawDiagram();
            return;
        }
        if (_linkFrom is { } from)
        {
            _linkFrom = null;
            if (_linkLine != null) _canvas.Children.Remove(_linkLine);
            _linkLine = null;
            // The field row under the pointer.
            foreach (var t in Document.Tables)
            {
                if (p.X < t.X || p.X > t.X + BoxWidth || t.Ref.Equals(from.Table, StringComparison.OrdinalIgnoreCase)) continue;
                var row = (int)((p.Y - t.Y - HeaderHeight) / RowHeight);
                var fields = FieldsOf(t);
                if (row >= 0 && row < fields.Count) Try(() => Join(from.Table, from.Field, t.Ref, fields[row]));
            }
        }
    }

    // ---- Tabs ---------------------------------------------------------------------------------------

    private static TextBlock Note(string text) => new() { Text = text, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };

    private static Button Small(string text, Action act)
    {
        var b = new Button { Content = text, Padding = new Thickness(6, 0), FontSize = 11, Margin = new Thickness(2, 0) };
        b.Click += (_, _) => act();
        return b;
    }

    private static TextBox Edit(string value, Action<string> commit, double width = 200, string? watermark = null)
    {
        var box = new TextBox { Text = value, Width = width, Watermark = watermark, FontFamily = Mono, FontSize = 12 };
        box.LostFocus += (_, _) => { if ((box.Text ?? "") != value) commit(box.Text ?? ""); };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && (box.Text ?? "") != value) commit(box.Text ?? ""); };
        return box;
    }

    private static StackPanel Row(params Control[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var i in items) row.Children.Add(i);
        return row;
    }

    private void FillFieldsTab()
    {
        _fieldsTab.Children.Clear();
        _fieldsTab.Children.Add(Note("Output fields, in order. Double-click a field in the diagram to add it; leave the list empty for all fields (*)."));
        for (int i = 0; i < Document.Fields.Count; i++)
        {
            var index = i;
            var f = Document.Fields[i];
            _fieldsTab.Children.Add(Row(
                Edit(f.Expression, v => { Document.Fields[index] = f with { Expression = v.Trim() }; Touch(); }, 280),
                new TextBlock { Text = "AS", VerticalAlignment = VerticalAlignment.Center },
                Edit(f.Alias ?? "", v => { Document.Fields[index] = f with { Alias = string.IsNullOrWhiteSpace(v) ? null : v.Trim() }; Touch(); }, 120),
                Small("▲", () => Move(Document.Fields, index, -1)), Small("▼", () => Move(Document.Fields, index, 1)),
                Small("✕", () => { Document.Fields.RemoveAt(index); Touch(); })));
        }
        var expr = new TextBox { Width = 280, Watermark = "expression (customer.name, SUM(orders.total) …)", FontFamily = Mono, FontSize = 12 };
        var alias = new TextBox { Width = 120, Watermark = "name", FontFamily = Mono, FontSize = 12 };
        _fieldsTab.Children.Add(Row(expr, new TextBlock { Text = "AS", VerticalAlignment = VerticalAlignment.Center }, alias,
            Small("Add", () => { if (!string.IsNullOrWhiteSpace(expr.Text)) AddField(expr.Text.Trim(), alias.Text); })));
    }

    private void Move<T>(List<T> list, int i, int delta)
    {
        var j = i + delta;
        if (j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        Touch();
    }

    private void FillJoinTab()
    {
        _joinTab.Children.Clear();
        if (Document.Tables.Count < 2) { _joinTab.Children.Add(Note("Add a second table to join it to the first.")); return; }
        foreach (var t in Document.Tables.Skip(1))
        {
            var table = t;
            var kinds = new[] { "Inner", "Left outer", "Right outer", "Full outer", "None (cross)" };
            var combo = new ComboBox { ItemsSource = kinds, SelectedIndex = t.Join is { } k ? (int)k : 4, Width = 130 };
            combo.SelectionChanged += (_, _) =>
            {
                table.Join = combo.SelectedIndex is >= 0 and < 4 ? (JoinKind)combo.SelectedIndex : null;
                if (table.Join != null && string.IsNullOrWhiteSpace(table.On)) table.On = ".T.";
                Touch();
            };
            _joinTab.Children.Add(Row(new TextBlock { Text = table.Ref, Width = 110, VerticalAlignment = VerticalAlignment.Center }, combo,
                new TextBlock { Text = "ON", VerticalAlignment = VerticalAlignment.Center },
                Edit(t.On ?? "", v => { table.On = string.IsNullOrWhiteSpace(v) ? null : v.Trim(); Touch(); }, 360)));
        }
    }

    private void FillConditions(StackPanel panel, List<QueryCondition> list, string help)
    {
        panel.Children.Clear();
        panel.Children.Add(Note(help));
        for (int i = 0; i < list.Count; i++)
        {
            var index = i;
            var c = list[i];
            var op = new ComboBox { ItemsSource = new[] { "(expression)" }.Concat(QueryCondition.Operators).ToList(), SelectedIndex = c.Op.Length == 0 ? 0 : Array.IndexOf(QueryCondition.Operators, c.Op) + 1, Width = 110 };
            op.SelectionChanged += (_, _) => { list[index] = c with { Op = op.SelectedIndex <= 0 ? "" : QueryCondition.Operators[op.SelectedIndex - 1] }; Touch(); };
            var not = new CheckBox { Content = "Not", IsChecked = c.Not };
            not.IsCheckedChanged += (_, _) => { list[index] = c with { Not = not.IsChecked == true }; Touch(); };
            var or = new ComboBox { ItemsSource = new[] { "AND", "OR" }, SelectedIndex = c.Or ? 1 : 0, Width = 70, IsVisible = i < list.Count - 1 };
            or.SelectionChanged += (_, _) => { list[index] = c with { Or = or.SelectedIndex == 1 }; Touch(); };
            panel.Children.Add(Row(Edit(c.Left, v => { list[index] = c with { Left = v.Trim() }; Touch(); }, 200), not, op,
                Edit(c.Right, v => { list[index] = c with { Right = v.Trim() }; Touch(); }, 200), or, Small("✕", () => { list.RemoveAt(index); Touch(); })));
        }
        var left = new TextBox { Width = 200, Watermark = "field or expression", FontFamily = Mono, FontSize = 12 };
        var opNew = new ComboBox { ItemsSource = QueryCondition.Operators, SelectedIndex = 0, Width = 110 };
        var right = new TextBox { Width = 200, Watermark = "value ('Boston', 100 …)", FontFamily = Mono, FontSize = 12 };
        panel.Children.Add(Row(left, opNew, right, Small("Add", () =>
        {
            if (string.IsNullOrWhiteSpace(left.Text)) return;
            var o = QueryCondition.Operators[Math.Max(0, opNew.SelectedIndex)];
            list.Add(new QueryCondition(left.Text.Trim(), string.IsNullOrWhiteSpace(right.Text) && o != "IS NULL" ? "" : o, o == "IS NULL" ? "" : right.Text?.Trim() ?? ""));
            Touch();
        })));
    }

    private void FillOrderTab()
    {
        _orderTab.Children.Clear();
        _orderTab.Children.Add(Note("Sort the results by these expressions (or output field numbers)."));
        for (int i = 0; i < Document.OrderBy.Count; i++)
        {
            var index = i;
            var o = Document.OrderBy[i];
            var desc = new CheckBox { Content = "Descending", IsChecked = o.Descending };
            desc.IsCheckedChanged += (_, _) => { Document.OrderBy[index] = o with { Descending = desc.IsChecked == true }; Touch(); };
            _orderTab.Children.Add(Row(Edit(o.Expression, v => { Document.OrderBy[index] = o with { Expression = v.Trim() }; Touch(); }, 280), desc,
                Small("▲", () => Move(Document.OrderBy, index, -1)), Small("▼", () => Move(Document.OrderBy, index, 1)), Small("✕", () => { Document.OrderBy.RemoveAt(index); Touch(); })));
        }
        var expr = new TextBox { Width = 280, Watermark = "expression", FontFamily = Mono, FontSize = 12 };
        _orderTab.Children.Add(Row(expr, Small("Add", () => { if (!string.IsNullOrWhiteSpace(expr.Text)) AddOrder(expr.Text.Trim()); })));
    }

    private void FillGroupTab()
    {
        _groupTab.Children.Clear();
        _groupTab.Children.Add(Note("Group records with the same values (GROUP BY); output fields that are not grouped should be aggregates (SUM, COUNT …)."));
        for (int i = 0; i < Document.GroupBy.Count; i++)
        {
            var index = i;
            _groupTab.Children.Add(Row(Edit(Document.GroupBy[i], v => { Document.GroupBy[index] = v.Trim(); Touch(); }, 280), Small("✕", () => { Document.GroupBy.RemoveAt(index); Touch(); })));
        }
        var expr = new TextBox { Width = 280, Watermark = "expression", FontFamily = Mono, FontSize = 12 };
        _groupTab.Children.Add(Row(expr, Small("Add", () => { if (!string.IsNullOrWhiteSpace(expr.Text)) { Document.GroupBy.Add(expr.Text.Trim()); Touch(); } })));
        var having = new StackPanel { Spacing = 4, Margin = new Thickness(0, 10, 0, 0) };
        FillConditions(having, Document.Having, "Groups must meet these conditions (HAVING).");
        _groupTab.Children.Add(having);
    }

    private void FillMiscTab()
    {
        _miscTab.Children.Clear();
        var distinct = new CheckBox { Content = "No duplicates (DISTINCT)", IsChecked = Document.Distinct };
        distinct.IsCheckedChanged += (_, _) => { Document.Distinct = distinct.IsChecked == true; Touch(); };
        _miscTab.Children.Add(distinct);
        var percent = new CheckBox { Content = "Percent", IsChecked = Document.Percent };
        percent.IsCheckedChanged += (_, _) => { Document.Percent = percent.IsChecked == true; Touch(); };
        _miscTab.Children.Add(Row(new TextBlock { Text = "Top", Width = 90, VerticalAlignment = VerticalAlignment.Center },
            Edit(Document.Top?.ToString() ?? "", v => { Document.Top = int.TryParse(v, out var n) && n > 0 ? n : null; Touch(); }, 80, "all"), percent));
        if (IsView) return;
        var dest = new ComboBox { ItemsSource = Enum.GetNames<QueryDestination>(), SelectedIndex = (int)Document.Destination, Width = 120 };
        dest.SelectionChanged += (_, _) => { Document.Destination = (QueryDestination)Math.Max(0, dest.SelectedIndex); Touch(); };
        _miscTab.Children.Add(Row(new TextBlock { Text = "Destination", Width = 90, VerticalAlignment = VerticalAlignment.Center }, dest,
            Edit(Document.IntoName ?? "", v => { Document.IntoName = string.IsNullOrWhiteSpace(v) ? null : v.Trim(); Touch(); }, 160, "name")));
        var rw = new CheckBox { Content = "Read-write cursor", IsChecked = Document.ReadWrite, IsEnabled = Document.Destination == QueryDestination.Cursor };
        rw.IsCheckedChanged += (_, _) => { Document.ReadWrite = rw.IsChecked == true; Touch(); };
        _miscTab.Children.Add(rw);
    }

    // ---- Views ----------------------------------------------------------------------------------------

    private string LayoutKey => "view_layout:" + (ViewName ?? "").ToUpperInvariant();

    private void LoadViewState()
    {
        if (ViewName == null || _viewDb!.GetView(ViewName) is not { } v) return;
        SendUpdates = v.Get("SendUpdates").AsBool;
        WhereType = (int)v.Get("WhereType").AsNumber;
        UpdateType = (int)v.Get("UpdateType").AsNumber;
        foreach (var field in v.FieldProperties.Keys)
        {
            if (v.GetField(field, "KeyField").AsBool) KeyFields.Add(field);
            if (v.GetField(field, "Updatable").AsBool) UpdatableFields.Add(field);
        }
        if (_viewDb.GetMeta(LayoutKey) is { Length: > 0 } json)
        {
            try
            {
                var layout = JsonSerializer.Deserialize<Dictionary<string, double[]>>(json) ?? [];
                foreach (var t in Document.Tables)
                    if (layout.TryGetValue(t.Ref.ToUpperInvariant(), out var xy) && xy.Length == 2) (t.X, t.Y) = (xy[0], xy[1]);
            }
            catch (JsonException) { }
        }
    }

    /// <summary>The view's output column names (for the update criteria).</summary>
    public IReadOnlyList<string> OutputColumns() => Document.Fields.Select(f => f.Alias ?? (f.Expression.Contains('.') ? f.Expression[(f.Expression.LastIndexOf('.') + 1)..] : f.Expression)).ToList();

    private void FillUpdateTab()
    {
        _updateTab.Children.Clear();
        var send = new CheckBox { Content = "Send SQL updates (TABLEUPDATE writes changes to the base tables)", IsChecked = SendUpdates };
        send.IsCheckedChanged += (_, _) => { SendUpdates = send.IsChecked == true; _dirty = true; };
        _updateTab.Children.Add(send);
        _updateTab.Children.Add(Note("Key fields identify a record in its base table; updatable fields are written back."));
        foreach (var col in OutputColumns())
        {
            var name = col;
            var key = new CheckBox { Content = "Key", IsChecked = KeyFields.Contains(name) };
            key.IsCheckedChanged += (_, _) => { if (key.IsChecked == true) KeyFields.Add(name); else KeyFields.Remove(name); _dirty = true; };
            var upd = new CheckBox { Content = "Updatable", IsChecked = UpdatableFields.Contains(name) };
            upd.IsCheckedChanged += (_, _) => { if (upd.IsChecked == true) UpdatableFields.Add(name); else UpdatableFields.Remove(name); _dirty = true; };
            _updateTab.Children.Add(Row(new TextBlock { Text = name, Width = 160, FontFamily = Mono, VerticalAlignment = VerticalAlignment.Center }, key, upd));
        }
        var where = new ComboBox { ItemsSource = new[] { "Key fields only", "Key and updatable fields", "Key and modified fields", "Key and timestamp" }, SelectedIndex = Math.Clamp(WhereType - 1, 0, 3), Width = 220 };
        where.SelectionChanged += (_, _) => { WhereType = where.SelectedIndex + 1; _dirty = true; };
        var how = new ComboBox { ItemsSource = new[] { "SQL UPDATE", "SQL DELETE then INSERT" }, SelectedIndex = UpdateType == 2 ? 1 : 0, Width = 220 };
        how.SelectionChanged += (_, _) => { UpdateType = how.SelectedIndex + 1; _dirty = true; };
        _updateTab.Children.Add(Row(new TextBlock { Text = "SQL WHERE clause", Width = 160, VerticalAlignment = VerticalAlignment.Center }, where));
        _updateTab.Children.Add(Row(new TextBlock { Text = "Update using", Width = 160, VerticalAlignment = VerticalAlignment.Center }, how));
    }

    // ---- Run and save -------------------------------------------------------------------------------

    /// <summary>Runs the query into a scratch cursor and shows its first rows on the Results tab. Returns the record count.</summary>
    public int RunPreview(int maxRows = 200)
    {
        const string cursor = "_jpqpreview";
        var sql = Document.IsSqlOnly ? Document.Sql! : Document.ToSql(withDestination: false);
        if (Document.IsSqlOnly && QueryDocument.FromSql(sql).Destination != QueryDestination.Browse)
            throw new InvalidOperationException("The SQL has its own INTO clause; run it from the Command Window.");
        var rt = _ide.Runtime;
        var saved = rt.Session.CurrentAreaNumber;
        rt.ExecuteCommand(sql + " INTO CURSOR " + cursor);
        var wa = rt.Session.FindAlias(cursor)!;
        var lines = new List<string>();
        try
        {
            var fields = wa.Table.Fields;
            var widths = fields.Select(f => Math.Clamp(f.Type is 'C' or 'V' ? f.Width : 12, f.Name.Length, 30)).ToList();
            lines.Add(string.Join("  ", fields.Select((f, i) => f.Name.ToLowerInvariant().PadRight(widths[i]))));
            wa.GoTop();
            while (!wa.Eof && lines.Count <= maxRows)
            {
                lines.Add(string.Join("  ", fields.Select((_, i) =>
                {
                    var text = Formatter.ToDisplay(wa.Get(i), rt.Options).Trim();
                    return (text.Length > widths[i] ? text[..widths[i]] : text).PadRight(widths[i]);
                })));
                wa.Skip();
            }
            var count = wa.RecordCount;
            if (count > maxRows) lines.Add($"… {count - maxRows} more record(s)");
            _results.ItemsSource = lines;
            _tabs.SelectedIndex = _tabs.Items.Count - 1;
            Status?.Invoke($"{count} record(s).");
            return count;
        }
        finally
        {
            wa.Close();
            rt.Session.Select(saved);
        }
    }

    /// <summary>Saves the query file (or the view in its database). Returns false when it needs a file name or cannot be saved.</summary>
    public bool Save(string? path = null)
    {
        if (!Document.IsSqlOnly && _sql.Text.Trim() != (IsView ? Document.ToSql(false) : Document.ToSql()).Trim())
            ApplySql(_sql.Text);   // unapplied edits in the SQL pane win
        if (IsView)
        {
            if (string.IsNullOrWhiteSpace(ViewName) || !TableDesign.IsName(ViewName)) { Status?.Invoke("Give the view a valid name."); return false; }
            var sql = Document.IsSqlOnly ? Document.Sql! : Document.ToSql(withDestination: false);
            try
            {
                var v = _ide.Runtime.DefineView(_viewDb!, ViewName, sql);
                v.Set("SendUpdates", Value.Logical(SendUpdates));
                v.Set("WhereType", Value.Number(WhereType));
                v.Set("UpdateType", Value.Number(UpdateType));
                foreach (var col in OutputColumns())
                {
                    v.SetField(col, "KeyField", Value.Logical(KeyFields.Contains(col)));
                    v.SetField(col, "Updatable", Value.Logical(UpdatableFields.Contains(col)));
                }
                _viewDb!.SaveView(v);
                _viewDb.SetMeta(LayoutKey, JsonSerializer.Serialize(Document.Tables.ToDictionary(t => t.Ref.ToUpperInvariant(), t => new[] { Math.Round(t.X), Math.Round(t.Y) })));
            }
            catch (Exception ex) when (ex is VfpException or InvalidOperationException) { Status?.Invoke(ex.Message); return false; }
            _dirty = false;
            Saved?.Invoke();
            Status?.Invoke($"Saved view {ViewName.ToLowerInvariant()} in {_viewDb!.Name.ToLowerInvariant()}.");
            return true;
        }
        path ??= FilePath;
        if (path == null) { SaveAsRequested?.Invoke(); return false; }
        if (System.IO.Path.GetExtension(path).Equals(".qpr", StringComparison.OrdinalIgnoreCase)) path = System.IO.Path.ChangeExtension(path, ".jpquery");
        Document.Save(path);
        FilePath = path;
        _dirty = false;
        Saved?.Invoke();
        Status?.Invoke($"Saved {System.IO.Path.GetFileName(path)}.");
        return true;
    }

    private void AddTableDialog()
    {
        var session = _ide.Runtime.Session;
        var names = new List<string>();
        foreach (var db in session.OpenDatabases)
        {
            var prefix = db == (_viewDb ?? session.CurrentDatabase) ? "" : db.Name.ToLowerInvariant() + "!";
            names.AddRange(db.TableNames().Select(t => prefix + t.ToLowerInvariant()));
            names.AddRange(db.ObjectNames(DbObjectStore.ViewKind).Select(t => prefix + t.ToLowerInvariant()));
        }
        names.AddRange(Directory.EnumerateFiles(_ide.Runtime.Options.Default_, "*" + Store.FreeTableExtension).Select(f => System.IO.Path.GetFileNameWithoutExtension(f).ToLowerInvariant()));
        var list = new ListBox { ItemsSource = names.Distinct().ToList(), Height = 260, Width = 260 };
        var other = new TextBox { Watermark = "or a table name / path", Width = 260 };
        var ok = new Button { Content = "Add", IsDefault = true, Classes = { "accent" } };
        var window = new Window
        {
            Title = "Add table or view", SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false,
            Content = new StackPanel { Margin = new Thickness(12), Spacing = 6, Children = { list, other, ok } },
        };
        void Add()
        {
            var name = !string.IsNullOrWhiteSpace(other.Text) ? other.Text.Trim() : list.SelectedItem as string;
            if (name != null) Try(() => AddTable(name));
            window.Close();
        }
        ok.Click += (_, _) => Add();
        list.DoubleTapped += (_, _) => Add();
        if (TopLevel.GetTopLevel(this) is Window owner) window.ShowDialog(owner);
        else window.Show();
    }
}
