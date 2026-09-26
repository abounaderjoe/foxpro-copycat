using System.Diagnostics;
using JoePro.Core;
using JoePro.Language;

namespace JoePro.Runtime;

public enum BreakpointKind
{
    /// <summary>Break at a line (optionally only when <see cref="Breakpoint.Condition"/> is true).</summary>
    Line,
    /// <summary>Break at any statement where the expression is true (VFP "break when expression is true").</summary>
    WhenTrue,
    /// <summary>Break when the expression's value changes (VFP "break when expression has changed").</summary>
    OnChange,
}

public sealed class Breakpoint
{
    private static int _nextId;

    public int Id { get; } = Interlocked.Increment(ref _nextId);
    public BreakpointKind Kind { get; init; } = BreakpointKind.Line;
    /// <summary>Full path of the program file, or a program/procedure name for code without a file.</summary>
    public string? File { get; init; }
    public int Line { get; init; }
    public string? Condition { get; init; }
    public string? Expression { get; init; }
    /// <summary>Break only on this hit (0 = every hit).</summary>
    public int HitCount { get; init; }
    public bool Enabled { get; set; } = true;
    public int Hits { get; internal set; }
    internal Value? LastValue { get; set; }
    internal bool Primed { get; set; }
}

public enum StopReason { Breakpoint, Step, Pause, Suspend, Exception, Entry }

public enum DebugAction { Continue, StepInto, StepOver, StepOut, Cancel }

/// <summary>One activation in the call stack, as shown by the debugger (innermost first).</summary>
public sealed record StackFrameInfo(int Id, string Name, string? File, int Line, int Level);

/// <summary>A variable, property or array element shown in the Locals/Watch windows.</summary>
public sealed record VariableInfo(string Name, string Value, string Type, string Scope, int ChildrenRef);

/// <summary>
/// The state at a stop. Its methods may only be called while the debugger is paused, on the thread
/// that runs the program (the host's Paused callback runs on that thread).
/// </summary>
public sealed class DebugStop
{
    private readonly Debugger _debugger;
    private readonly List<Frame> _frames;

    internal DebugStop(Debugger debugger, StopReason reason, List<Frame> frames, Breakpoint? breakpoint, string? message)
    {
        _debugger = debugger;
        Reason = reason;
        _frames = frames;
        Breakpoint = breakpoint;
        Message = message;
        Frames = frames.Select((f, i) => new StackFrameInfo(i, f.Program, FileOf(f), f.Line, f.Level)).ToList();
    }

    public StopReason Reason { get; }
    public Breakpoint? Breakpoint { get; }
    public string? Message { get; }
    public IReadOnlyList<StackFrameInfo> Frames { get; }
    public StackFrameInfo Top => Frames[0];

    internal static string? FileOf(Frame f) => f.Unit?.File;

    /// <summary>Variables visible in a frame: its LOCALs and PRIVATEs (and PUBLICs for the outermost frame).</summary>
    public IReadOnlyList<VariableInfo> Locals(int frameId)
    {
        var f = _frames[frameId];
        var list = new List<VariableInfo>();
        foreach (var v in f.Locals.Values) list.Add(_debugger.Describe(v.Name, v, "Local"));
        foreach (var v in f.Privates.Values) list.Add(_debugger.Describe(v.Name, v, "Private"));
        if (f.This != null) list.Insert(0, _debugger.Describe("This", new Variable("This", Value.Object(f.This)), "Object"));
        return list.OrderBy(v => v.Scope == "Object" ? 0 : 1).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<VariableInfo> Publics() =>
        _debugger.Runtime.PublicVariables().Select(v => _debugger.Describe(v.Name, v, "Public")).OrderBy(v => v.Name).ToList();

    /// <summary>Children of an array or object returned by <see cref="VariableInfo.ChildrenRef"/>.</summary>
    public IReadOnlyList<VariableInfo> Children(int reference) => _debugger.ChildrenOf(reference);

    /// <summary>Evaluates an expression in the context of a frame (Watch window, hover, debug console).</summary>
    public VariableInfo Evaluate(string expression, int frameId = 0)
    {
        try
        {
            var v = _debugger.Runtime.EvaluateInFrame(_frames[frameId], expression);
            return _debugger.Describe(expression, new Variable(expression, v), "Watch");
        }
        catch (VfpException ex)
        {
            return new VariableInfo(expression, $"Error {ex.Number}: {ex.Message}", "U", "Watch", 0);
        }
    }

    /// <summary>Changes a variable while paused (Locals window editing).</summary>
    public void SetVariable(int frameId, string name, string expression) =>
        _debugger.Runtime.AssignInFrame(_frames[frameId], name, expression);
}

/// <summary>Implemented by debugger front ends (the IDE, the DAP server).</summary>
public interface IDebugHost
{
    /// <summary>Called on the program thread when execution stops. Blocks until the user chooses how to resume.</summary>
    DebugAction Paused(DebugStop stop);

    /// <summary>DEBUGOUT text and event-tracking messages.</summary>
    void Output(string text);
}

/// <summary>
/// The Joe Pro debugger engine: breakpoints, stepping, call stack and variable inspection, event tracking
/// and coverage logging. The runtime calls <see cref="OnStatement"/> before each statement.
/// </summary>
public sealed class Debugger
{
    private readonly Dictionary<int, object> _handles = new();
    private int _nextHandle = 1;
    private DebugAction _mode = DebugAction.Continue;
    private int _stepLevel;
    private Frame? _stepFrame;
    private int _stepLine;
    private volatile bool _pauseRequested;
    private StreamWriter? _coverage;
    private readonly Stopwatch _clock = new();
    private (string? File, int Line, int Level, string Program)? _lastCoverage;

    public Debugger(Interpreter runtime)
    {
        Runtime = runtime;
        runtime.Debugger = this;
    }

    public Interpreter Runtime { get; }
    public IDebugHost? Host { get; set; }
    public List<Breakpoint> Breakpoints { get; } = new();
    public bool IsPaused { get; private set; }
    /// <summary>SET EVENTTRACKING ON: write every event to the debug output.</summary>
    public bool EventTracking { get; set; }
    public string? CoverageFile { get; private set; }

    // ---- Breakpoints -------------------------------------------------------------------

    public Breakpoint AddLineBreakpoint(string file, int line, string? condition = null)
    {
        var bp = new Breakpoint { File = Path.GetFullPath(file), Line = line, Condition = string.IsNullOrWhiteSpace(condition) ? null : condition };
        Breakpoints.Add(bp);
        return bp;
    }

    public void SetLineBreakpoints(string file, IEnumerable<(int Line, string? Condition)> lines)
    {
        var full = Path.GetFullPath(file);
        Breakpoints.RemoveAll(b => b.Kind == BreakpointKind.Line && SameFile(b.File, full));
        foreach (var (line, cond) in lines) AddLineBreakpoint(full, line, cond);
    }

    public Breakpoint AddExpressionBreakpoint(string expression, bool onChange)
    {
        var bp = new Breakpoint { Kind = onChange ? BreakpointKind.OnChange : BreakpointKind.WhenTrue, Expression = expression };
        Breakpoints.Add(bp);
        return bp;
    }

    public bool ToggleLineBreakpoint(string file, int line)
    {
        var full = Path.GetFullPath(file);
        var existing = Breakpoints.FirstOrDefault(b => b.Kind == BreakpointKind.Line && b.Line == line && SameFile(b.File, full));
        if (existing != null) { Breakpoints.Remove(existing); return false; }
        AddLineBreakpoint(full, line);
        return true;
    }

    private static bool SameFile(string? a, string? b) =>
        a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Asks the running program to stop at the next statement (Pause button, ESC).</summary>
    public void RequestPause() => _pauseRequested = true;

    // ---- Coverage ----------------------------------------------------------------------

    /// <summary>SET COVERAGE TO file: logs every executed line (VFP-style comma-separated format).</summary>
    public void StartCoverage(string file, bool additive)
    {
        StopCoverage();
        CoverageFile = file;
        _coverage = new StreamWriter(file, additive);
        _clock.Restart();
    }

    public void StopCoverage()
    {
        FlushCoverageLine();
        _coverage?.Dispose();
        _coverage = null;
        CoverageFile = null;
    }

    private void FlushCoverageLine()
    {
        if (_coverage == null || _lastCoverage is not { } c) return;
        // time,class,procedure,line,file,stack level
        _coverage.WriteLine(string.Join(",", (_clock.Elapsed.TotalMilliseconds / 1000).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture),
            "", c.Program, c.Line, c.File ?? "", c.Level));
        _clock.Restart();
        _lastCoverage = null;
    }

    // ---- Runtime hooks -------------------------------------------------------------------

    internal void OnStatement(Frame frame, Stmt stmt)
    {
        if (IsPaused) return; // statements run by Evaluate while paused never stop
        if (_coverage != null)
        {
            FlushCoverageLine();
            _lastCoverage = (frame.Unit?.File, stmt.Line, frame.Level, frame.Program);
        }
        if (Host == null) return;

        StopReason? reason = null;
        Breakpoint? hit = null;
        if (_pauseRequested)
        {
            _pauseRequested = false;
            reason = StopReason.Pause;
        }
        else if (_mode != DebugAction.Continue && StepShouldStop(frame, stmt))
        {
            reason = StopReason.Step;
        }
        else if (Breakpoints.Count > 0)
        {
            hit = MatchBreakpoint(frame, stmt);
            if (hit != null) reason = StopReason.Breakpoint;
        }
        if (reason != null) Stop(frame, stmt.Line, reason.Value, hit, null);
    }

    private bool StepShouldStop(Frame frame, Stmt stmt)
    {
        // A statement on the same line in the same frame (e.g. the body of a one-line IF) is not a new step.
        if (frame == _stepFrame && stmt.Line == _stepLine) return false;
        return _mode switch
        {
            DebugAction.StepInto => true,
            DebugAction.StepOver => frame.Level <= _stepLevel,
            DebugAction.StepOut => frame.Level < _stepLevel,
            _ => false,
        };
    }

    private Breakpoint? MatchBreakpoint(Frame frame, Stmt stmt)
    {
        var file = frame.Unit?.File;
        foreach (var bp in Breakpoints)
        {
            if (!bp.Enabled) continue;
            switch (bp.Kind)
            {
                case BreakpointKind.Line:
                    if (bp.Line != stmt.Line) continue;
                    if (!(SameFile(bp.File, file) || (file == null && string.Equals(bp.File, frame.Program, StringComparison.OrdinalIgnoreCase)))) continue;
                    if (bp.Condition != null && !IsTrue(bp.Condition, frame)) continue;
                    bp.Hits++;
                    if (bp.HitCount > 0 && bp.Hits != bp.HitCount) continue;
                    return bp;
                case BreakpointKind.WhenTrue:
                    if (IsTrue(bp.Expression!, frame)) { bp.Hits++; return bp; }
                    continue;
                case BreakpointKind.OnChange:
                {
                    Value current;
                    try { current = Runtime.EvaluateInFrame(frame, bp.Expression!); }
                    catch (VfpException) { continue; }
                    if (!bp.Primed) { bp.Primed = true; bp.LastValue = current; continue; }
                    if (bp.LastValue is { } last && last.Equals(current)) continue;
                    bp.LastValue = current;
                    bp.Hits++;
                    return bp;
                }
            }
        }
        return null;
    }

    private bool IsTrue(string expr, Frame frame)
    {
        try { return Runtime.EvaluateInFrame(frame, expr) is { Kind: ValueKind.Logical } v && v.AsBool; }
        catch (VfpException) { return false; }
    }

    /// <summary>SUSPEND / SET STEP ON: stop immediately (the next statement is shown).</summary>
    internal void Suspend(Frame frame, int line)
    {
        if (Host == null) return;
        Stop(frame, line, StopReason.Suspend, null, null);
    }

    /// <summary>Breaks when an error is not handled by TRY/CATCH or ON ERROR (if the host wants it).</summary>
    internal void OnUnhandledError(Frame frame, VfpException ex)
    {
        if (Host == null || !BreakOnErrors) return;
        Stop(frame, ex.ErrorLine ?? frame.Line, StopReason.Exception, null, $"Error {ex.Number}: {ex.Message}");
    }

    public bool BreakOnErrors { get; set; }

    private void Stop(Frame frame, int line, StopReason reason, Breakpoint? bp, string? message)
    {
        frame.Line = line;
        var frames = new List<Frame>();
        for (var f = frame; f != null; f = f.Parent) frames.Add(f);
        _handles.Clear();
        _nextHandle = 1;
        IsPaused = true;
        DebugAction action;
        try
        {
            action = Host!.Paused(new DebugStop(this, reason, frames, bp, message));
        }
        finally
        {
            IsPaused = false;
        }
        _mode = action;
        _stepLevel = frame.Level;
        _stepFrame = frame;
        _stepLine = line;
        // Watch-change breakpoints re-prime after a stop so values changed while paused don't fire.
        foreach (var b in Breakpoints.Where(b => b.Kind == BreakpointKind.OnChange))
        {
            try { b.LastValue = Runtime.EvaluateInFrame(frame, b.Expression!); b.Primed = true; }
            catch (VfpException) { }
        }
        if (action == DebugAction.Cancel)
        {
            _mode = DebugAction.Continue;
            throw new CancelProgramException();
        }
    }

    internal void OnEvent(VfpObject o, string eventName)
    {
        if (EventTracking) Output($"{DateTime.Now:HH:mm:ss.fff} {PathOf(o)}.{eventName}");
    }

    private static string PathOf(VfpObject o)
    {
        var parts = new List<string>();
        for (var p = o; p != null; p = p.Parent) parts.Insert(0, p.Name);
        return string.Join(".", parts);
    }

    public void Output(string text)
    {
        if (Host != null) Host.Output(text);
        else Runtime.Notify(text);
    }

    // ---- Variable display ----------------------------------------------------------------

    internal VariableInfo Describe(string name, Variable v, string scope)
    {
        if (v.IsArray)
        {
            var a = v.Array!;
            return new VariableInfo(name, a.TwoDimensional ? $"Array [{a.Rows}, {a.Cols}]" : $"Array [{a.Rows}]", "A", scope, Handle(a));
        }
        return DescribeValue(name, v.Value, scope);
    }

    private VariableInfo DescribeValue(string name, Value value, string scope)
    {
        if (value.Kind == ValueKind.Object && value.AsObject is VfpObject o)
            return new VariableInfo(name, $"({o.Class.Name}) {o.Name}", "O", scope, Handle(o));
        var text = value.Kind switch
        {
            ValueKind.Character => "\"" + value.AsString + "\"",
            ValueKind.Number => Formatter.FormatNumber(value.AsNumber, value.Decimals, Runtime.Options),
            _ => Formatter.ToDisplay(value, Runtime.Options),
        };
        return new VariableInfo(name, text, value.VarType.ToString(), scope, 0);
    }

    private int Handle(object o)
    {
        foreach (var (k, v) in _handles) if (ReferenceEquals(v, o)) return k;
        var h = _nextHandle++;
        _handles[h] = o;
        return h;
    }

    internal IReadOnlyList<VariableInfo> ChildrenOf(int reference)
    {
        if (!_handles.TryGetValue(reference, out var target)) return [];
        var list = new List<VariableInfo>();
        switch (target)
        {
            case VfpArray a:
                for (int i = 1; i <= a.Length && i <= 1000; i++)
                {
                    var (r, c) = a.Subscript(i);
                    list.Add(DescribeValue(a.TwoDimensional ? $"[{r}, {c}]" : $"[{r}]", a[i], "Element"));
                }
                break;
            case VfpObject o:
                if (o.Items != null)
                    for (int i = 0; i < o.Items.Count; i++)
                        list.Add(DescribeValue($"Item({i + 1}){(o.Items[i].Key != null ? " \"" + o.Items[i].Key + "\"" : "")}", o.Items[i].Value, "Item"));
                foreach (var (k, v) in o.Properties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                    list.Add(Describe(k, v, "Property"));
                break;
        }
        return list;
    }
}

/// <summary>CANCEL, or Stop in the debugger: abandons the running program and returns to the Command Window.</summary>
public sealed class CancelProgramException() : Exception("Program canceled.");
