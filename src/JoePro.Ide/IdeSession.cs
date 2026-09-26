using System.Text;
using JoePro.Core;
using JoePro.Language;
using JoePro.Migration;
using JoePro.Runtime;
using JoePro.Ui.Runtime;

namespace JoePro.Ide;

/// <summary>The Screen pane's output device (_SCREEN): collects ? / LIST output.</summary>
public sealed class ScreenOutput : IConsoleOutput, IClearableOutput
{
    private readonly StringBuilder _text = new();

    public int Column { get; private set; }
    public string Text => _text.ToString();
    public event Action? Changed;

    public void Write(string text)
    {
        _text.Append(text);
        var nl = text.LastIndexOf('\n');
        Column = nl >= 0 ? text.Length - nl - 1 : Column + text.Length;
        Changed?.Invoke();
    }

    public void NewLine()
    {
        _text.Append('\n');
        Column = 0;
        Changed?.Invoke();
    }

    public void Clear()
    {
        _text.Clear();
        Column = 0;
        Changed?.Invoke();
    }
}

/// <summary>
/// The IDE's non-visual state: one runtime (the default data session), the UI host for forms and
/// tool windows, the Screen output, and Command Window execution with block handling.
/// </summary>
public sealed class IdeSession : IDisposable
{
    public IdeSession(string startDirectory)
    {
        MigrationCommands.Register();
        Screen = new ScreenOutput();
        Runtime = new Interpreter(Screen, startDirectory);
        Host = new AvaloniaUiHost(Runtime);
        Runtime.Status += m => StatusMessage?.Invoke(m);
        Host.Error += ex => ReportError(ex);
        Runtime.MessageBox = (text, title, flags) =>
        {
            StatusMessage?.Invoke($"[{title}] {text}");
            return (flags & 0xF) switch { 3 or 4 => 6, 2 => 3, _ => 1 };
        };
    }

    public Interpreter Runtime { get; }
    public AvaloniaUiHost Host { get; }
    public ScreenOutput Screen { get; }
    public List<string> History { get; } = new();

    public event Action<string>? StatusMessage;
    public event Action<VfpException>? ErrorRaised;
    /// <summary>Raised after every command so tool windows (Data Session, status bar) can refresh.</summary>
    public event Action? CommandCompleted;

    /// <summary>Executes Command Window input. Errors are written to the Screen and reported, never thrown.</summary>
    public bool Execute(string code)
    {
        code = code.TrimEnd();
        if (code.Trim().Length == 0) return true;
        History.Add(code);
        try
        {
            Runtime.ExecuteCommand(code);
            return true;
        }
        catch (VfpException ex)
        {
            ReportError(ex);
            return false;
        }
        catch (QuitException)
        {
            QuitRequested?.Invoke();
            return true;
        }
        finally
        {
            CommandCompleted?.Invoke();
        }
    }

    public event Action? QuitRequested;

    private void ReportError(VfpException ex)
    {
        var where = ex.ErrorLine is { } l && l > 0 && ex.ErrorProgram is { } p && p != "(command)" ? $" ({p}, line {l})" : "";
        if (Screen.Column > 0) Screen.NewLine();
        Screen.Write($"Error {ex.Number}: {ex.Message}{where}");
        Screen.NewLine();
        ErrorRaised?.Invoke(ex);
    }

    /// <summary>True while <paramref name="text"/> is an unfinished block (IF without ENDIF, ';' continuation…).</summary>
    public static bool NeedsMoreInput(string text)
    {
        if (text.TrimEnd().EndsWith(';')) return true;
        try
        {
            Parser.ParseInteractive(text);
            return false;
        }
        catch (CompileException ex)
        {
            return ex.Message.StartsWith("Missing END", StringComparison.OrdinalIgnoreCase)
                   || ex.Message.Contains("for the block started", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Summary of open work areas for the Data Session window.</summary>
    public IReadOnlyList<WorkAreaInfo> WorkAreas() =>
        Runtime.Session.OpenWorkAreas().Select(w => new WorkAreaInfo(
            w.Number, w.Alias, w.Eof ? w.RecordCount + 1 : w.RecNo, w.RecordCount, w.Order?.Name, w.Session.CurrentAreaNumber == w.Number,
            w.Source, w.BufferMode)).ToList();

    public void Dispose()
    {
        foreach (var f in Host.OpenForms.ToList()) Runtime.Release(f);
        foreach (var s in Runtime.Sessions.ToList()) s.Dispose();
    }
}

public sealed record WorkAreaInfo(int Number, string Alias, int RecNo, int RecordCount, string? Order, bool Selected, string Source, int BufferMode)
{
    public override string ToString() =>
        $"{(Selected ? "▶" : " ")} {Number,3}  {Alias,-14} {(RecNo > RecordCount ? "EOF" : RecNo.ToString()),5}/{RecordCount,-6}{(Order != null ? " [" + Order + "]" : "")}";
}
