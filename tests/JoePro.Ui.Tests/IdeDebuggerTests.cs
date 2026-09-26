using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using JoePro.Ide;
using JoePro.Runtime;

namespace JoePro.Ui.Tests;

public class IdeDebuggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "joepro-ide-dbg", Guid.NewGuid().ToString("N"));
    private readonly IdeSession _session;
    private readonly MainWindow _window;

    public IdeDebuggerTests()
    {
        Directory.CreateDirectory(_dir);
        _session = new IdeSession(_dir);
        _window = new MainWindow(_session);
        _window.Show();
        _session.Execute("SET TALK OFF");
    }

    public void Dispose()
    {
        _window.Close();
        _session.Dispose();
    }

    [AvaloniaFact]
    public void Breakpoint_pauses_shows_panes_steps_and_continues()
    {
        var path = Path.Combine(_dir, "loop.prg");
        File.WriteAllText(path, "LOCAL n\nn = 0\nFOR i = 1 TO 3\n   n = n + i\nENDFOR\n? 'total', n");
        var editor = _window.OpenFile(path);
        _window.Debugger.Engine.AddLineBreakpoint(path, 4);
        _window.Debugger.AddWatch("n * 100");

        var observations = new List<string>();
        void Step1()
        {
            var dbg = _window.Debugger;
            observations.Add($"paused={dbg.IsPaused} line={dbg.CurrentStop!.Top.Line} exec={editor.ExecutionLine.Line}");
            var locals = ((IEnumerable<VariableNode>)_window.Debugger.Panel.Locals.ItemsSource!).Select(n => n.Info.Name + "=" + n.Info.Value);
            observations.Add(string.Join(",", locals));
            observations.Add(string.Join(",", ((IEnumerable<VariableNode>)_window.Debugger.Panel.WatchTree.ItemsSource!).Select(n => n.Info.Value)));
            observations.Add(string.Join("|", (IEnumerable<string>)_window.Debugger.Panel.Stack.ItemsSource!));
            // Commands typed while suspended see the program's variables.
            _session.Execute("? 'inspect', n");
            _window.Debugger.Engine.Breakpoints.Clear();
            Dispatcher.UIThread.Post(Step2);
            dbg.Resume(DebugAction.StepOver);
        }
        void Step2()
        {
            observations.Add($"step line={_window.Debugger.CurrentStop?.Top.Line}");
            _window.Debugger.Resume(DebugAction.Continue);
        }
        Dispatcher.UIThread.Post(Step1);
        _window.RunActive();

        Assert.Equal("paused=True line=4 exec=4", observations[0]);
        Assert.Contains("n=0", observations[1]);
        Assert.Equal("0", observations[2]);
        Assert.StartsWith("LOOP  line 4  loop.prg", observations[3]);
        Assert.Equal("step line=4", observations[4]); // next loop iteration
        Dispatcher.UIThread.RunJobs();
        var screen = _window.Screen.Box.Text ?? "";
        Assert.Contains("inspect          0", screen);
        Assert.Contains("total          6", screen);
        Assert.False(_window.Debugger.IsPaused);
        Assert.Equal(0, editor.ExecutionLine.Line);
    }

    [AvaloniaFact]
    public void Stop_cancels_the_program_and_debugout_reaches_the_output_pane()
    {
        var path = Path.Combine(_dir, "stop.prg");
        File.WriteAllText(path, "DEBUGOUT 'hello debugger'\nSUSPEND\n? 'after'");
        _window.OpenFile(path);
        Dispatcher.UIThread.Post(() => _window.Debugger.Resume(DebugAction.Cancel));
        _window.RunActive();
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain("after", _window.Screen.Box.Text ?? "");
        Assert.Contains("hello debugger", _window.Debugger.Panel.OutputText);
    }
}
