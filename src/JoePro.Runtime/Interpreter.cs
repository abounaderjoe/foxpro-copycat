using System.Text;
using System.Text.RegularExpressions;
using JoePro.Core;
using JoePro.Data;
using JoePro.Language;

namespace JoePro.Runtime;

/// <summary>Output device for ?, ??, LIST, DISPLAY and similar commands (the _SCREEN equivalent).</summary>
public interface IConsoleOutput
{
    int Column { get; }
    void Write(string text);
    void NewLine();
}

/// <summary>An output device that CLEAR can erase (the IDE's Screen pane).</summary>
public interface IClearableOutput
{
    void Clear();
}

public sealed class TextWriterOutput(TextWriter writer) : IConsoleOutput
{
    public int Column { get; private set; }

    public void Write(string text)
    {
        writer.Write(text);
        var nl = text.LastIndexOf('\n');
        Column = nl >= 0 ? text.Length - nl - 1 : Column + text.Length;
    }

    public void NewLine()
    {
        writer.WriteLine();
        Column = 0;
    }
}

/// <summary>Thrown by QUIT / CANCEL so the host can shut down.</summary>
public sealed class QuitException(bool cancel) : Exception("QUIT")
{
    public bool Cancel { get; } = cancel;
}

internal enum Flow { Normal, Exit, Loop, Return }

internal sealed class ReturnToMasterException : Exception;

/// <summary>
/// The Joe Pro runtime: executes FoxPro programs and Command Window input against a data session.
/// This first implementation walks the syntax tree; a bytecode VM with the same semantics is planned
/// (see docs/adr/0002).
/// </summary>
public sealed partial class Interpreter : IExpressionHost
{
    private readonly Dictionary<string, Variable> _publics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime Stamp, ProgramUnit Unit)> _programCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProgramUnit> _procedureFiles = new();
    private readonly List<ProgramUnit> _loadedUnits = new();
    private readonly Dictionary<string, ClassInfo> _classes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int Session, int Area), (Expr? For, Expr? While)> _locates = new();
    private readonly Stack<IRecord> _recordContext = new();
    private Frame _frame;
    private int _tryDepth;
    private int _callDepth;

    public Interpreter(IConsoleOutput? output = null, string? defaultDirectory = null)
    {
        Output = output ?? new TextWriterOutput(Console.Out);
        var options = new SetOptions();
        if (defaultDirectory != null) options.Default_ = Path.GetFullPath(defaultDirectory);
        Session = new DataSession(options, this);
        Sessions.Add(Session);
        _frame = new Frame("(command)", null);
        SetPublic("_TALLY", Value.Number(0));
    }

    public IConsoleOutput Output { get; set; }
    public DataSession Session { get; private set; }
    public List<DataSession> Sessions { get; } = new();
    public SetOptions Options => Session.Options;
    public Frame CurrentFrame => _frame;
    /// <summary>Informational messages (SET TALK, WAIT WINDOW, deprecation notes) for the host's status area.</summary>
    public event Action<string>? Status;
    /// <summary>Host hook for MESSAGEBOX(): (text, title, flags) → button result.</summary>
    public Func<string, string, int, int>? MessageBox { get; set; }
    public string? OnErrorCommand { get; private set; }
    /// <summary>The attached user-interface host, if any.</summary>
    public IUiHost? Ui { get; set; }
    /// <summary>The attached debugger, if any.</summary>
    public Debugger? Debugger { get; set; }
    public int LastErrorNumber { get; private set; }
    public string LastErrorMessage { get; private set; } = "";
    public int LastErrorLine { get; private set; }
    public string LastErrorProgram { get; private set; } = "";
    /// <summary>AERROR() elements 3–7 when the last error carries extra detail (ODBC errors).</summary>
    public Value[]? LastErrorDetail { get; private set; }
    /// <summary>SET TEXTMERGE ON: \ lines and TEXT … TEXTMERGE expand &lt;&lt;expressions&gt;&gt;.</summary>
    public bool TextMergeOn { get; private set; }
    private bool _textMergeShow = true;
    private string? _textMergeFile;

    /// <summary>SQL pass-through connections.</summary>
    public SqlPassThrough Remote => _remote ??= new SqlPassThrough(this);
    private SqlPassThrough? _remote;
    /// <summary>Connections shared by remote views with ShareConnection set, by connection name.</summary>
    internal Dictionary<string, int> SharedViewConnections { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> UnsupportedSettings { get; } = new();

    internal void Talk(string message)
    {
        if (Options.Talk) Status?.Invoke(message);
    }

    public void Notify(string message) => Status?.Invoke(message);

    // ================================================================================
    // Public API
    // ================================================================================

    /// <summary>Executes Command Window input (one or more lines).</summary>
    public void ExecuteCommand(string source)
    {
        var unit = Parser.ParseInteractive(source);
        RegisterUnit(unit);
        var saved = _frame.Unit;
        _frame.Unit ??= unit;
        try
        {
            RunTop(() => Exec(unit.Main));
        }
        finally
        {
            _frame.Unit = saved ?? _frame.Unit;
        }
    }

    /// <summary>Runs a program file (DO file.prg).</summary>
    public Value RunProgram(string path, params Value[] args)
    {
        var unit = LoadProgram(path);
        Value result = Value.True;
        RunTop(() => { result = CallUnitMain(unit, args.Select(a => new Arg(a, null)).ToList()); });
        return result;
    }

    /// <summary>Evaluates an expression in the current context.</summary>
    public Value Evaluate(string expression) => Eval(Parser.ParseExpression(expression));

    /// <summary>Evaluates an expression as if it ran inside <paramref name="frame"/> (debugger watches and hovers).</summary>
    public Value EvaluateInFrame(Frame frame, string expression)
    {
        var saved = _frame;
        var savedSession = Session;
        var savedTry = _tryDepth;
        _frame = frame;
        if (frame.This != null) Session = SessionFor(frame.This);
        _tryDepth++; // errors while evaluating never trigger ON ERROR
        try { return Evaluate(expression); }
        finally
        {
            _tryDepth = savedTry;
            _frame = saved;
            Session = savedSession;
        }
    }

    /// <summary>Assigns a variable in a frame (debugger Locals window editing).</summary>
    public void AssignInFrame(Frame frame, string name, string expression)
    {
        var value = EvaluateInFrame(frame, expression);
        var saved = _frame;
        _frame = frame;
        try { Assign(Parser.ParseExpression(name), value); }
        finally { _frame = saved; }
    }

    internal IEnumerable<Variable> PublicVariables() => _publics.Values;

    private void RunTop(Action body)
    {
        // Restore the frame we started in (not the root): commands typed while a program is suspended
        // in the debugger run inside the suspended program's frame and must leave it intact.
        var entry = _frame;
        try
        {
            body();
        }
        catch (ReturnToMasterException)
        {
        }
        catch (CancelProgramException)
        {
            Notify("Program canceled.");
        }
        finally
        {
            _frame = entry;
        }
    }

    // ================================================================================
    // Variables
    // ================================================================================

    public Variable? FindVariable(string name)
    {
        if (_frame.Locals.TryGetValue(name, out var v)) return v;
        for (var f = _frame; f != null; f = f.Parent)
            if (f.Privates.TryGetValue(name, out v)) return v;
        return _publics.TryGetValue(name, out v) ? v : null;
    }

    public Value GetVariable(string name)
    {
        var v = FindVariable(name) ?? throw VfpException.VariableNotFound(name);
        if (v.IsArray) return v.Array![1];
        return v.Value;
    }

    /// <summary>Assigns a memory variable, creating a PRIVATE variable in the current frame if it does not exist.</summary>
    public void SetVariable(string name, Value value)
    {
        var v = FindVariable(name);
        if (v == null)
        {
            if (_frame.Level == 1 && _frame.Parent == null && _callDepth == 0 && _frame.Program == "(command)")
            {
                // Variables created in the Command Window are PUBLIC-like in VFP (they live at the top level).
                _frame.Privates[name] = new Variable(name, value);
                return;
            }
            _frame.Privates[name] = new Variable(name, value);
            return;
        }
        if (v.IsArray) v.Array!.Fill(value); // assigning a scalar to an array fills every element
        else v.Value = value;
    }

    public void SetPublic(string name, Value value)
    {
        if (_publics.TryGetValue(name, out var v)) v.Value = value;
        else _publics[name] = new Variable(name, value);
    }

    private Variable Declare(string scope, string name)
    {
        switch (scope)
        {
            case "LOCAL":
            {
                var v = new Variable(name, Value.False);
                _frame.Locals[name] = v;
                return v;
            }
            case "PUBLIC":
            {
                if (!_publics.TryGetValue(name, out var v))
                    _publics[name] = v = new Variable(name, Value.False);
                return v;
            }
            case "PRIVATE":
            {
                var v = new Variable(name, Value.False);
                _frame.Privates[name] = v;
                return v;
            }
            default: // DIMENSION: reuse an existing variable in scope, else create a private one
            {
                var v = FindVariable(name);
                if (v == null)
                {
                    v = new Variable(name, Value.False);
                    _frame.Privates[name] = v;
                }
                return v;
            }
        }
    }

    public IEnumerable<Variable> VisibleVariables()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in _frame.Locals.Values) if (seen.Add(v.Name)) yield return v;
        for (var f = _frame; f != null; f = f.Parent)
            foreach (var v in f.Privates.Values) if (seen.Add(v.Name)) yield return v;
        foreach (var v in _publics.Values) if (seen.Add(v.Name)) yield return v;
    }

    private void Release(string name)
    {
        if (_frame.Locals.Remove(name, out var v) || _frame.Privates.Remove(name, out v) || RemoveFromChain(name, out v) || _publics.Remove(name, out v))
        {
            if (v != null && v.Value.Kind == ValueKind.Object && v.Value.AsObject is VfpObject o) ReleaseObject(o);
        }
    }

    private bool RemoveFromChain(string name, out Variable? v)
    {
        for (var f = _frame.Parent; f != null; f = f.Parent)
            if (f.Privates.Remove(name, out v)) return true;
        v = null;
        return false;
    }

    // ================================================================================
    // Programs, procedures and calls
    // ================================================================================

    internal sealed record Arg(Value Value, Variable? Ref);

    private void RegisterUnit(ProgramUnit unit)
    {
        if (!_loadedUnits.Contains(unit)) _loadedUnits.Add(unit);
        foreach (var c in unit.Classes.Values) _classes.Remove(c.Name);
    }

    public ProgramUnit LoadProgram(string path)
    {
        var resolved = ResolveProgramFile(path) ?? throw VfpException.FileNotFound(Path.GetFileName(path));
        var stamp = File.GetLastWriteTimeUtc(resolved);
        if (_programCache.TryGetValue(resolved, out var cached) && cached.Stamp == stamp) return cached.Unit;
        var unit = Parser.ParseProgram(File.ReadAllText(resolved), Path.GetFileNameWithoutExtension(resolved).ToUpperInvariant(), resolved,
            inc => ResolveProgramFile(inc, ".h") is { } p ? File.ReadAllText(p) : null);
        _programCache[resolved] = (stamp, unit);
        RegisterUnit(unit);
        return unit;
    }

    private string? ResolveProgramFile(string name, string ext = ".prg")
    {
        var p = Session.ResolvePath(name, ext);
        if (File.Exists(p)) return p;
        if (_frame.Unit?.File is { } current)
        {
            var q = DataSession.FindIgnoringCase(Path.Combine(Path.GetDirectoryName(current)!, Path.HasExtension(name) ? name : name + ext));
            if (q != null) return q;
        }
        return null;
    }

    private IEnumerable<ProgramUnit> SearchUnits()
    {
        var seen = new HashSet<ProgramUnit>();
        for (var f = _frame; f != null; f = f.Parent)
            if (f.Unit != null && seen.Add(f.Unit)) yield return f.Unit;
        foreach (var u in _procedureFiles) if (seen.Add(u)) yield return u;
        foreach (var u in _classLibraries) if (seen.Add(u)) yield return u;
        foreach (var u in StoredProcedureUnits()) if (seen.Add(u)) yield return u;
        foreach (var u in _loadedUnits.AsEnumerable().Reverse()) if (seen.Add(u)) yield return u;
    }

    private readonly Dictionary<string, ProgramUnit> _storedProcs = new(StringComparer.OrdinalIgnoreCase);

    private IEnumerable<ProgramUnit> StoredProcedureUnits()
    {
        foreach (var db in Session.OpenDatabases)
        {
            var code = db.StoredProcedures;
            var key = db.Path + "|" + code.GetHashCode();
            if (!_storedProcs.TryGetValue(key, out var unit))
            {
                unit = Parser.ParseProgram(code, db.Name, db.Path);
                _storedProcs[key] = unit;
            }
            yield return unit;
        }
    }

    internal (ProcedureDef Proc, ProgramUnit Unit)? FindProcedure(string name)
    {
        foreach (var u in SearchUnits())
            if (u.Procedures.TryGetValue(name, out var p)) return (p, u);
        return null;
    }

    private Value CallUnitMain(ProgramUnit unit, List<Arg> args)
    {
        var proc = new ProcedureDef(unit.Name, unit.MainParameters, unit.MainLocalParameters, unit.Main, 1);
        return Invoke(proc, unit, args, bindDeclared: false);
    }

    /// <summary>Invokes a procedure or method body in a new frame.</summary>
    internal Value Invoke(ProcedureDef proc, ProgramUnit? unit, List<Arg> args, bool bindDeclared = true,
        VfpObject? self = null, ClassInfo? methodClass = null)
    {
        if (_callDepth > 256) throw new VfpException(1490, "Nesting level is too deep.");
        var frame = new Frame(proc.Name.ToUpperInvariant(), _frame)
        {
            Unit = unit ?? _frame.Unit,
            This = self,
            MethodClass = methodClass,
            MethodName = methodClass != null ? proc.Name[(proc.Name.LastIndexOf('.') + 1)..] : null,
            ParameterCount = args.Count,
            ReturnValue = Value.True,
        };
        frame.Line = proc.Line;
        _pendingArgs = args;
        var saved = _frame;
        var savedSession = Session;
        if (self != null) Session = SessionFor(self);
        _frame = frame;
        _callDepth++;
        try
        {
            if (bindDeclared && proc.Parameters.Count > 0) BindParameters(proc.Parameters, local: proc.LocalParameters);
            Exec(proc.Body);
            return frame.ReturnValue;
        }
        finally
        {
            _callDepth--;
            _frame = saved;
            Session = savedSession;
            LastNoDefault = frame.NoDefault;
        }
    }

    /// <summary>True if the most recently finished method executed NODEFAULT (used by event dispatch).</summary>
    public bool LastNoDefault { get; private set; }

    private List<Arg> _pendingArgs = new();

    private void BindParameters(List<string> names, bool local)
    {
        var args = _pendingArgs;
        for (int i = 0; i < names.Count; i++)
        {
            Variable v;
            if (i < args.Count && args[i].Ref != null) v = args[i].Ref!;
            else v = new Variable(names[i], i < args.Count ? args[i].Value : Value.False);
            if (local) _frame.Locals[names[i]] = v;
            else _frame.Privates[names[i]] = v;
        }
        if (args.Count > names.Count && _frame.Parent != null && args.Count > 0)
            throw new VfpException(1230, "Too many arguments.");
    }

    private List<Arg> EvalArgs(List<Expr> exprs, bool byRefVariables)
    {
        var list = new List<Arg>(exprs.Count);
        foreach (var e in exprs)
        {
            switch (e)
            {
                case EmptyArgExpr:
                    list.Add(new Arg(Value.False, null));
                    break;
                case ByRefExpr br:
                    list.Add(new Arg(Value.False, FindVariable(br.Name) ?? throw VfpException.VariableNotFound(br.Name)));
                    break;
                case NameExpr n when byRefVariables && FindVariable(n.Name) is { } v && !IsField(n.Name):
                    list.Add(new Arg(v.Value, v));
                    break;
                case NameExpr n when FindVariable(n.Name) is { IsArray: true } av && !IsField(n.Name):
                    // Arrays are always passed by reference when passed by name.
                    list.Add(new Arg(av.Value, av));
                    break;
                default:
                    list.Add(new Arg(Eval(e), null));
                    break;
            }
        }
        return list;
    }

    private bool IsField(string name) => Session.Current.InUse && Session.Current.FieldIndex(name) >= 0;

    /// <summary>Calls a user-defined function or procedure by name, or returns null if none exists.</summary>
    internal Value? TryCallUserFunction(string name, List<Arg> args)
    {
        var found = FindProcedure(name);
        if (found != null) return Invoke(found.Value.Proc, found.Value.Unit, args);
        if (ResolveProgramFile(name) is { } file)
        {
            var unit = LoadProgram(file);
            return CallUnitMain(unit, args);
        }
        return null;
    }

    // ================================================================================
    // Statements
    // ================================================================================

    // The statement lists being executed, innermost last, for the debugger's Set Next Statement.
    private readonly List<(List<Stmt> Stmts, Frame Frame)> _activeLists = new();

    /// <summary>Lines the debugger may move execution to: statements in the blocks now executing in <paramref name="frame"/>.</summary>
    internal IReadOnlyList<int> GotoTargets(Frame frame) =>
        _activeLists.Where(a => a.Frame == frame).SelectMany(a => a.Stmts).Select(s => s.Line).Where(l => l > 0).Distinct().Order().ToList();

    internal Flow Exec(List<Stmt> stmts)
    {
        _activeLists.Add((stmts, _frame));
        try
        {
            int i = 0;
            while (i < stmts.Count)
            {
                var s = stmts[i];
                _frame.Line = s.Line;
                if (Debugger != null)
                {
                    Debugger.OnStatement(_frame, s);
                    if (Debugger.TakeGoto() is int target)
                    {
                        // Set Next Statement: jump within this block, or unwind to the enclosing block that has the line.
                        var idx = stmts.FindIndex(x => x.Line == target);
                        if (idx < 0) throw new GotoException(target, _frame);
                        i = idx;
                        continue;
                    }
                }
                Flow f;
                try
                {
                    f = ExecStmt(s);
                }
                catch (GotoException g) when (g.Frame == _frame && stmts.FindIndex(x => x.Line == g.Line) is var at && at >= 0)
                {
                    i = at;
                    Debugger?.StopAfterGoto();
                    continue;
                }
                catch (VfpException ex) when (Annotate(ex) && ReportToDebugger(ex)) { throw; }
                catch (VfpException ex) when (_tryDepth == 0 && OnErrorCommand != null)
                {
                    RecordError(ex);
                    RunOnError();
                    i++;
                    continue;
                }
                catch (Exception ex) when (ex is not VfpException and not QuitException and not ReturnToMasterException and not InternalFlowException and not CancelProgramException and not GotoException)
                {
                    var vex = Wrap(ex);
                    if (_tryDepth == 0 && OnErrorCommand != null)
                    {
                        RecordError(vex);
                        RunOnError();
                        i++;
                        continue;
                    }
                    throw vex;
                }
                if (f != Flow.Normal) return f;
                i++;
            }
            return Flow.Normal;
        }
        finally
        {
            _activeLists.RemoveAt(_activeLists.Count - 1);
        }
    }

    private readonly HashSet<VfpException> _reportedToDebugger = new(ReferenceEqualityComparer.Instance);

    /// <summary>Exception filter: lets the debugger stop at an unhandled error before the stack unwinds. Always returns false.</summary>
    private bool ReportToDebugger(VfpException ex)
    {
        if (Debugger is { BreakOnErrors: true } d && _tryDepth == 0 && OnErrorCommand == null && _reportedToDebugger.Add(ex))
            d.OnUnhandledError(_frame, ex);
        return false;
    }

    private bool Annotate(VfpException ex)
    {
        ex.ErrorLine ??= ex is CompileException ce && ce.Line > 0 ? ce.Line : _frame.Line;
        ex.ErrorProgram ??= _frame.Program;
        return true;
    }

    internal static VfpException Wrap(Exception ex) => ex switch
    {
        VfpException v => v,
        DivideByZeroException => new VfpException(1307, "Division by zero."),
        OverflowException => new VfpException(1988, "Numeric overflow. Data was lost."),
        IOException io => new VfpException(ErrorCodes.FileAccessDenied, io.Message),
        UnauthorizedAccessException ua => new VfpException(ErrorCodes.FileAccessDenied, ua.Message),
        _ => new VfpException(ErrorCodes.FeatureNotAvailable, "Internal error: " + ex.Message),
    };

    /// <summary>Records an error for AERROR() without raising it (SQL pass-through functions return -1 instead).</summary>
    internal void RecordError(VfpException ex)
    {
        LastErrorNumber = ex.Number;
        LastErrorMessage = ex.Message;
        LastErrorDetail = ex.Data.Contains("OdbcMessage")
            ? [Value.String((string)ex.Data["OdbcMessage"]!), ex.Data["SqlState"] is string st ? Value.String(st) : Value.Null,
               Value.Number(Convert.ToDouble(ex.Data["NativeError"] ?? 0)), ex.Data["Handle"] is int h ? Value.Number(h) : Value.Null, Value.Null]
            : null;
        LastErrorLine = ex.ErrorLine ?? _frame.Line;
        LastErrorProgram = ex.ErrorProgram ?? _frame.Program;
    }

    private void RunOnError()
    {
        var cmd = OnErrorCommand!;
        OnErrorCommand = null; // prevent recursion while the handler runs
        try
        {
            var unit = Parser.ParseInteractive(cmd);
            Exec(unit.Main);
        }
        finally
        {
            OnErrorCommand ??= cmd;
        }
    }

    private Flow ExecStmt(Stmt s)
    {
        switch (s)
        {
            case AssignStmt a:
                Assign(a.Target, Eval(a.Value));
                return Flow.Normal;
            case StoreStmt st:
            {
                var v = Eval(st.Value);
                foreach (var t in st.Targets) Assign(t, v);
                return Flow.Normal;
            }
            case ExprStmt e:
                Eval(e.Expr);
                return Flow.Normal;
            case PrintStmt p:
                Print(p);
                return Flow.Normal;
            case DeclareStmt d:
                foreach (var v0 in d.Vars)
                {
                    var v = v0.NameExpr != null ? v0 with { Name = Eval(v0.NameExpr).AsString.Trim() } : v0;
                    if (v.Target != null)
                    {
                        if (Eval(v.Target.Target) is not { Kind: ValueKind.Object } owner) throw VfpException.TypeMismatch();
                        var obj = (VfpObject)owner.AsObject;
                        var prop = obj.FindProperty(v.Name);
                        if (prop == null) { obj.Set(v.Name, Value.False); prop = obj.FindProperty(v.Name)!; }
                        var pd = v.Dims!.Select(x => (int)Eval(x).AsNumber).ToList();
                        if (prop.Array != null) prop.Array.Redimension(pd[0], pd.Count > 1 ? pd[1] : 0);
                        else prop.Array = new VfpArray(pd[0], pd.Count > 1 ? pd[1] : 0);
                        continue;
                    }
                    var variable = Declare(d.Scope, v.Name);
                    if (v.Dims != null)
                    {
                        var dims = v.Dims.Select(x => (int)Eval(x).AsNumber).ToList();
                        int rows = dims[0], cols = dims.Count > 1 ? dims[1] : 0;
                        if (variable.Array != null && d.Scope == "DIMENSION") variable.Array.Redimension(rows, cols);
                        else variable.Array = new VfpArray(rows, cols);
                    }
                    else if (d.Scope == "DIMENSION")
                    {
                        throw VfpException.Syntax("DIMENSION requires subscripts.");
                    }
                }
                return Flow.Normal;
            case PrivateAllStmt:
                return Flow.Normal;
            case IfStmt i:
            {
                var c = Eval(i.Cond);
                if (Truthy(c)) return Exec(i.Then);
                return i.Else != null ? Exec(i.Else) : Flow.Normal;
            }
            case DoCaseStmt dc:
                foreach (var c in dc.Cases)
                    if (Truthy(Eval(c.Cond))) return Exec(c.Body);
                return dc.Otherwise != null ? Exec(dc.Otherwise) : Flow.Normal;
            case DoWhileStmt w:
                while (Truthy(Eval(w.Cond)))
                {
                    var f = Exec(w.Body);
                    if (f == Flow.Exit) break;
                    if (f == Flow.Return) return f;
                }
                return Flow.Normal;
            case ForStmt fs:
                return ExecFor(fs);
            case ForEachStmt fe:
                return ExecForEach(fe);
            case ScanStmt sc:
                return ExecScan(sc);
            case ExitStmt:
                return Flow.Exit;
            case LoopStmt:
                return Flow.Loop;
            case ReturnStmt r:
                if (r.ToMaster) throw new ReturnToMasterException();
                _frame.ReturnValue = r.Value != null ? Eval(r.Value) : Value.True;
                return Flow.Return;
            case ParametersStmt ps:
                BindParameters(ps.Names, ps.Local);
                return Flow.Normal;
            case DoStmt d:
                ExecDo(d);
                return Flow.Normal;
            case TryStmt t:
                return ExecTry(t);
            case ThrowStmt th:
                throw new UserThrowException(th.Value != null ? Eval(th.Value) : Value.Null, _frame.Line);
            case ErrorStmt es:
                ExecError(es);
                return Flow.Normal;
            case WithStmt w:
            {
                var target = Eval(w.Target);
                _frame.WithStack.Push(target.Kind == ValueKind.Object ? target.AsObject : null);
                try { return Exec(w.Body); }
                finally { _frame.WithStack.Pop(); }
            }
            case TextStmt tx:
                ExecText(tx);
                return Flow.Normal;
            case TextOutStmt tout:
            {
                var text = TextMergeOn ? TextMerge(tout.Text) : tout.Text;
                if (_textMergeFile != null) File.AppendAllText(_textMergeFile, (tout.NewLine ? "\r\n" : "") + text);
                if (_textMergeShow)
                {
                    if (tout.NewLine) Output.NewLine();
                    Output.Write(text);
                }
                return Flow.Normal;
            }
            case ReleaseStmt rl:
                ExecRelease(rl);
                return Flow.Normal;
            case OnErrorStmt oe:
                OnErrorCommand = string.IsNullOrWhiteSpace(oe.Command) ? null : oe.Command;
                return Flow.Normal;
            case MacroStmt m:
            {
                var text = ExpandMacros(m.Text);
                var unit = Parser.ParseInteractive(text);
                foreach (var p in unit.Procedures) _frame.Unit?.Procedures.TryAdd(p.Key, p.Value);
                return Exec(unit.Main);
            }
            case NoOpStmt { Verb: "SUSPEND" }:
                if (Debugger != null) Debugger.Suspend(_frame, s.Line);
                else Notify("SUSPEND: no debugger is attached; continuing.");
                return Flow.Normal;
            case NoOpStmt { Verb: "RESUME" }:
                return Flow.Normal;
            case NoOpStmt n:
                Notify($"{n.Verb} is not supported yet; statement skipped.");
                return Flow.Normal;
            case QuitStmt { Cancel: true }:
                throw new CancelProgramException();
            case QuitStmt q:
                throw new QuitException(q.Cancel);
            case ReadEventsStmt re:
                if (re.Clear) Ui?.ClearEvents();
                else if (Ui != null) Ui.ReadEvents();
                else Notify("READ EVENTS: no UI runtime is attached; continuing.");
                return Flow.Normal;
            case DoFormStmt df:
                ExecDoForm(df);
                return Flow.Normal;
            case ClearStmt cl:
                ExecClear(cl);
                return Flow.Normal;
            case WaitStmt wt:
                ExecWait(wt);
                return Flow.Normal;
            case DefineClassStmt:
                return Flow.Normal;
            case SetProcedureStmt sp:
                if (!sp.Additive) _procedureFiles.Clear();
                foreach (var f in sp.Files) _procedureFiles.Add(LoadProgram(NameValue(f)));
                return Flow.Normal;
            case CompileStmt c:
                LoadProgram(NameValue(c.File));
                return Flow.Normal;
            case ChdirStmt cd:
            {
                var path = NameValue(cd.Path);
                var full = Path.GetFullPath(Path.Combine(Options.Default_, path));
                if (!Directory.Exists(full)) throw new VfpException(202, "Invalid path or file name.", path);
                Options.Default_ = full;
                return Flow.Normal;
            }
            default:
                return ExecDataStmt(s) ? Flow.Normal : throw VfpException.NotSupported(s.GetType().Name.Replace("Stmt", "").ToUpperInvariant());
        }
    }

    internal static bool Truthy(Value v) => v.Kind switch
    {
        ValueKind.Logical => v.AsBool,
        ValueKind.Null => false,
        _ => throw VfpException.TypeMismatch(),
    };

    private void Print(PrintStmt p)
    {
        if (p.NewLine && Output.Column > 0) Output.NewLine();
        else if (p.NewLine && _printedSomething) Output.NewLine();
        for (int i = 0; i < p.Items.Count; i++)
        {
            if (i > 0) Output.Write(" ");
            Output.Write(Formatter.ToDisplay(Eval(p.Items[i]), Options));
        }
        _printedSomething = true;
    }

    private bool _printedSomething;

    /// <summary>Writes a full line of output (used by LIST, DISPLAY and friends).</summary>
    internal void WriteLine(string text)
    {
        if (Output.Column > 0 || _printedSomething) Output.NewLine();
        Output.Write(text);
        _printedSomething = true;
    }

    private Flow ExecFor(ForStmt fs)
    {
        var from = Eval(fs.From);
        var to = Eval(fs.To).AsNumber;
        var step = fs.Step != null ? Eval(fs.Step).AsNumber : 1;
        Assign(new NameExpr(fs.Var), from);
        while (true)
        {
            var cur = GetVariable(fs.Var).AsNumber;
            if (step >= 0 ? cur > to : cur < to) break;
            var f = Exec(fs.Body);
            if (f == Flow.Exit) break;
            if (f == Flow.Return) return f;
            var now = GetVariable(fs.Var);
            Assign(new NameExpr(fs.Var), Value.Number(now.AsNumber + step, Math.Max(now.Decimals, 0)));
        }
        return Flow.Normal;
    }

    private Flow ExecForEach(ForEachStmt fe)
    {
        IEnumerable<Value> items;
        if (fe.Collection is NameExpr n && FindVariable(n.Name) is { IsArray: true } av)
            items = av.Array!.Raw.ToList();
        else
        {
            var c = Eval(fe.Collection);
            if (c.Kind != ValueKind.Object) throw VfpException.TypeMismatch();
            var o = (VfpObject)c.AsObject;
            if (o.Items != null) items = o.Items.Select(i => i.Value).ToList();
            else if (o.Class.BaseClass is "Form" or "Container" or "Page" or "PageFrame" or "Grid" or "FormSet" or "CommandGroup" or "OptionGroup")
                items = o.Members.Select(m => Value.Object(m)).ToList();
            else throw new VfpException(1924, "Object is not a collection.");
        }
        foreach (var item in items)
        {
            Assign(new NameExpr(fe.Var), item);
            var f = Exec(fe.Body);
            if (f == Flow.Exit) break;
            if (f == Flow.Return) return f;
        }
        return Flow.Normal;
    }

    private Flow ExecTry(TryStmt t)
    {
        Flow result = Flow.Normal;
        try
        {
            _tryDepth++;
            try { result = Exec(t.Body); }
            finally { _tryDepth--; }
        }
        catch (Exception ex) when (t.Catch != null && ex is not QuitException and not ReturnToMasterException and not InternalFlowException and not CancelProgramException and not GotoException)
        {
            List<Stmt>? handler = null;
            if (CatchMatches(t.CatchVar, t.When, ex)) handler = t.Catch;
            else foreach (var c in t.MoreCatches) if (CatchMatches(c.Var, c.When, ex)) { handler = c.Body; break; }
            if (handler == null) throw;
            result = Exec(handler);
        }
        finally
        {
            if (t.Finally != null)
            {
                var f = Exec(t.Finally);
                if (f != Flow.Normal) result = f;
            }
        }
        return result;
    }

    private bool CatchMatches(string? catchVar, Expr? when, Exception ex)
    {
        Value exValue;
        if (ex is UserThrowException ut && ut.Payload.Kind == ValueKind.Object && ut.Payload.AsObject is VfpObject thrownObj && thrownObj.Class.IsA("Exception"))
            exValue = ut.Payload;
        else
        {
            var vex = ex is UserThrowException u
                ? new VfpException(2071, "User Thrown Error.")
                : Wrap(ex);
            RecordError(vex);
            var o = CreateObjectByName("Exception", []);
            o.Set("ErrorNo", Value.Number(vex.Number));
            o.Set("Message", Value.String(vex.Message));
            o.Set("LineNo", Value.Number(ex is UserThrowException ux ? ux.Line : LastErrorLine));
            o.Set("Procedure", Value.String(LastErrorProgram));
            o.Set("Details", Value.String(vex.Detail ?? ""));
            o.Set("LineContents", Value.EmptyString);
            o.Set("StackLevel", Value.Number(_frame.Level));
            if (ex is UserThrowException u2) o.Set("UserValue", u2.Payload);
            exValue = Value.Object(o);
        }
        if (catchVar != null) Assign(new NameExpr(catchVar), exValue);
        if (when != null && !Truthy(Eval(when))) return false;
        return true;
    }

    private void ExecError(ErrorStmt es)
    {
        if (es.Args.Count == 0) throw new VfpException(ErrorCodes.UserDefined, "User-defined error.");
        var first = Eval(es.Args[0]);
        if (first.Kind == ValueKind.Character) throw new VfpException(ErrorCodes.UserDefined, first.AsString);
        var n = (int)first.AsNumber;
        var detail = es.Args.Count > 1 ? Formatter.ToDisplay(Eval(es.Args[1]), Options).Trim() : null;
        throw new VfpException(n, detail != null ? $"Error {n}: {detail}" : $"Error {n}.", detail);
    }

    private void ExecText(TextStmt t)
    {
        var lines = t.Lines.Select(l => t.Merge ? TextMerge(l) : l).ToList();
        var text = string.Join("\r\n", lines);
        if (t.ToTarget != null)
        {
            if (t.Additive && Eval(t.ToTarget) is { Kind: ValueKind.Character } old) text = old.AsString + "\r\n" + text;
            Assign(t.ToTarget, Value.String(text));
            return;
        }
        if (t.ToVar != null)
        {
            if (t.Additive && FindVariable(t.ToVar) is { } v && v.Value.Kind == ValueKind.Character)
                text = v.Value.AsString + "\r\n" + text;
            SetVariable(t.ToVar, Value.String(text));
            if (t.NoShow) return;
            if (t.ToVar != null) return;
        }
        foreach (var l in lines) WriteLine(l);
    }

    /// <summary>Replaces &lt;&lt;expr&gt;&gt; with the text of each evaluated expression.</summary>
    internal string TextMerge(string text) =>
        Regex.Replace(text, "<<(.+?)>>", m =>
        {
            var v = Evaluate(m.Groups[1].Value);
            return v.Kind == ValueKind.Character ? v.AsString : Builtins.Library.TransformDefault(v, Options);
        });

    private void ExecRelease(ReleaseStmt r)
    {
        foreach (var m in r.Members) Assign(m, Value.Null);
        if (r.All)
        {
            var names = _frame.Locals.Keys.Concat(_frame.Privates.Keys).ToList();
            if (_frame.Parent == null) names.AddRange(_publics.Keys.Where(k => !k.StartsWith('_')));
            foreach (var n in names)
            {
                if (r.Like != null)
                {
                    var match = Builtins.Library.LikeMatch(r.Like.ToUpperInvariant(), n.ToUpperInvariant());
                    if (match == r.Except) continue;
                }
                Release(n);
            }
            return;
        }
        foreach (var n in r.Names) Release(n);
    }

    private void ExecClear(ClearStmt c)
    {
        switch (c.What)
        {
            case null:
                (Output as IClearableOutput)?.Clear();
                _printedSomething = false;
                break;
            case "ALL" or "MEMORY":
                foreach (var n in _frame.Privates.Keys.ToList()) Release(n);
                foreach (var n in _publics.Keys.Where(k => !k.StartsWith('_')).ToList()) Release(n);
                if (c.What == "ALL") { _classes.Clear(); _programCache.Clear(); }
                break;
            case "PROGRAM" or "CLASS" or "CLASSLIB":
                _programCache.Clear();
                _classes.Clear();
                break;
        }
    }

    private void ExecWait(WaitStmt w)
    {
        var msg = w.Message != null ? Formatter.ToDisplay(Eval(w.Message), Options) : (w.Clear ? null : "Press any key to continue...");
        if (msg != null) Notify(msg);
        if (w.ToVar != null) SetVariable(w.ToVar, Value.EmptyString);
    }

    private void ExecDo(DoStmt d)
    {
        var name = NameValue(d.Target);
        var args = EvalArgs(d.Args, byRefVariables: true);
        if (d.InFile != null)
        {
            var unit = LoadProgram(d.InFile);
            if (unit.Procedures.TryGetValue(name, out var p)) { Invoke(p, unit, args); return; }
            throw new VfpException(1, $"Procedure '{name}' is not found in {d.InFile}.");
        }
        var stem = Path.GetFileNameWithoutExtension(name);
        if (!name.Contains('.') && !name.Contains('\\') && !name.Contains('/') && FindProcedure(stem) is { } found)
        {
            Invoke(found.Proc, found.Unit, args);
            return;
        }
        var file = ResolveProgramFile(name) ?? throw VfpException.FileNotFound(Path.HasExtension(name) ? name : name + ".prg");
        CallUnitMain(LoadProgram(file), args);
    }

    /// <summary>
    /// DO FORM name: loads name.jpform (a form stored as DEFINE CLASS text) or a legacy name.scx (converted in
    /// memory), instantiates its form class and shows it.
    /// </summary>
    private void ExecDoForm(DoFormStmt df)
    {
        var name = NameValue(df.Form);
        var path = ResolveClassFile(name, ".jpform", ".scx", _frame.Unit?.File)
            ?? throw VfpException.FileNotFound(Path.HasExtension(name) ? name : name + ".jpform");
        var unit = LoadClassFile(path);
        var def = unit.Classes.Values.FirstOrDefault(c => ResolveClass(c.Name, unit).BaseClass is "Form" or "FormSet")
                  ?? throw new VfpException(1733, $"{Path.GetFileName(path)} does not define a Form class.");
        var args = EvalArgs(df.Args, byRefVariables: false);
        var form = CreateObject(ResolveClass(def.Name, unit), args);
        if (form == null) return; // Load or Init returned .F.
        var varName = df.NameVar ?? Path.GetFileNameWithoutExtension(path);
        if (df.NameVar != null || !df.Linked) SetVariable(varName, Value.Object(form));
        if (df.NoShow) return;
        var modal = df.ToVar != null || (form.FindProperty("WindowType")?.Value is { Kind: ValueKind.Number } wt && wt.AsNumber == 1);
        if (Ui == null)
        {
            Notify($"DO FORM {Path.GetFileName(path)}: no UI runtime is attached; the form object was created but not shown.");
            return;
        }
        form.Set("Visible", Value.True);
        Ui.Show(form, modal);
        if (df.ToVar != null)
            SetVariable(df.ToVar, UnloadResults.TryGetValue(form, out var r) ? r : Value.True);
    }

    // ================================================================================
    // Macros and name arguments
    // ================================================================================

    /// <summary>Textual &amp;macro substitution, as VFP does before compiling a line.</summary>
    internal string ExpandMacros(string text) =>
        Regex.Replace(text, @"&([A-Za-z_][A-Za-z0-9_]*)\.?", m =>
        {
            var v = GetVariable(m.Groups[1].Value);
            if (v.Kind != ValueKind.Character) throw VfpException.TypeMismatch();
            return v.AsString;
        });

    /// <summary>Evaluates a "name argument" (file, table, alias or tag name) to text.</summary>
    internal string NameValue(Expr e) => e switch
    {
        LiteralExpr { Value.Kind: ValueKind.Character } l => ExpandMacros(l.Value.AsString).Trim(),
        MacroExpr m => GetVariable(m.VarName) is { Kind: ValueKind.Character } s ? s.AsString.Trim() : throw VfpException.TypeMismatch(),
        NameExpr n => n.Name,
        _ => Eval(e) is { Kind: ValueKind.Character } v ? v.AsString.Trim() : throw VfpException.TypeMismatch(),
    };

    // ================================================================================
    // IExpressionHost (index keys, filters, rules)
    // ================================================================================

    public ICompiledExpression Compile(string expression) => new CompiledExpression(this, expression, Parser.ParseExpression(expression));

    private sealed class CompiledExpression(Interpreter rt, string source, Expr expr) : ICompiledExpression
    {
        public string Source => source;

        public Value Evaluate(IRecord record)
        {
            rt._recordContext.Push(record);
            try { return rt.Eval(expr); }
            finally { rt._recordContext.Pop(); }
        }
    }

    internal IRecord? RecordContext => _recordContext.Count > 0 ? _recordContext.Peek() : null;
}

/// <summary>THROW with a user value.</summary>
public sealed class UserThrowException(Value payload, int line) : VfpException(2071, "User Thrown Error.")
{
    public Value Payload { get; } = payload;
    public int Line { get; } = line;
}

internal sealed class InternalFlowException : Exception;

/// <summary>Set Next Statement to a line in an enclosing block: unwinds to the Exec loop that owns the line.</summary>
internal sealed class GotoException(int line, Frame frame) : Exception
{
    public int Line { get; } = line;
    public Frame Frame { get; } = frame;
}
