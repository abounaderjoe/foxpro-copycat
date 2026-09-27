using System.Text;
using JoePro.Core;
using JoePro.Language;

namespace JoePro.Runtime;

/// <summary>SET options with behavior beyond a flag: DATASESSION, ALTERNATE, CONSOLE, and ASSERT; other SET values are stored for SET().</summary>
public sealed partial class Interpreter
{
    private StreamWriter? _alternateFile;
    private bool _alternateOn;

    internal bool AlternateOn => _alternateOn;
    internal string AlternateFile => (_alternateFile?.BaseStream as FileStream)?.Name.ToUpperInvariant() ?? "";
    internal IEnumerable<string> ProcedureFileNames => _procedureFiles.Select(u => (u.File ?? u.Name).ToUpperInvariant());

    private int _sessionCounter;

    /// <summary>Numbers a new data session from 1 within this runtime (the default session is 1, as in VFP) and tracks it.</summary>
    private void RegisterSession(JoePro.Data.DataSession session)
    {
        session.Id = ++_sessionCounter;
        Sessions.Add(session);
    }

    /// <summary>SET DATASESSION TO n: makes another data session current.</summary>
    private void SetDataSession(int id)
    {
        var s = Sessions.FirstOrDefault(x => x.Id == id) ?? throw new VfpException(1540, $"Data session number {id} is invalid.");
        Session = s;
    }

    /// <summary>SET ALTERNATE TO [file [ADDITIVE]] and SET ALTERNATE ON | OFF: a copy of ?, ?? and LIST output goes to a file.</summary>
    private void SetAlternate(SetStmt st, string? value)
    {
        var words = st.Raw.Select(t => t.Text.ToUpperInvariant()).ToList();
        if (!words.Contains("TO"))
        {
            _alternateOn = value == "ON";
            EnsureRouter();
            return;
        }
        _alternateFile?.Dispose();
        _alternateFile = null;
        var additive = words.Contains("ADDITIVE");
        var name = (value ?? "").Trim();
        if (name.EndsWith(" ADDITIVE", StringComparison.OrdinalIgnoreCase)) name = name[..^" ADDITIVE".Length].Trim();
        name = name.Trim('"', '\'');
        if (name.Length == 0) return;
        var path = Path.Combine(Options.Default_, Path.HasExtension(name) ? name : name + ".txt");
        _alternateFile = new StreamWriter(path, additive, new UTF8Encoding(false)) { AutoFlush = true };
        EnsureRouter();
    }

    /// <summary>Other SET values: kept so SET() returns what was set.</summary>
    private void StoreSetting(SetStmt st)
    {
        if (st.Option.Length == 0) return;
        string text;
        if (st.Expr != null)
        {
            try { text = Formatter.ToDisplay(Eval(st.Expr), Options).Trim(); }
            catch (VfpException) { text = st.Value ?? ""; }
        }
        else text = st.Value ?? "";
        if (text.Length > 0) Options.Values[st.Option] = text;
    }

    private void EnsureRouter()
    {
        if (Output is not ConsoleRouter) Output = new ConsoleRouter(this, Output);
    }

    /// <summary>ASSERT lExpression [MESSAGE cMessageText]: with SET ASSERTS ON, a false expression stops in the debugger or reports.</summary>
    private void AssertCommand(string rest)
    {
        if (!Options.Asserts) return;
        var m = System.Text.RegularExpressions.Regex.Match(rest, @"^(?<e>.*?)(\s+MESS\w*\s+(?<m>.+))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        var ok = Truthy(Eval(Parser.ParseExpression(m.Groups["e"].Value)));
        if (ok) return;
        var message = m.Groups["m"].Success
            ? Formatter.ToDisplay(Eval(Parser.ParseExpression(m.Groups["m"].Value)), Options)
            : $"Assertion failed on line {_frame.Line} of procedure {_frame.Program.ToLowerInvariant()}.";
        if (Debugger != null)
        {
            Debugger.Output(message);
            Debugger.Suspend(_frame, _frame.Line);
        }
        else if (MessageBox != null) MessageBox(message, "Assertion Failed", 48);
        else Notify(message);
    }

    /// <summary>Sends output to the screen unless SET CONSOLE is OFF, and to the SET ALTERNATE file while it is ON.</summary>
    private sealed class ConsoleRouter(Interpreter rt, IConsoleOutput inner) : IConsoleOutput, IClearableOutput
    {
        private int _column;
        public IConsoleOutput Inner => inner;
        public int Column => rt.Options.Console ? inner.Column : _column;

        public void Write(string text)
        {
            if (rt.Options.Console) inner.Write(text);
            if (rt._alternateOn) rt._alternateFile?.Write(text);
            var nl = text.LastIndexOf('\n');
            _column = nl < 0 ? _column + text.Length : text.Length - nl - 1;
        }

        public void NewLine()
        {
            if (rt.Options.Console) inner.NewLine();
            if (rt._alternateOn) rt._alternateFile?.WriteLine();
            _column = 0;
        }

        public void Clear() => (inner as IClearableOutput)?.Clear();
    }
}
