using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using JoePro.Runtime;

namespace JoePro.Ide;

/// <summary>A variable in the Locals/Watch trees. Children are captured while paused (handles expire on resume).</summary>
public sealed class VariableNode
{
    public VariableNode(VariableInfo info, IReadOnlyList<VariableNode> children)
    {
        Info = info;
        Children = children;
    }

    public VariableInfo Info { get; }
    public IReadOnlyList<VariableNode> Children { get; }
    public override string ToString() => $"{Info.Name} = {Info.Value}  ({Info.Type})";
}

/// <summary>
/// The IDE side of the debugger: stops the program on the UI thread with a nested dispatcher frame,
/// shows the current line in the editor and fills the Locals, Watch, Call Stack and Debug Output panes.
/// </summary>
public sealed class IdeDebugger : IDebugHost
{
    private readonly MainWindow _window;
    private DispatcherFrame? _frame;
    private DebugAction _action;

    public IdeDebugger(MainWindow window, IdeSession session)
    {
        _window = window;
        Engine = new Debugger(session.Runtime) { Host = this, BreakOnErrors = false };
        Panel = new DebugPanel(this);
    }

    public Debugger Engine { get; }
    public DebugPanel Panel { get; }
    public DebugStop? CurrentStop { get; private set; }
    public int SelectedFrame { get; private set; }
    public bool IsPaused => CurrentStop != null;
    public List<string> Watches { get; } = new();
    public event Action? StateChanged;

    public DebugAction Paused(DebugStop stop)
    {
        CurrentStop = stop;
        SelectedFrame = 0;
        ShowStop();
        _frame = new DispatcherFrame();
        Dispatcher.UIThread.PushFrame(_frame);
        _frame = null;
        CurrentStop = null;
        _window.ClearExecutionLine();
        Panel.Clear();
        StateChanged?.Invoke();
        return _action;
    }

    public void Output(string text) => Dispatcher.UIThread.Post(() => Panel.AppendOutput(text));

    /// <summary>Resumes a paused program (Continue, Step, Stop buttons and keys).</summary>
    public void Resume(DebugAction action)
    {
        if (_frame == null) return;
        _action = action;
        _frame.Continue = false;
    }

    /// <summary>Set Next Statement (Ctrl+Shift+F10): runs <paramref name="line"/> next, if it is in a block now executing.</summary>
    public bool SetNextStatement(int line)
    {
        if (CurrentStop == null || !CurrentStop.SetNextStatement(line)) return false;
        Resume(DebugAction.Goto);
        return true;
    }

    public void SelectFrame(int index)
    {
        if (CurrentStop == null || index < 0 || index >= CurrentStop.Frames.Count) return;
        SelectedFrame = index;
        var f = CurrentStop.Frames[index];
        _window.ShowExecutionLine(f.File, f.Line);
        Panel.ShowVariables(Locals(), WatchNodes());
    }

    private void ShowStop()
    {
        var stop = CurrentStop!;
        var top = stop.Top;
        _window.ShowExecutionLine(top.File, top.Line);
        var reason = stop.Reason switch
        {
            StopReason.Breakpoint => "Breakpoint",
            StopReason.Step => "Step",
            StopReason.Suspend => "Suspended",
            StopReason.Exception => stop.Message ?? "Error",
            StopReason.Goto => "Moved",
            _ => "Paused",
        };
        _window.SetStatus($"{reason} in {top.Name}, line {top.Line}. F5 continue · F10 step over · F11 step into · Shift+F11 step out · Ctrl+Shift+F10 set next statement · Shift+F5 stop");
        Panel.ShowStack(stop.Frames);
        Panel.ShowVariables(Locals(), WatchNodes());
        StateChanged?.Invoke();
    }

    private List<VariableNode> Locals() =>
        CurrentStop!.Locals(SelectedFrame).Select(v => Node(v, 0)).ToList();

    public List<VariableNode> WatchNodes() =>
        CurrentStop == null ? [] : Watches.Select(w => Node(CurrentStop.Evaluate(w, SelectedFrame), 0)).ToList();

    private VariableNode Node(VariableInfo v, int depth) =>
        new(v, v.ChildrenRef > 0 && depth < 3 ? CurrentStop!.Children(v.ChildrenRef).Take(200).Select(c => Node(c, depth + 1)).ToList() : []);

    public void AddWatch(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return;
        Watches.Add(expression.Trim());
        Panel.ShowVariables(CurrentStop == null ? null : Locals(), WatchNodes());
    }

    public void RemoveWatch(string expression)
    {
        Watches.Remove(expression);
        Panel.ShowVariables(CurrentStop == null ? null : Locals(), WatchNodes());
    }
}

/// <summary>Locals, Watch, Call Stack and Debug Output tabs.</summary>
public sealed class DebugPanel : UserControl
{
    private readonly IdeDebugger _debugger;
    private readonly TextBox _output;

    public DebugPanel(IdeDebugger debugger)
    {
        _debugger = debugger;
        Locals = NewTree();
        WatchTree = NewTree();
        Stack = new ListBox { FontFamily = Mono, FontSize = 12 };
        Stack.SelectionChanged += (_, _) => { if (Stack.SelectedIndex >= 0) _debugger.SelectFrame(Stack.SelectedIndex); };
        _output = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.NoWrap, BorderThickness = new Thickness(0) };

        WatchInput = new TextBox { Watermark = "Add watch expression and press Enter" };
        WatchInput.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            _debugger.AddWatch(WatchInput.Text ?? "");
            WatchInput.Text = "";
            e.Handled = true;
        };
        WatchTree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && WatchTree.SelectedItem is VariableNode n) _debugger.RemoveWatch(n.Info.Name);
        };
        var watchPane = new DockPanel();
        DockPanel.SetDock(WatchInput, Dock.Top);
        watchPane.Children.Add(WatchInput);
        watchPane.Children.Add(WatchTree);

        Tabs = new TabControl
        {
            Padding = new Thickness(0),
            Items =
            {
                Tab("Locals", Locals),
                Tab("Watch", watchPane),
                Tab("Call Stack", Stack),
                Tab("Debug Output", _output),
            },
        };
        Content = Tabs;
    }

    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace");

    public TabControl Tabs { get; }
    public TreeView Locals { get; }
    public TreeView WatchTree { get; }
    public TextBox WatchInput { get; }
    public ListBox Stack { get; }
    public string OutputText => _output.Text ?? "";

    private static TabItem Tab(string header, Control content) =>
        new() { Header = header, Content = content, FontSize = 13, MinHeight = 30, Padding = new Thickness(10, 3) };

    private static TreeView NewTree() => new()
    {
        FontFamily = Mono,
        FontSize = 12,
        ItemTemplate = new FuncTreeDataTemplate<VariableNode>((n, _) => new TextBlock { Text = n.ToString() }, n => n.Children),
    };

    public void ShowStack(IReadOnlyList<StackFrameInfo> frames)
    {
        Stack.ItemsSource = frames.Select(f => $"{f.Name}  line {f.Line}{(f.File != null ? "  " + Path.GetFileName(f.File) : "")}").ToList();
        Stack.SelectedIndex = 0;
    }

    public void ShowVariables(List<VariableNode>? locals, List<VariableNode> watches)
    {
        if (locals != null) Locals.ItemsSource = locals;
        WatchTree.ItemsSource = watches.Count > 0 ? watches : _debugger.Watches.Select(w => new VariableNode(new VariableInfo(w, "(not paused)", "", "Watch", 0), [])).ToList();
    }

    public void Clear()
    {
        Locals.ItemsSource = null;
        Stack.ItemsSource = null;
        ShowVariables(null, []);
    }

    public void AppendOutput(string text)
    {
        _output.Text = (_output.Text ?? "") + text + Environment.NewLine;
        _output.CaretIndex = _output.Text.Length;
    }
}

/// <summary>Breakpoint gutter: a dot per breakpoint line; clicking toggles a breakpoint.</summary>
public sealed class BreakpointMargin : AbstractMargin
{
    private readonly Func<string?> _file;
    private readonly Debugger _debugger;

    public BreakpointMargin(Func<string?> file, Debugger debugger)
    {
        _file = file;
        _debugger = debugger;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override Size MeasureOverride(Size availableSize) => new(16, 0);

    public override void Render(DrawingContext context)
    {
        var view = TextView;
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var file = _file();
        if (view == null || file == null || !view.VisualLinesValid) return;
        var lines = BreakpointLines(file);
        foreach (var vl in view.VisualLines)
        {
            var n = vl.FirstDocumentLine.LineNumber;
            if (!lines.Contains(n)) continue;
            var y = vl.VisualTop - view.VerticalOffset + vl.Height / 2;
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE5, 0x14, 0x00)), null, new Point(8, y), 5, 5);
        }
    }

    private HashSet<int> BreakpointLines(string file)
    {
        var full = Path.GetFullPath(file);
        return _debugger.Breakpoints.Where(b => b.Kind == BreakpointKind.Line && b.File != null && string.Equals(Path.GetFullPath(b.File), full, StringComparison.OrdinalIgnoreCase)).Select(b => b.Line).ToHashSet();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var view = TextView;
        var file = _file();
        if (view == null || file == null) return;
        var pos = e.GetPosition(view);
        var vl = view.GetVisualLineFromVisualTop(pos.Y + view.VerticalOffset);
        if (vl == null) return;
        _debugger.ToggleLineBreakpoint(file, vl.FirstDocumentLine.LineNumber);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView != null) oldTextView.VisualLinesChanged -= Redraw;
        if (newTextView != null) newTextView.VisualLinesChanged += Redraw;
        base.OnTextViewChanged(oldTextView, newTextView);
    }

    private void Redraw(object? sender, EventArgs e) => InvalidateVisual();
}

/// <summary>Highlights the line where execution is stopped.</summary>
public sealed class ExecutionLineRenderer : IBackgroundRenderer
{
    public int Line { get; set; }
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Line <= 0 || !textView.VisualLinesValid) return;
        foreach (var vl in textView.VisualLines)
        {
            if (vl.FirstDocumentLine.LineNumber != Line) continue;
            var y = vl.VisualTop - textView.VerticalOffset;
            drawingContext.FillRectangle(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xD7, 0x00)), new Rect(0, y, textView.Bounds.Width, vl.Height));
        }
    }
}
