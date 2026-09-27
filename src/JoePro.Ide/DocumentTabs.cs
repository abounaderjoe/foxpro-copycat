using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using JoePro.Ui.Runtime;

namespace JoePro.Ide;

/// <summary>A document shown in the IDE's tabbed area.</summary>
public abstract class DocumentTab : TabItem
{
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center };

    // Use the TabItem control theme for subclasses.
    protected override Type StyleKeyOverride => typeof(TabItem);

    protected DocumentTab()
    {
        FontSize = 13;
        MinHeight = 32;
        Padding = new Thickness(10, 4);
        var close = new Button { Content = "✕", Padding = new Thickness(4, 0), Margin = new Thickness(6, 0, 0, 0), FontSize = 10, Background = Brushes.Transparent };
        close.Click += (_, _) => CloseRequested?.Invoke(this);
        Header = new StackPanel { Orientation = Orientation.Horizontal, Children = { _title, close } };
    }

    public event Action<DocumentTab>? CloseRequested;

    public string Title
    {
        get => _title.Text ?? "";
        set
        {
            _title.Text = value;
            if (Header is StackPanel { Children: [_, Button close] }) Avalonia.Automation.AutomationProperties.SetName(close, "Close " + value);
            Avalonia.Automation.AutomationProperties.SetName(this, value);
        }
    }

    public virtual bool CanClose() => true;
}

/// <summary>A FoxPro source file in the code editor (MODIFY COMMAND).</summary>
public sealed class CodeEditorTab : DocumentTab
{
    private bool _dirty;

    public CodeEditorTab(string? path, bool dark, JoePro.Runtime.Debugger? debugger = null, JoePro.Tooling.LanguageService? language = null)
    {
        FilePath = path;
        Editor = new TextEditor
        {
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"),
            FontSize = 13,
            ShowLineNumbers = true,
            SyntaxHighlighting = FoxProHighlighting.Get(dark),
            Padding = new Thickness(4),
        };
        Avalonia.Automation.AutomationProperties.SetName(Editor, "Code editor");
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 3;
        if (path != null && File.Exists(path)) Editor.Text = File.ReadAllText(path);
        Editor.TextChanged += (_, _) => { _dirty = true; UpdateTitle(); };
        if (debugger != null)
        {
            Breakpoints = new BreakpointMargin(() => FilePath, debugger);
            Editor.TextArea.LeftMargins.Insert(0, Breakpoints);
        }
        Editor.TextArea.TextView.BackgroundRenderers.Add(ExecutionLine);
        if (language != null) Intelligence = new EditorIntelligence(Editor, language, () => FilePath);
        Content = Editor;
        UpdateTitle();
    }

    public BreakpointMargin? Breakpoints { get; }
    public EditorIntelligence? Intelligence { get; }
    public ExecutionLineRenderer ExecutionLine { get; } = new();

    public void ShowExecutionLine(int line)
    {
        ExecutionLine.Line = line;
        if (line > 0 && line <= Editor.Document.LineCount)
        {
            Editor.TextArea.Caret.Line = line;
            Editor.ScrollToLine(line);
        }
        Editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
    }

    public string? FilePath { get; private set; }
    public TextEditor Editor { get; }
    public bool IsDirty => _dirty;

    /// <summary>The name an unsaved editor shows and suggests when it is saved (a generated script, say).</summary>
    public string? SuggestedName
    {
        get => _suggested;
        set
        {
            _suggested = value;
            UpdateTitle();
        }
    }

    private string? _suggested;

    private void UpdateTitle() => Title = (FilePath != null ? Path.GetFileName(FilePath) : _suggested ?? "Untitled.prg") + (_dirty ? " •" : "");

    public void Save(string? path = null)
    {
        FilePath = path ?? FilePath ?? throw new InvalidOperationException("No file name.");
        File.WriteAllText(FilePath, Editor.Text);
        _dirty = false;
        UpdateTitle();
    }

    public void SetDark(bool dark) => Editor.SyntaxHighlighting = FoxProHighlighting.Get(dark);
}

/// <summary>An editable grid over a work area (BROWSE).</summary>
public sealed class BrowseTab : DocumentTab
{
    public BrowseTab(BrowseModel model, AvaloniaUiHost host, Action changed)
    {
        Model = model;
        Grid = host.CreateGrid(model, readOnly: false);
        var refresh = new Button { Content = "Refresh" };
        refresh.Click += (_, _) => { model.Load(); Title = model.Title; changed(); };
        var append = new Button { Content = "Append record" };
        append.Click += (_, _) => { model.AppendRow(); Title = model.Title; Grid.SelectedIndex = model.Rows.Count - 1; changed(); };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6), Children = { refresh, append,
            new TextBlock { Text = "Ctrl+T toggles the deleted mark", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 } } };
        var dock = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        dock.Children.Add(toolbar);
        dock.Children.Add(Grid);
        Content = dock;
        Title = model.Title;
    }

    public BrowseModel Model { get; }
    public DataGrid Grid { get; }
}

/// <summary>The Screen tab: output of ?, LIST, DISPLAY (the _SCREEN equivalent).</summary>
public sealed class ScreenTab : TabItem
{
    protected override Type StyleKeyOverride => typeof(TabItem);

    public ScreenTab(ScreenOutput screen)
    {
        Header = "Screen";
        FontSize = 13;
        MinHeight = 32;
        Padding = new Thickness(10, 4);
        Box = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"),
            FontSize = 13,
            BorderThickness = new Thickness(0),
        };
        Content = Box;
        var pending = false;
        screen.Changed += () =>
        {
            if (pending) return;
            pending = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                pending = false;
                Box.Text = screen.Text;
                Box.CaretIndex = Box.Text?.Length ?? 0;
            });
        };
    }

    public TextBox Box { get; }
}
