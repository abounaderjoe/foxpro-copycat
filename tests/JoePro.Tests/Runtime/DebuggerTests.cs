using JoePro.Runtime;

namespace JoePro.Tests.Runtime;

public class DebuggerTests : RuntimeHarness
{
    private sealed class ScriptedHost(Func<DebugStop, DebugAction> onStop) : IDebugHost
    {
        public List<string> Output { get; } = new();
        public DebugAction Paused(DebugStop stop) => onStop(stop);
        void IDebugHost.Output(string text) => Output.Add(text);
    }

    private string WriteProgram(string name, string code)
    {
        var path = Path.Combine(Dir, name);
        File.WriteAllText(path, code.Replace("\r\n", "\n"));
        return path;
    }

    private const string Program = """
        LOCAL total
        total = 0
        FOR i = 1 TO 3
           total = total + Square(i)
        ENDFOR
        ? total
        FUNCTION Square(n)
           LOCAL r
           r = n * n
           RETURN r
        ENDFUNC
        """;

    [Fact]
    public void Line_breakpoint_stops_and_shows_locals_and_call_stack()
    {
        var path = WriteProgram("calc.prg", Program);
        var stops = new List<(int Line, string Frames, string Locals, string Watch)>();
        var dbg = new Debugger(Rt)
        {
            Host = new ScriptedHost(stop =>
            {
                stops.Add((stop.Top.Line,
                    string.Join(">", stop.Frames.Select(f => f.Name)),
                    string.Join(",", stop.Locals(0).Select(v => $"{v.Name}={v.Value}")),
                    stop.Evaluate("n * 10").Value));
                return DebugAction.Continue;
            }),
        };
        dbg.AddLineBreakpoint(path, 10);
        var o = Run($"DO \"{path}\"");
        Assert.Equal("        14", o);
        Assert.Equal(3, stops.Count);
        Assert.Equal(10, stops[0].Line);
        Assert.Equal("SQUARE>CALC>(command)", stops[0].Frames);
        Assert.Equal("n=1,r=1", stops[0].Locals);
        Assert.Equal("30", stops[2].Watch);
    }

    [Fact]
    public void Conditional_breakpoint_and_hit_count()
    {
        var path = WriteProgram("calc.prg", Program);
        var lines = new List<int>();
        var dbg = new Debugger(Rt) { Host = new ScriptedHost(stop => { lines.Add((int)Rt.EvaluateInFrame(Rt.CurrentFrame, "0").AsNumber); return DebugAction.Continue; }) };
        var n = new List<string>();
        dbg.Host = new ScriptedHost(stop => { n.Add(stop.Evaluate("n").Value); return DebugAction.Continue; });
        dbg.AddLineBreakpoint(path, 9, condition: "n = 2");
        Run($"DO \"{path}\"");
        Assert.Equal(["2"], n);
    }

    [Fact]
    public void Step_over_into_and_out()
    {
        var path = WriteProgram("calc.prg", Program);
        var trail = new List<string>();
        var actions = new Queue<DebugAction>([DebugAction.StepOver, DebugAction.StepOver, DebugAction.StepInto, DebugAction.StepInto, DebugAction.StepOut, DebugAction.Continue]);
        var dbg = new Debugger(Rt)
        {
            Host = new ScriptedHost(stop =>
            {
                trail.Add($"{stop.Top.Name}:{stop.Top.Line}");
                return actions.Dequeue();
            }),
        };
        dbg.AddLineBreakpoint(path, 2);
        Run($"DO \"{path}\"");
        // 2 → over → 3 (FOR) → over → 4 → into → Square line 8 → into → 9 → out → back in CALC
        Assert.Equal(["CALC:2", "CALC:3", "CALC:4", "SQUARE:8", "SQUARE:9", "CALC:4"], trail);
    }

    [Fact]
    public void Break_when_true_and_on_change()
    {
        var path = WriteProgram("calc.prg", Program);
        var seen = new List<string>();
        var dbg = new Debugger(Rt) { Host = new ScriptedHost(stop => { seen.Add($"{stop.Breakpoint!.Kind}:{stop.Evaluate("total").Value}"); return DebugAction.Continue; }) };
        dbg.AddExpressionBreakpoint("TYPE('total') = 'N' AND total > 4", onChange: false);
        Run($"DO \"{path}\"");
        Assert.NotEmpty(seen);
        Assert.StartsWith("WhenTrue:5", seen[0]);

        seen.Clear();
        dbg.Breakpoints.Clear();
        dbg.AddExpressionBreakpoint("m.total", onChange: true);
        Run($"DO \"{path}\"");
        Assert.Contains("OnChange:1", seen);
        Assert.Contains("OnChange:14", seen);
    }

    [Fact]
    public void Suspend_and_set_step_on_stop_and_cancel_aborts_the_program()
    {
        var reasons = new List<StopReason>();
        new Debugger(Rt) { Host = new ScriptedHost(stop => { reasons.Add(stop.Reason); return reasons.Count == 1 ? DebugAction.Continue : DebugAction.Cancel; }) };
        var o = Run("""
            ? "before"
            SUSPEND
            ? "middle"
            SET STEP ON
            ? "never printed"
            """);
        Assert.Equal([StopReason.Suspend, StopReason.Suspend], reasons);
        Assert.Equal("before\nmiddle", o);
        Assert.Contains(StatusMessages, m => m.Contains("canceled"));
    }

    [Fact]
    public void Cancel_statement_stops_the_program_without_quitting()
    {
        var o = Run("? 'a'\nCANCEL\n? 'b'");
        Assert.Equal("a", o);
        Assert.Equal("ok", Eval("'ok'"));
    }

    [Fact]
    public void Arrays_and_objects_can_be_expanded()
    {
        IReadOnlyList<VariableInfo>? locals = null;
        IReadOnlyList<VariableInfo>? arr = null, obj = null;
        new Debugger(Rt)
        {
            Host = new ScriptedHost(stop =>
            {
                locals = stop.Locals(0);
                arr = stop.Children(locals.Single(v => v.Name == "aList").ChildrenRef);
                obj = stop.Children(locals.Single(v => v.Name == "oPerson").ChildrenRef);
                return DebugAction.Continue;
            }),
        };
        Run("""
            PRIVATE aList, oPerson
            DIMENSION aList(2)
            aList(1) = "x"
            aList(2) = 5
            oPerson = CREATEOBJECT("Empty")
            =ADDPROPERTY(oPerson, "Name", "Ann")
            SUSPEND
            """);
        Assert.Equal("Array [2]", locals!.Single(v => v.Name == "aList").Value);
        Assert.Equal(["[1]=\"x\"", "[2]=5"], arr!.Select(v => $"{v.Name}={v.Value}"));
        Assert.Contains(obj!, v => v.Name == "Name" && v.Value == "\"Ann\"");
    }

    [Fact]
    public void Break_on_unhandled_error_shows_the_failing_frame()
    {
        var path = WriteProgram("err.prg", "x = 1\n=Boom()\nFUNCTION Boom\n   RETURN 1 / 0\nENDFUNC");
        DebugStop? stop = null;
        new Debugger(Rt) { BreakOnErrors = true, Host = new ScriptedHost(s => { stop = s; return DebugAction.Continue; }) };
        Assert.Throws<JoePro.Core.VfpException>(() => Run($"DO \"{path}\""));
        Assert.Equal(StopReason.Exception, stop!.Reason);
        Assert.Equal("BOOM", stop.Top.Name);
        Assert.Equal(4, stop.Top.Line);
        Assert.Contains("Division by zero", stop.Message);
    }

    [Fact]
    public void Event_tracking_debugout_and_coverage()
    {
        var host = new ScriptedHost(_ => DebugAction.Continue);
        new Debugger(Rt) { Host = host };
        Run("""
            SET EVENTTRACKING ON
            o = CREATEOBJECT("Custom")
            SET EVENTTRACKING OFF
            DEBUGOUT "value:", 1 + 1
            SET COVERAGE TO cov.log
            x = 1
            y = 2
            SET COVERAGE TO
            """);
        Assert.Contains(host.Output, l => l.EndsWith("Custom.Init"));
        Assert.Contains("value: 2", host.Output);
        var cov = File.ReadAllLines(Path.Combine(Dir, "cov.log"));
        Assert.True(cov.Length >= 2);
        Assert.All(cov, l => Assert.Equal(6, l.Split(',').Length));
    }

    [Fact]
    public void Set_next_statement_skips_forward_and_jumps_out_of_a_loop()
    {
        var path = WriteProgram("jump.prg", """
            x = 1
            x = 2
            x = 3
            FOR i = 1 TO 5
               x = x + 10
            ENDFOR
            ? x, i
            """);
        var reasons = new List<string>();
        var dbg = new Debugger(Rt)
        {
            Host = new ScriptedHost(stop =>
            {
                reasons.Add($"{stop.Reason}@{stop.Top.Line}");
                if (stop.Top.Line == 2) { Assert.True(stop.SetNextStatement(4)); return DebugAction.Goto; } // skip x = 2 and x = 3
                if (stop.Top.Line == 5 && stop.Reason == StopReason.Breakpoint)
                {
                    Assert.False(stop.SetNextStatement(99)); // not a statement
                    Assert.True(stop.SetNextStatement(7));   // leave the loop after one pass
                    return DebugAction.Goto;
                }
                return DebugAction.Continue;
            }),
        };
        dbg.AddLineBreakpoint(path, 2);
        dbg.AddLineBreakpoint(path, 5);
        var o = Run($"DO \"{path}\"");
        Assert.Equal("         1          1", o); // lines 2, 3 and the loop body never ran
        Assert.Equal(["Breakpoint@2", "Goto@4", "Breakpoint@5", "Goto@7"], reasons);
    }
}
