using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using JoePro.Core;
using JoePro.Data;
using JoePro.Tooling;

namespace JoePro.Ide;

/// <summary>MODIFY MEMO: a memo field of the current record in an editor; Save writes it back to that record.</summary>
public sealed class MemoEditorTab : DocumentTab
{
    private readonly IdeSession _session;
    private bool _dirty;

    public MemoEditorTab(IdeSession session, string alias, string field)
    {
        _session = session;
        var wa = session.Runtime.Session.ResolveAlias(alias);
        if (wa.Eof) throw new VfpException(ErrorCodes.EndOfFile, "End of file encountered.");
        Alias = wa.Alias;
        Field = field.ToUpperInvariant();
        FieldIndex = wa.FieldIndex(field) is var i && i >= 0 ? i : throw VfpException.FieldNotFound(field);
        RecNo = wa.RecNo;
        var v = wa.Get(FieldIndex);
        Editor = new TextEditor
        {
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"),
            FontSize = 13,
            WordWrap = true,
            Padding = new Thickness(4),
            Text = v.IsNull ? "" : v.Kind == ValueKind.Character ? v.AsString : Formatter.ToDisplay(v, session.Runtime.Options),
        };
        Avalonia.Automation.AutomationProperties.SetName(Editor, $"{Alias}.{Field}");
        Editor.TextChanged += (_, _) => { _dirty = true; UpdateTitle(); };
        Content = Editor;
        UpdateTitle();
    }

    public string Alias { get; }
    public string Field { get; }
    public int FieldIndex { get; }
    public int RecNo { get; }
    public TextEditor Editor { get; }
    public bool IsDirty => _dirty;

    private void UpdateTitle() => Title = $"{Alias.ToLowerInvariant()}.{Field.ToLowerInvariant()} (record {RecNo})" + (_dirty ? " •" : "");

    /// <summary>Writes the text into the record it was read from (the record pointer is left where it is).</summary>
    public void Save()
    {
        var wa = _session.Runtime.Session.ResolveAlias(Alias);
        var (rec, eof) = (wa.RecNo, wa.Eof);
        wa.Go(RecNo);
        wa.Replace(FieldIndex, Value.String(Editor.Text));
        if (eof) { wa.GoBottom(); if (!wa.Eof) wa.Skip(); }
        else if (rec != RecNo) wa.Go(rec);
        _dirty = false;
        UpdateTitle();
    }
}

/// <summary>MODIFY CONNECTION: a named connection of the current database (data source, login, connection string).</summary>
public sealed class ConnectionEditorTab : DocumentTab
{
    private readonly Store _database;
    private readonly TextBox _name = new(), _dataSource = new(), _user = new(), _password = new() { PasswordChar = '•' }, _db = new(),
        _connString = new() { AcceptsReturn = false, Watermark = "Driver=…;Server=…  or  Provider=sqlite;Data Source=file.db" };
    private readonly TextBlock _message = new() { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };

    public ConnectionEditorTab(Store database, string? name)
    {
        _database = database;
        var existing = name != null ? database.GetConnection(name) ?? throw new VfpException(1562, $"Cannot find object {name.ToUpperInvariant()} in the database.") : null;
        OriginalName = existing?.Name;
        _name.Text = existing?.Name ?? "";
        _dataSource.Text = existing?.DataSource ?? "";
        _user.Text = existing?.UserId ?? "";
        _password.Text = existing?.Password ?? "";
        _db.Text = existing?.Database ?? "";
        _connString.Text = existing?.ConnectString ?? "";
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("160,*"), Margin = new Thickness(12), RowSpacing = 6 };
        void Row(string label, Control box)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(l, grid.RowDefinitions.Count - 1);
            Grid.SetRow(box, grid.RowDefinitions.Count - 1);
            Grid.SetColumn(box, 1);
            Avalonia.Automation.AutomationProperties.SetName(box, label.TrimEnd(':'));
            grid.Children.Add(l);
            grid.Children.Add(box);
        }
        Row("Connection name:", _name);
        Row("Data source (DSN):", _dataSource);
        Row("User ID:", _user);
        Row("Password:", _password);
        Row("Database:", _db);
        Row("Connection string:", _connString);
        var save = new Button { Content = "Save" };
        save.Click += (_, _) => Save();
        var test = new Button { Content = "Test connection" };
        test.Click += (_, _) => TestRequested?.Invoke(Build().BuildConnectString());
        Content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = $"Connection in database {database.Name.ToLowerInvariant()}. A connection string overrides the other settings.", Margin = new Thickness(12, 12, 12, 0), Opacity = 0.8 },
                grid,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0), Children = { save, test } },
                new Border { Child = _message, Margin = new Thickness(12, 8) },
            },
        };
        Title = "Connection " + (OriginalName ?? "(new)").ToLowerInvariant();
    }

    public string? OriginalName { get; private set; }

    /// <summary>Raised by "Test connection" with the connection string (the host tries it with SQLSTRINGCONNECT).</summary>
    public event Action<string>? TestRequested;

    public void ShowMessage(string text) => _message.Text = text;

    private ConnectionDefinition Build()
    {
        string? T(TextBox b) => string.IsNullOrWhiteSpace(b.Text) ? null : b.Text.Trim();
        var c = OriginalName != null ? _database.GetConnection(OriginalName) ?? new ConnectionDefinition() : new ConnectionDefinition();
        c.Name = (T(_name) ?? throw new VfpException(ErrorCodes.InvalidArgument, "The connection needs a name.")).ToUpperInvariant();
        c.DataSource = T(_dataSource);
        c.UserId = T(_user);
        c.Password = T(_password);
        c.Database = T(_db);
        c.ConnectString = T(_connString);
        return c;
    }

    public void Save()
    {
        try
        {
            var c = Build();
            if (OriginalName != null && !OriginalName.Equals(c.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (_database.GetConnection(c.Name) != null) throw new VfpException(1562, $"A connection named {c.Name} already exists.");
                _database.DeleteObject(DbObjectStore.ConnectionKind, OriginalName);
            }
            _database.SaveConnection(c);
            OriginalName = c.Name;
            Title = "Connection " + c.Name.ToLowerInvariant();
            ShowMessage("Saved.");
        }
        catch (VfpException ex) { ShowMessage(ex.Message); }
    }

    public void SetField(string which, string value)
    {
        var box = which switch { "name" => _name, "datasource" => _dataSource, "user" => _user, "password" => _password, "database" => _db, _ => _connString };
        box.Text = value;
    }
}

/// <summary>HELP [topic]: the language reference in the IDE, searchable, from the same docs as hover and completion.</summary>
public sealed class HelpTab : DocumentTab
{
    private readonly TextBox _search = new() { Watermark = "Search functions and commands" };
    private readonly ListBox _list = new();
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), FontSize = 13 };
    private readonly List<(string Name, string Text)> _topics;

    public HelpTab(string? topic)
    {
        _topics = FunctionDocs.Functions.Select(kv => (kv.Key.ToUpperInvariant() + "()", $"{kv.Value.Signature}\n\n{kv.Value.Summary}" + (kv.Value.Returns.Length > 0 ? $"\n\nReturns: {kv.Value.Returns}" : "")))
            .Concat(FunctionDocs.Commands.Select(kv => (kv.Key.ToUpperInvariant(), kv.Value)))
            .OrderBy(t => t.Item1, StringComparer.Ordinal).ToList();
        Avalonia.Automation.AutomationProperties.SetName(_list, "Help topics");
        Avalonia.Automation.AutomationProperties.SetName(_search, "Search help");
        _search.TextChanged += (_, _) => Fill(_search.Text ?? "");
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is string s) Show(s); };
        var left = new DockPanel { Width = 260 };
        DockPanel.SetDock(_search, Dock.Top);
        left.Children.Add(_search);
        left.Children.Add(_list);
        var root = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);
        root.Children.Add(new ScrollViewer { Content = _detail });
        Content = root;
        Title = "Help";
        Fill("");
        if (!string.IsNullOrWhiteSpace(topic)) Select(topic);
        else _detail.Text = "Choose a function or command on the left, or type to search. The full reference is in docs/reference/language.md; " +
                            "what Joe Pro supports compared with Visual FoxPro 9 is in docs/reference/coverage.md.";
    }

    public string DetailText => _detail.Text ?? "";

    /// <summary>Shows a topic: an exact function or command name, else the first match.</summary>
    public void Select(string topic)
    {
        var t = topic.Trim().TrimEnd('(', ')').ToUpperInvariant();
        var hit = _topics.FirstOrDefault(x => x.Name == t + "()" || x.Name == t);
        if (hit.Name == null)
        {
            _search.Text = t;
            Fill(t);
            hit = _topics.FirstOrDefault(x => x.Name.StartsWith(t, StringComparison.Ordinal));
        }
        if (hit.Name != null) { _list.SelectedItem = hit.Name; Show(hit.Name); }
        else _detail.Text = $"No help topic for {topic}.";
    }

    private void Fill(string q)
    {
        _list.ItemsSource = _topics.Where(t => q.Length == 0 || t.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Name).ToList();
    }

    private void Show(string name) => _detail.Text = _topics.First(t => t.Name == name).Text;
}
