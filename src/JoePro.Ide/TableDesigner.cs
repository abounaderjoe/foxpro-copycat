using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using JoePro.Core;
using JoePro.Data;

namespace JoePro.Ide;

/// <summary>A table in the Table Designer (MODIFY STRUCTURE, or from the Database Designer).</summary>
public sealed class TableDesignerTab : DocumentTab
{
    public TableDesignerTab(TableDesigner designer)
    {
        Designer = designer;
        Content = designer;
        designer.Changed += UpdateTitle;
        designer.Saved += UpdateTitle;
        UpdateTitle();
    }

    public TableDesigner Designer { get; }
    private void UpdateTitle() => Title = Strings.F("{0} · structure", Designer.Design.Name.ToLowerInvariant()) + (Designer.IsDirty ? " •" : "");
}

/// <summary>
/// The Table Designer: fields (name, type, width, decimals, null, autoincrement, and for database tables caption,
/// default, validation rule, comment, format, input mask and display class), indexes (regular, unique, candidate,
/// primary) and the table's record rule, triggers and comment. It lists the changes before saving and shows the
/// FoxPro script that makes them; saving rebuilds the table only when its structure changed.
/// </summary>
public sealed class TableDesigner : UserControl
{
    public static readonly (string Name, char Type)[] Types =
    [
        ("Character", 'C'), ("Varchar", 'V'), ("Numeric", 'N'), ("Float", 'F'), ("Double", 'B'), ("Integer", 'I'), ("Currency", 'Y'),
        ("Date", 'D'), ("DateTime", 'T'), ("Logical", 'L'), ("Memo", 'M'), ("General", 'G'), ("Blob", 'W'), ("Varbinary", 'Q'),
    ];

    private static readonly string[] TagKinds = ["Regular", "Unique", "Candidate", "Primary"];

    private readonly IdeSession _ide;
    private readonly Store? _database;
    private readonly string? _freePath;
    private readonly TextBox _name = new() { Width = 220 };
    private readonly ListBox _fields = new() { [Avalonia.Automation.AutomationProperties.NameProperty] = Strings.T("Fields"), FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"), FontSize = 12 };
    private readonly ListBox _tags = new() { [Avalonia.Automation.AutomationProperties.NameProperty] = Strings.T("Index tags"), FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"), FontSize = 12 };
    private readonly StackPanel _fieldEditor = new() { Spacing = 4, Margin = new Thickness(10, 8) };
    private readonly StackPanel _tagEditor = new() { Spacing = 4, Margin = new Thickness(10, 8) };
    private readonly TextBlock _changes = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85, Margin = new Thickness(0, 4) };
    private readonly TextBlock _errors = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed };
    private readonly Dictionary<string, TextBox> _tableBoxes = new();
    private TableSchema? _original;
    private bool _loading;

    public TableDesigner(TableDesign design, Store? database, string? freeTablePath, IdeSession ide)
    {
        _ide = ide;
        _database = database;
        _freePath = freeTablePath;
        Design = design;
        _original = design.IsNew ? null : LoadOriginal();
        _name.Text = design.Name.ToLowerInvariant();
        _name.IsEnabled = database != null;   // a free table is renamed by renaming its file
        _name.TextChanged += (_, _) => { if (!_loading) { Design.Name = (_name.Text ?? "").Trim().ToUpperInvariant(); Touch(); } };
        _fields.SelectionChanged += (_, _) => { if (!_loading) ShowField(); };
        _tags.SelectionChanged += (_, _) => { if (!_loading) ShowTag(); };

        Button B(string text, string tip, Action act)
        {
            var b = new Button { Content = Strings.T(text), Padding = new Thickness(8, 2), FontSize = 12 };
            ToolTip.SetTip(b, Strings.T(tip));
            b.Click += (_, _) => act();
            return b;
        }
        var fieldButtons = new WrapPanel
        {
            Children =
            {
                B("Insert", "Insert a field after the selected one", () => AddField(UniqueFieldName(), 'C', 10)),
                B("Delete", "Remove the selected field", RemoveField),
                B("▲", "Move the selected field up", () => MoveField(-1)),
                B("▼", "Move the selected field down", () => MoveField(1)),
            },
        };
        var fieldList = new DockPanel();
        fieldButtons.Margin = new Thickness(4);
        DockPanel.SetDock(fieldButtons, Dock.Bottom);
        fieldList.Children.Add(fieldButtons);
        fieldList.Children.Add(_fields);
        var fieldListCard = IdeTheme.Card(Strings.T("Fields"), fieldList);
        fieldListCard.Width = 300;
        var fieldsTab = new DockPanel { Margin = new Thickness(2) };
        DockPanel.SetDock(fieldListCard, Dock.Left);
        fieldsTab.Children.Add(fieldListCard);
        fieldsTab.Children.Add(IdeTheme.Card(Strings.T("Field"), new ScrollViewer { Content = _fieldEditor }));

        var tagButtons = new WrapPanel
        {
            Children =
            {
                B("Insert", "Add an index", () => AddTag(UniqueTagName(), SelectedField?.Field.Name ?? Design.Fields.FirstOrDefault()?.Field.Name ?? "RECNO()", TagKind.Regular)),
                B("Delete", "Remove the selected index", () => { if (SelectedTag is { } t) RemoveTag(t.Name); }),
            },
        };
        var tagList = new DockPanel();
        tagButtons.Margin = new Thickness(4);
        DockPanel.SetDock(tagButtons, Dock.Bottom);
        tagList.Children.Add(tagButtons);
        tagList.Children.Add(_tags);
        var tagListCard = IdeTheme.Card(Strings.T("Indexes"), tagList);
        tagListCard.Width = 300;
        var tagsTab = new DockPanel { Margin = new Thickness(2) };
        DockPanel.SetDock(tagListCard, Dock.Left);
        tagsTab.Children.Add(tagListCard);
        tagsTab.Children.Add(IdeTheme.Card(Strings.T("Index"), new ScrollViewer { Content = _tagEditor }));

        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { FontSize = 14, Header = Strings.T("Fields"), Content = fieldsTab },
                new TabItem { FontSize = 14, Header = Strings.T("Indexes"), Content = tagsTab },
                new TabItem { FontSize = 14, Header = Strings.T("Table"), Content = new ScrollViewer { Content = TablePage() } },
            },
        };
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8),
            Children =
            {
                new TextBlock { Text = Strings.T("Table"), VerticalAlignment = VerticalAlignment.Center },
                _name,
                new TextBlock { Text = database != null ? $"in database {database.Name.ToLowerInvariant()}" : "free table", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 },
            },
        };
        var bottomButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                B("Show script", "The FoxPro commands that make these changes", () => ScriptRequested?.Invoke(Design.Name.ToLowerInvariant() + "-changes.prg", Script())),
                B("Revert", "Discard the changes", Revert),
                new Button { Content = Strings.T("Save"), Classes = { "accent" }, Padding = new Thickness(12, 2) },
            },
        };
        ((Button)bottomButtons.Children[^1]).Click += (_, _) => Save();
        var bottom = new StackPanel { Margin = new Thickness(8), Children = { _errors, _changes, bottomButtons } };
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(tabs);
        Content = root;
        FillFields();
        FillTags();
        Touch(changed: false);
    }

    public TableDesign Design { get; private set; }
    /// <summary>The database file, or the free table's file.</summary>
    public string? StorePath => _database?.Path ?? _freePath;
    public bool IsDatabaseTable => _database != null;
    public bool IsDirty => Changes.Count > 0;
    public IReadOnlyList<SchemaChange> Changes => Design.Changes(_original);
    public FieldDesign? SelectedField => _fields.SelectedIndex is var i and >= 0 && i < Design.Fields.Count ? Design.Fields[i] : null;
    public TagDef? SelectedTag => _tags.SelectedIndex is var i and >= 0 && i < Design.Tags.Count ? Design.Tags[i] : null;
    public string Errors => _errors.Text ?? "";

    public event Action? Changed;
    public event Action? Saved;
    public event Action<string>? Status;
    public event Action<string, string>? ScriptRequested;

    private TableSchema LoadOriginal() => _database != null
        ? _database.OpenTable(Design.OriginalName!, _ide.Runtime).Schema
        : _ide.Runtime.Session.StoreOf(_freePath!) is var s ? s.OpenTable(s.TableNames()[0], _ide.Runtime).Schema : throw new InvalidOperationException();

    private void Touch(bool changed = true)
    {
        var errors = Design.Validate();
        _errors.Text = string.Join("\n", errors);
        var changes = Changes;
        _changes.Text = changes.Count == 0 ? Strings.T("No changes.")
            : string.Join("\n", changes.Take(8).Select(c => "• " + c.Description)) + (changes.Count > 8 ? $"\n… and {changes.Count - 8} more." : "")
              + (changes.Any(c => c.NeedsRebuild) && _original != null ? "\nSaving rebuilds the table (its records are copied into the new structure)." : "");
        if (changed) Changed?.Invoke();
    }

    // ---- Fields ------------------------------------------------------------------------------------

    private static string FieldLine(FieldDef f) =>
        $"{f.Name.ToLowerInvariant(),-16}{TableDesign.TypeText(f),-8}{(f.Nullable ? "null " : "")}{(f.AutoIncNext != null ? "autoinc" : "")}";

    private void FillFields(int select = -1)
    {
        _loading = true;
        var keep = select >= 0 ? select : _fields.SelectedIndex;
        _fields.ItemsSource = Design.Fields.Select(f => FieldLine(f.Field)).ToList();
        _fields.SelectedIndex = Design.Fields.Count == 0 ? -1 : Math.Clamp(keep < 0 ? 0 : keep, 0, Design.Fields.Count - 1);
        _loading = false;
        ShowField();
    }

    private string UniqueFieldName()
    {
        for (int i = 1; ; i++)
            if (Design.FindField("FIELD" + i) == null) return "FIELD" + i;
    }

    public FieldDesign AddField(string name, char type, int width = 0, int decimals = 0)
    {
        var at = _fields.SelectedIndex < 0 ? Design.Fields.Count : _fields.SelectedIndex + 1;
        var fd = new FieldDesign(new FieldDef(name.ToUpperInvariant(), type, width == 0 && type is 'C' or 'V' or 'Q' ? 10 : width == 0 && type is 'N' or 'F' ? 10 : width, decimals));
        Design.Fields.Insert(at, fd);
        FillFields(at);
        Touch();
        return fd;
    }

    public void RemoveField()
    {
        if (_fields.SelectedIndex is not (var i and >= 0)) return;
        Design.Fields.RemoveAt(i);
        FillFields(Math.Min(i, Design.Fields.Count - 1));
        Touch();
    }

    public void MoveField(int delta)
    {
        var i = _fields.SelectedIndex;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Design.Fields.Count) return;
        (Design.Fields[i], Design.Fields[j]) = (Design.Fields[j], Design.Fields[i]);
        FillFields(j);
        Touch();
    }

    public void SelectField(string name)
    {
        var i = Design.Fields.FindIndex(f => f.Field.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) FillFields(i);
    }

    /// <summary>Changes the selected field.</summary>
    public void UpdateField(Func<FieldDef, FieldDef> change)
    {
        if (SelectedField is not { } fd) return;
        fd.Field = change(fd.Field);
        _loading = true;
        var i = _fields.SelectedIndex;
        _fields.ItemsSource = Design.Fields.Select(f => FieldLine(f.Field)).ToList();
        _fields.SelectedIndex = i;
        _loading = false;
        Touch();
    }

    private static int ParseInt(string? s, int fallback) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private Control Labeled(string label, Control editor, string? tip = null)
    {
        var l = new TextBlock { Text = Strings.T(label), Width = 130, VerticalAlignment = VerticalAlignment.Center };
        if (tip != null) ToolTip.SetTip(l, Strings.T(tip));
        var row = new DockPanel { Margin = new Thickness(0, 1) };
        DockPanel.SetDock(l, Dock.Left);
        row.Children.Add(l);
        row.Children.Add(editor);
        return row;
    }

    private TextBox Text(string? value, Action<string> changed, string? watermark = null)
    {
        // TextChanged can arrive after the editor was rebuilt for another field: only real edits count.
        var last = value ?? "";
        var box = new TextBox { Text = last, Watermark = watermark };
        box.TextChanged += (_, _) =>
        {
            var text = box.Text ?? "";
            if (_loading || text == last || box.Parent == null) return;
            last = text;
            changed(text);
        };
        return box;
    }

    private CheckBox Check(string text, bool value, Action<bool> changed, bool enabled = true)
    {
        var box = new CheckBox { Content = Strings.T(text), IsChecked = value, IsEnabled = enabled };
        box.IsCheckedChanged += (_, _) => { if (!_loading) changed(box.IsChecked == true); };
        return box;
    }

    private void ShowField()
    {
        _fieldEditor.Children.Clear();
        if (SelectedField is not { } fd) { _fieldEditor.Children.Add(new TextBlock { Text = Strings.T("Insert a field to start."), Opacity = 0.7 }); return; }
        _loading = true;
        var f = fd.Field;
        var t = char.ToUpperInvariant(f.Type);
        var type = new ComboBox { ItemsSource = Types.Select(x => x.Name).ToList(), SelectedIndex = Array.FindIndex(Types, x => x.Type == t), HorizontalAlignment = HorizontalAlignment.Stretch };
        type.SelectionChanged += (_, _) =>
        {
            if (_loading || type.SelectedIndex < 0) return;
            var nt = Types[type.SelectedIndex].Type;
            UpdateField(x => x with
            {
                Type = nt,
                Width = nt is 'C' or 'V' or 'Q' ? (x.Width is > 0 and <= 254 ? x.Width : 10) : nt is 'N' or 'F' ? (x.Width is > 0 and <= 20 ? x.Width : 10) : 0,
                Decimals = nt is 'N' or 'F' or 'B' ? x.Decimals : 0,
                AutoIncNext = nt == 'I' ? x.AutoIncNext : null,
            });
            ShowField();
        };
        var sized = t is 'C' or 'V' or 'Q' or 'N' or 'F';
        var withDecimals = t is 'N' or 'F' or 'B';
        _fieldEditor.Children.Add(Labeled("Name", Text(f.Name.ToLowerInvariant(), v => UpdateField(x => x with { Name = v.Trim().ToUpperInvariant() }))));
        _fieldEditor.Children.Add(Labeled("Type", type));
        var width = Text(sized ? f.Width.ToString(CultureInfo.InvariantCulture) : "", v => UpdateField(x => x with { Width = ParseInt(v, x.Width) }));
        width.IsEnabled = sized;
        _fieldEditor.Children.Add(Labeled("Width", width));
        var dec = Text(withDecimals ? f.Decimals.ToString(CultureInfo.InvariantCulture) : "", v => UpdateField(x => x with { Decimals = ParseInt(v, x.Decimals) }));
        dec.IsEnabled = withDecimals;
        _fieldEditor.Children.Add(Labeled("Decimals", dec));
        _fieldEditor.Children.Add(Check("Accepts null values", f.Nullable, v => UpdateField(x => x with { Nullable = v })));
        _fieldEditor.Children.Add(Check("Binary (no code page translation)", f.Binary, v => UpdateField(x => x with { Binary = v }), enabled: t is 'C' or 'V' or 'M'));
        _fieldEditor.Children.Add(Check("Autoincrement", f.AutoIncNext != null, v => { UpdateField(x => x with { AutoIncNext = v ? x.AutoIncNext ?? 1 : null }); ShowField(); }, enabled: t == 'I'));
        if (f.AutoIncNext is { } next)
        {
            _fieldEditor.Children.Add(Labeled("Next value", Text(next.ToString(CultureInfo.InvariantCulture), v => UpdateField(x => x with { AutoIncNext = long.TryParse(v, out var n) ? n : x.AutoIncNext }))));
            _fieldEditor.Children.Add(Labeled("Step", Text(f.AutoIncStep.ToString(CultureInfo.InvariantCulture), v => UpdateField(x => x with { AutoIncStep = ParseInt(v, x.AutoIncStep) }))));
        }
        _fieldEditor.Children.Add(new TextBlock { Text = Strings.T("Display"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        _fieldEditor.Children.Add(Labeled("Caption", Text(f.Caption, v => UpdateField(x => x with { Caption = Blank(v) })), "The label forms and grids show for the field"));
        _fieldEditor.Children.Add(Labeled("Format", Text(f.Format, v => UpdateField(x => x with { Format = Blank(v) }))));
        _fieldEditor.Children.Add(Labeled("Input mask", Text(f.InputMask, v => UpdateField(x => x with { InputMask = Blank(v) }))));
        _fieldEditor.Children.Add(Labeled("Display class", Text(f.DisplayClass, v => UpdateField(x => x with { DisplayClass = Blank(v) })), "The class the Form Designer uses when the field is dropped on a form"));
        _fieldEditor.Children.Add(Labeled("Class library", Text(f.DisplayClassLibrary, v => UpdateField(x => x with { DisplayClassLibrary = Blank(v) }))));
        _fieldEditor.Children.Add(Labeled("Comment", Text(f.Comment, v => UpdateField(x => x with { Comment = Blank(v) }))));
        _fieldEditor.Children.Add(new TextBlock { Text = Strings.T("Validation"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        if (!IsDatabaseTable) _fieldEditor.Children.Add(new TextBlock { Text = Strings.T("Defaults and rules need a table in a database."), Opacity = 0.7 });
        var rule = Text(f.RuleExpr, v => UpdateField(x => x with { RuleExpr = Blank(v) }), "expression that must be true");
        var ruleText = Text(f.RuleText, v => UpdateField(x => x with { RuleText = Blank(v) }), "message when the rule fails");
        var def = Text(f.DefaultExpr, v => UpdateField(x => x with { DefaultExpr = Blank(v) }), "expression");
        foreach (var box in new[] { rule, ruleText, def }) box.IsEnabled = IsDatabaseTable;
        _fieldEditor.Children.Add(Labeled("Rule", rule));
        _fieldEditor.Children.Add(Labeled("Message", ruleText));
        _fieldEditor.Children.Add(Labeled("Default value", def));
        _loading = false;
    }

    // ---- Indexes -----------------------------------------------------------------------------------

    private static string TagLine(TagDef t) =>
        $"{t.Name.ToLowerInvariant(),-12}{TagKinds[(int)t.Kind],-10}{t.Expression}{(t.Descending ? " desc" : "")}";

    private void FillTags(int select = -1)
    {
        _loading = true;
        var keep = select >= 0 ? select : _tags.SelectedIndex;
        _tags.ItemsSource = Design.Tags.Select(TagLine).ToList();
        _tags.SelectedIndex = Design.Tags.Count == 0 ? -1 : Math.Clamp(keep < 0 ? 0 : keep, 0, Design.Tags.Count - 1);
        _loading = false;
        ShowTag();
    }

    private string UniqueTagName()
    {
        for (int i = 1; ; i++)
            if (!Design.Tags.Any(t => t.Name.Equals("TAG" + i, StringComparison.OrdinalIgnoreCase))) return "TAG" + i;
    }

    public TagDef AddTag(string name, string expression, TagKind kind)
    {
        if (kind == TagKind.Primary && !IsDatabaseTable) kind = TagKind.Candidate;
        var tag = new TagDef(name.ToUpperInvariant(), expression, Kind: kind);
        Design.Tags.Add(tag);
        FillTags(Design.Tags.Count - 1);
        Touch();
        return tag;
    }

    public void RemoveTag(string name)
    {
        var i = Design.Tags.FindIndex(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        Design.Tags.RemoveAt(i);
        FillTags(Math.Min(i, Design.Tags.Count - 1));
        Touch();
    }

    public void UpdateTag(Func<TagDef, TagDef> change)
    {
        if (_tags.SelectedIndex is not (var i and >= 0) || i >= Design.Tags.Count) return;
        Design.Tags[i] = change(Design.Tags[i]);
        _loading = true;
        _tags.ItemsSource = Design.Tags.Select(TagLine).ToList();
        _tags.SelectedIndex = i;
        _loading = false;
        Touch();
    }

    private void ShowTag()
    {
        _tagEditor.Children.Clear();
        if (SelectedTag is not { } t) { _tagEditor.Children.Add(new TextBlock { Text = Strings.T("No indexes."), Opacity = 0.7 }); return; }
        _loading = true;
        var kinds = IsDatabaseTable ? TagKinds : TagKinds[..3];
        var kind = new ComboBox { ItemsSource = kinds, SelectedIndex = Math.Min((int)t.Kind, kinds.Length - 1), HorizontalAlignment = HorizontalAlignment.Stretch };
        kind.SelectionChanged += (_, _) =>
        {
            if (_loading || kind.SelectedIndex < 0) return;
            var k = (TagKind)kind.SelectedIndex;
            // One primary index per table: making this one primary demotes the other to candidate.
            if (k == TagKind.Primary)
                for (int i = 0; i < Design.Tags.Count; i++)
                    if (Design.Tags[i].Kind == TagKind.Primary && i != _tags.SelectedIndex) Design.Tags[i] = Design.Tags[i] with { Kind = TagKind.Candidate };
            UpdateTag(x => x with { Kind = k });
        };
        _tagEditor.Children.Add(Labeled("Name", Text(t.Name.ToLowerInvariant(), v => UpdateTag(x => x with { Name = v.Trim().ToUpperInvariant() }))));
        _tagEditor.Children.Add(Labeled("Type", kind, "Primary and candidate indexes allow no duplicate keys; a relation starts at one of them"));
        _tagEditor.Children.Add(Labeled("Expression", Text(t.Expression, v => UpdateTag(x => x with { Expression = v.Trim() }))));
        _tagEditor.Children.Add(Labeled("Filter", Text(t.ForExpression, v => UpdateTag(x => x with { ForExpression = Blank(v) }), "FOR expression (optional)")));
        _tagEditor.Children.Add(Check("Descending", t.Descending, v => UpdateTag(x => x with { Descending = v })));
        _loading = false;
    }

    // ---- Table ------------------------------------------------------------------------------------

    private Control TablePage()
    {
        var page = new StackPanel { Spacing = 4, Margin = new Thickness(12), MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Left };
        if (!IsDatabaseTable) page.Children.Add(new TextBlock { Text = Strings.T("Record rules, triggers and comments need a table in a database."), Opacity = 0.7 });
        TextBox Box(string key, string? value, Action<string?> set, string? watermark = null)
        {
            var box = Text(value, v => { set(Blank(v)); Touch(); }, watermark is null ? null : Strings.T(watermark));
            box.IsEnabled = IsDatabaseTable;
            _tableBoxes[key] = box;
            return box;
        }
        page.Children.Add(new TextBlock { Text = Strings.T("Record validation"), FontWeight = FontWeight.SemiBold });
        page.Children.Add(Labeled("Rule", Box("rule", Design.RuleExpr, v => Design.RuleExpr = v, "expression checked when a record is saved")));
        page.Children.Add(Labeled("Message", Box("ruletext", Design.RuleText, v => Design.RuleText = v)));
        page.Children.Add(new TextBlock { Text = Strings.T("Triggers"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        page.Children.Add(Labeled("Insert trigger", Box("insert", Design.InsertTrigger, v => Design.InsertTrigger = v, "expression; false refuses the insert")));
        page.Children.Add(Labeled("Update trigger", Box("update", Design.UpdateTrigger, v => Design.UpdateTrigger = v)));
        page.Children.Add(Labeled("Delete trigger", Box("delete", Design.DeleteTrigger, v => Design.DeleteTrigger = v)));
        page.Children.Add(new TextBlock { Text = Strings.T("Comment"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        page.Children.Add(Box("comment", Design.Comment, v => Design.Comment = v));
        return page;
    }

    // ---- Save --------------------------------------------------------------------------------------

    public string Script() => Design.Script(_original, IsDatabaseTable);

    /// <summary>Saves the design (creating or changing the table). Returns false, with the reason in <see cref="Status"/>, when it cannot.</summary>
    public bool Save()
    {
        if (Design.Validate() is { Count: > 0 } errors) { Status?.Invoke(errors[0]); return false; }
        if (_original != null && Changes.Count == 0) { Status?.Invoke(Strings.T("No changes to save.")); return true; }
        try
        {
            var path = _freePath ?? (_database == null ? System.IO.Path.Combine(_ide.Runtime.Options.Default_, Design.Name.ToLowerInvariant() + Store.FreeTableExtension) : null);
            var rebuilt = Changes.Any(c => c.NeedsRebuild) && _original != null;
            var table = _ide.Runtime.ApplyTableDesign(Design, _database, path);
            _original = table.Schema;
            Design = TableDesign.From(table.Schema);
            _loading = true;
            _name.Text = Design.Name.ToLowerInvariant();
            _loading = false;
            FillFields();
            FillTags();
            Touch(changed: false);
            Saved?.Invoke();
            Status?.Invoke(Strings.F("Saved {0}{1}.", Design.Name.ToLowerInvariant(), (rebuilt ? " (the table was rebuilt)" : "")));
            return true;
        }
        catch (Exception ex) when (ex is VfpException or InvalidOperationException or IOException or ArgumentException)
        {
            Status?.Invoke(ex.Message);
            _errors.Text = ex.Message;
            return false;
        }
    }

    public void Revert()
    {
        Design = _original != null ? TableDesign.From(_original) : TableDesign.New(Design.Name);
        _loading = true;
        _name.Text = Design.Name.ToLowerInvariant();
        foreach (var (key, box) in _tableBoxes)
            box.Text = key switch
            {
                "rule" => Design.RuleExpr, "ruletext" => Design.RuleText, "insert" => Design.InsertTrigger,
                "update" => Design.UpdateTrigger, "delete" => Design.DeleteTrigger, _ => Design.Comment,
            } ?? "";
        _loading = false;
        FillFields(0);
        FillTags(0);
        Touch();
    }
}
