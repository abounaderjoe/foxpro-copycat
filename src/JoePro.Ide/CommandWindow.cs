using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;

namespace JoePro.Ide;

/// <summary>
/// The Command Window: a text buffer where Enter executes the current line (or the selection), as in
/// VFP. Earlier lines stay in the buffer and can be edited and re-executed. Block statements (IF, FOR,
/// DEFINE CLASS…) keep collecting lines until the block is complete.
/// </summary>
public sealed class CommandWindow : UserControl
{
    private readonly IdeSession _session;
    private int? _blockStartLine;

    public CommandWindow(IdeSession session)
    {
        _session = session;
        Editor = new TextEditor
        {
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"),
            FontSize = 13,
            ShowLineNumbers = false,
            WordWrap = false,
            Padding = new Avalonia.Thickness(4),
        };
        Avalonia.Automation.AutomationProperties.SetName(Editor, "Command Window");
        Editor.TextArea.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Content = Editor;
    }

    public TextEditor Editor { get; }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && e.KeyModifiers == KeyModifiers.None)
            e.Handled = ExecuteAtCaret();
    }

    /// <summary>Executes the selection, or the line under the caret (or the block ending there).</summary>
    public bool ExecuteAtCaret()
    {
        var doc = Editor.Document;
        if (Editor.SelectionLength > 0)
        {
            _session.Execute(Editor.SelectedText);
            MoveToNewLastLine();
            return true;
        }
        var lineNo = doc.GetLineByOffset(Editor.CaretOffset).LineNumber;
        var start = _blockStartLine ?? lineNo;
        if (start > lineNo) start = lineNo;
        var from = doc.GetLineByNumber(start);
        var to = doc.GetLineByNumber(lineNo);
        var code = doc.GetText(from.Offset, to.EndOffset - from.Offset);
        if (code.Trim().Length == 0)
        {
            _blockStartLine = null;
            return false;
        }
        if (IdeSession.NeedsMoreInput(code))
        {
            _blockStartLine = start;
            return false; // let Enter insert a new line inside the block
        }
        _blockStartLine = null;
        _session.Execute(code);
        MoveToNewLastLine();
        return true;
    }

    private void MoveToNewLastLine()
    {
        var doc = Editor.Document;
        var last = doc.GetLineByNumber(doc.LineCount);
        if (last.Length > 0) doc.Insert(doc.TextLength, Environment.NewLine);
        Editor.CaretOffset = doc.TextLength;
        Editor.SelectionLength = 0;
        ScrollCaretIntoView();
    }

    private void ScrollCaretIntoView() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Editor.TextArea.Caret.BringCaretToView(), Avalonia.Threading.DispatcherPriority.Background);

    /// <summary>Types a command on a new last line and executes it (used by menus and the command palette).</summary>
    public void Run(string command)
    {
        var doc = Editor.Document;
        var last = doc.GetLineByNumber(doc.LineCount);
        if (last.Length > 0) doc.Insert(doc.TextLength, Environment.NewLine);
        doc.Insert(doc.TextLength, command);
        Editor.CaretOffset = doc.TextLength;
        _blockStartLine = null;
        _session.Execute(command);
        MoveToNewLastLine();
    }

    public void SetDark(bool dark) => Editor.SyntaxHighlighting = FoxProHighlighting.Get(dark);
}
