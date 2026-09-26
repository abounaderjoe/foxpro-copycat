using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using JoePro.Tooling;

namespace JoePro.Ide;

/// <summary>Connects a code editor to the language service: squiggles, completion, hover and go-to-definition.</summary>
public sealed class EditorIntelligence
{
    private readonly TextEditor _editor;
    private readonly LanguageService _service;
    private readonly Func<string?> _file;
    private readonly DiagnosticRenderer _renderer = new();
    private readonly DispatcherTimer _debounce;
    private CompletionWindow? _completion;

    public EditorIntelligence(TextEditor editor, LanguageService service, Func<string?> file)
    {
        _editor = editor;
        _service = service;
        _file = file;
        editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RefreshDiagnostics(); };
        editor.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        editor.TextArea.TextEntered += (_, e) => { if (e.Text == ".") ShowCompletion(); };
        editor.TextArea.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { ShowCompletion(); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        editor.PointerMoved += OnPointerMoved;
        RefreshDiagnostics();
    }

    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];

    /// <summary>Raised with the location to open when the user asks for a definition (F12).</summary>
    public event Action<SymbolLocation>? NavigateRequested;

    public void RefreshDiagnostics()
    {
        Diagnostics = _service.Diagnostics(_editor.Text, _file());
        _renderer.Diagnostics = Diagnostics;
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    public IReadOnlyList<CompletionItem> CompletionsAtCaret()
    {
        var caret = _editor.TextArea.Caret;
        return _service.Complete(_editor.Text, caret.Line, caret.Column);
    }

    public void ShowCompletion()
    {
        var items = CompletionsAtCaret();
        if (items.Count == 0) return;
        _completion?.Close();
        _completion = new CompletionWindow(_editor.TextArea);
        // Replace the word fragment before the caret.
        var offset = _editor.CaretOffset;
        var start = offset;
        while (start > 0 && (char.IsLetterOrDigit(_editor.Document.GetCharAt(start - 1)) || _editor.Document.GetCharAt(start - 1) == '_')) start--;
        _completion.StartOffset = start;
        foreach (var i in items) _completion.CompletionList.CompletionData.Add(new CompletionData(i));
        _completion.Closed += (_, _) => _completion = null;
        _completion.Show();
    }

    public void GoToDefinition()
    {
        var caret = _editor.TextArea.Caret;
        var loc = _service.Definition(_editor.Text, caret.Line, caret.Column, _file());
        if (loc != null) NavigateRequested?.Invoke(loc);
    }

    public string? HoverAt(int line, int column)
    {
        var diag = Diagnostics.FirstOrDefault(d => d.Line == line && column >= d.Column && column <= d.Column + d.Length);
        var info = _service.Hover(_editor.Text, line, column);
        if (diag == null) return info;
        return info == null ? diag.Message : diag.Message + "\n\n" + info;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var view = _editor.TextArea.TextView;
        var pos = view.GetPositionFloor(e.GetPosition(view) + view.ScrollOffset);
        var tip = pos is { } p ? HoverAt(p.Line, p.Column) : null;
        ToolTip.SetTip(_editor, tip);
        ToolTip.SetIsOpen(_editor, tip != null);
    }

    private sealed class CompletionData(CompletionItem item) : ICompletionData
    {
        public IImage? Image => null;
        public string Text => item.Label;
        public object Content => item.Label;
        public object? Description => item.Detail;
        public double Priority => item.Kind is CompletionKind.Field or CompletionKind.Property or CompletionKind.Variable ? 1 : 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, item.InsertText ?? item.Label);
    }
}

/// <summary>Draws wavy underlines under diagnostics (red errors, amber warnings, blue information).</summary>
public sealed class DiagnosticRenderer : IBackgroundRenderer
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; set; } = [];
    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Diagnostics.Count == 0 || !textView.VisualLinesValid || textView.Document == null) return;
        foreach (var d in Diagnostics)
        {
            if (d.Line < 1 || d.Line > textView.Document.LineCount) continue;
            var line = textView.Document.GetLineByNumber(d.Line);
            var start = line.Offset + Math.Clamp(d.Column - 1, 0, line.Length);
            var end = Math.Min(line.EndOffset, start + Math.Max(1, d.Length));
            var color = d.Severity switch
            {
                DiagnosticSeverity.Error => Color.FromRgb(0xE5, 0x14, 0x00),
                DiagnosticSeverity.Warning => Color.FromRgb(0xD1, 0x8A, 0x00),
                _ => Color.FromRgb(0x2F, 0x81, 0xF7),
            };
            var pen = new Pen(new SolidColorBrush(color), 1.2);
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = start, EndOffset = end }))
            {
                var y = rect.Bottom - 1;
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(new Point(rect.Left, y), false);
                    bool up = true;
                    for (var x = rect.Left + 2; x <= rect.Right + 2; x += 2)
                    {
                        ctx.LineTo(new Point(x, up ? y - 2 : y));
                        up = !up;
                    }
                    ctx.EndFigure(false);
                }
                drawingContext.DrawGeometry(null, pen, geo);
            }
        }
    }
}
